param([string]$ExePath = "$PSScriptRoot/../src/AudioSwitcher/bin/Release/net10.0-windows/AudioSwitcher.exe", [switch]$ScrollComparison)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SmokeNative {
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int command);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
}
'@
$app = Start-Process -FilePath (Resolve-Path -LiteralPath $ExePath) -WindowStyle Hidden -PassThru
try {
    $app.WaitForInputIdle(10000) | Out-Null
    Start-Sleep -Milliseconds 700
    $app.Refresh()
    [SmokeNative]::ShowWindow($app.MainWindowHandle, 5) | Out-Null
    [SmokeNative]::SetForegroundWindow($app.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 500
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
    if ($null -eq $window) { throw 'Main window missing' }
    if (([SmokeNative]::GetWindowLong($app.MainWindowHandle, -20) -band 8) -eq 0) { throw 'Window is not topmost' }
    Write-Output 'PASS Window is topmost'
    function Find-Control([string]$Id) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
        $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -eq $element) { throw "Missing control: $Id" }
        return $element
    }
    function Invoke-Control([string]$Id) {
        $element = Find-Control $Id
        $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $pattern.Invoke()
        Start-Sleep -Milliseconds 150
    }
    function Assert-Status([string]$Text) {
        $element = Find-Control 'Status'
        if (-not $element.Current.Name.Contains($Text)) { throw "Unexpected status: $($element.Current.Name)" }
    }
    $artifactPath = Join-Path $PSScriptRoot '../artifacts'
    New-Item -ItemType Directory -Path $artifactPath -Force | Out-Null
    function Save-ProgramImage([string]$Name) {
        $programBounds = New-Object SmokeNative+Rect
        [SmokeNative]::GetWindowRect($app.MainWindowHandle, [ref]$programBounds) | Out-Null
        $programShot = New-Object System.Drawing.Bitmap(($programBounds.Right - $programBounds.Left), ($programBounds.Bottom - $programBounds.Top))
        $programCanvas = [System.Drawing.Graphics]::FromImage($programShot)
        try { $programCanvas.CopyFromScreen($programBounds.Left, $programBounds.Top, 0, 0, $programShot.Size); $programShot.Save((Join-Path $artifactPath $Name)) }
        finally { $programCanvas.Dispose(); $programShot.Dispose() }
    }
    function Send-SmokeKey([int]$Key) {
        [SmokeNative]::PostMessage($app.MainWindowHandle, 0x100, [IntPtr]$Key, [IntPtr]0) | Out-Null
        [SmokeNative]::PostMessage($app.MainWindowHandle, 0x101, [IntPtr]$Key, [IntPtr]0) | Out-Null
        Start-Sleep -Milliseconds 180
    }
    Find-Control 'Devices' | Out-Null
    foreach ($removedId in @('ApplyButton', 'CloseButton', 'PadStatus', 'SectionLabel')) {
        $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $removedId)
        if ($null -ne $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)) { throw "Control must be absent: $removedId" }
    }
    if (-not (Find-Control 'ApplyHint').Current.Name.Contains('A / Enter')) { throw 'Apply hint missing' }
    Write-Output 'PASS Extra buttons, controller status and section label are absent; A / Enter hint is present'
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'AudioSwitcher')
    if ($null -ne $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCondition)) { throw 'Application heading must be absent' }
    Write-Output 'PASS Application heading is absent'
    Assert-Status 'Готово'
    if ((Find-Control 'Devices').Current.Name -ne 'Устройства вывода') { throw 'Audio section list label missing' }
    Write-Output 'PASS Audio section loaded'
    Invoke-Control 'DisplayTab'
    Assert-Status 'Готово'
    if ((Find-Control 'Devices').Current.Name -ne 'Мониторы') { throw 'Monitor section list label missing' }
    Write-Output 'PASS Display section loaded'
    Invoke-Control 'AudioTab'
    Assert-Status 'Готово'
    if ((Find-Control 'Devices').Current.Name -ne 'Устройства вывода') { throw 'Return to audio section failed' }
    Write-Output 'PASS Return to audio section'
    Invoke-Control 'ProgramsTab'
    Start-Sleep -Milliseconds 500
    Find-Control 'RunningList' | Out-Null
    Invoke-Control 'LaunchMode'
    if ((Find-Control 'LaunchList').Current.IsOffscreen) { throw 'Launch list must be visible' }
    $runningCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'RunningList')
    $hiddenRunningList = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $runningCondition)
    if ($null -ne $hiddenRunningList -and -not $hiddenRunningList.Current.IsOffscreen) { throw 'Running and launch lists must be separated' }
    Find-Control 'AddProgram' | Out-Null
    Save-ProgramImage 'ui-programs-launch.png'
    Invoke-Control 'RunningMode'
    if ((Find-Control 'RunningList').Current.IsOffscreen) { throw 'Running list must be visible' }
    Write-Output 'PASS Programs section separates running applications and manual launch catalog'
    Save-ProgramImage 'ui-programs-running.png'
    $programList = Find-Control 'RunningList'
    $programItemCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    if ($programList.FindAll([System.Windows.Automation.TreeScope]::Children, $programItemCondition).Count -gt 0) {
        $programList.SetFocus()
        Send-SmokeKey 13
        $programActions = Find-Control 'ProgramActions'
        Save-ProgramImage 'ui-programs-actions.png'
        $actionRows = $programActions.FindAll([System.Windows.Automation.TreeScope]::Children, $programItemCondition)
        $actionRows[1].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        Send-SmokeKey 13
        $confirmation = Find-Control 'ProgramActions'
        $defaultChoice = $confirmation.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()[0]
        if ($defaultChoice.Current.Name -ne 'Отмена') { throw 'Termination must default to Cancel' }
        Save-ProgramImage 'ui-programs-confirm.png'
        Send-SmokeKey 13
        if ((Find-Control 'PanelDescription').Current.Name -ne 'Выберите действие') { throw 'Default confirmation did not cancel' }
        Send-SmokeKey 27
        Find-Control 'RunningList' | Out-Null
        if ($app.HasExited) { throw 'Esc must return from actions before closing app' }
        Write-Output 'PASS Termination defaults to Cancel; Enter cancels and Esc returns from actions'
    } else { Write-Output 'SKIP Program action UI: no user application windows available' }
    Invoke-Control 'AudioTab'
    $artifactPath = Join-Path $PSScriptRoot '../artifacts'
    New-Item -ItemType Directory -Path $artifactPath -Force | Out-Null
    if ($ScrollComparison) {
        $deviceList = Find-Control 'Devices'
        $deviceList.SetFocus()
        $itemCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
        $itemCount = $deviceList.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCondition).Count
        function Send-Navigation([int]$Key) {
            [SmokeNative]::PostMessage($app.MainWindowHandle, 0x100, [IntPtr]$Key, [IntPtr]0) | Out-Null
            [SmokeNative]::PostMessage($app.MainWindowHandle, 0x101, [IntPtr]$Key, [IntPtr]0) | Out-Null
            Start-Sleep -Milliseconds 60
        }
        function Save-ScrollImage([string]$Name) {
            $bounds = New-Object SmokeNative+Rect
            [SmokeNative]::GetWindowRect($app.MainWindowHandle, [ref]$bounds) | Out-Null
            $shot = New-Object System.Drawing.Bitmap(($bounds.Right - $bounds.Left), ($bounds.Bottom - $bounds.Top))
            $canvas = [System.Drawing.Graphics]::FromImage($shot)
            try { $canvas.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $shot.Size); $shot.Save((Join-Path $artifactPath $Name)) }
            finally { $canvas.Dispose(); $shot.Dispose() }
        }
        for ($i = 0; $i -le $itemCount; $i++) { Send-Navigation 40 }
        for ($i = 0; $i -lt 3; $i++) { Send-Navigation 38 }
        Save-ScrollImage 'ui-scroll-end.png'
        Send-Navigation 38
        Save-ScrollImage 'ui-scroll-up.png'
        Write-Output 'PASS Scroll comparison images captured after navigating to end and back'
    }
    $rect = New-Object SmokeNative+Rect
    [SmokeNative]::GetWindowRect($app.MainWindowHandle, [ref]$rect) | Out-Null
    $bitmap = New-Object System.Drawing.Bitmap(($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
    $bitmap.Save((Join-Path $artifactPath 'ui.png'))
    $graphics.Dispose(); $bitmap.Dispose()
    $scale = [SmokeNative]::GetDpiForWindow($app.MainWindowHandle) / 96.0
    [SmokeNative]::SetWindowPos($app.MainWindowHandle, [IntPtr]::Zero, 0, 0, [int](340 * $scale), [int](400 * $scale), 6) | Out-Null
    Start-Sleep -Milliseconds 200
    Invoke-Control 'ProgramsTab'
    Start-Sleep -Milliseconds 400
    Save-ProgramImage 'ui-programs-running-small.png'
    Invoke-Control 'LaunchMode'
    Save-ProgramImage 'ui-programs-launch-small.png'
    Invoke-Control 'AudioTab'
    [SmokeNative]::GetWindowRect($app.MainWindowHandle, [ref]$rect) | Out-Null
    $bitmap = New-Object System.Drawing.Bitmap(($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
    $bitmap.Save((Join-Path $artifactPath 'ui-small.png'))
    $graphics.Dispose(); $bitmap.Dispose()
    [SmokeNative]::PostMessage($app.MainWindowHandle, 0x100, [IntPtr]27, [IntPtr]0) | Out-Null
    [SmokeNative]::PostMessage($app.MainWindowHandle, 0x101, [IntPtr]27, [IntPtr]0) | Out-Null
    if (-not $app.WaitForExit(3000)) { throw 'Esc did not close process' }
    if ($app.ExitCode -ne 0) { throw "App exited with code $($app.ExitCode)" }
    Write-Output 'PASS Esc closes process'
} finally { if (-not $app.HasExited) { $app.CloseMainWindow() | Out-Null; $app.WaitForExit(2000) | Out-Null } }
exit 0
