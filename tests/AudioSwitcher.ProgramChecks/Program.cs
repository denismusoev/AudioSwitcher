using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioSwitcher.Core;
using AudioSwitcher.Platform;

int passed = 0, failed = 0, skipped = 0;
async Task Check(string name, Func<Task> test) { try { await test(); Console.WriteLine($"PASS {name}"); passed++; } catch (Exception e) { Console.WriteLine($"FAIL {name}: {e}"); failed++; } }
void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
string configuration = AppContext.BaseDirectory.Contains("Release") ? "Release" : "Debug";
string fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, $"../../../../AudioSwitcher.ProgramFixture/bin/{configuration}/net10.0-windows/AudioSwitcher.ProgramFixture.exe"));
var owned = new List<Process>();
string signal = Path.Combine(Path.GetTempPath(), $"AudioSwitcher-fixture-{Guid.NewGuid():N}");
string shortcutPath = Path.Combine(Path.GetTempPath(), $"AudioSwitcher shortcut {Guid.NewGuid():N}.lnk");
var windows = new ProgramWindowService(); var processes = new ProgramProcessService();
async Task<RunningProgram> Start(string suffix, string extra = "")
{
    var process = Process.Start(new ProcessStartInfo(fixture) { Arguments = $"\"AudioSwitcher fixture {suffix}\" {extra} --grant={Environment.ProcessId}", UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })!;
    owned.Add(process);
    for (int i = 0; i < 100; i++)
    {
        var program = (await windows.GetProgramsAsync()).FirstOrDefault(p => p.Identity.Pid == process.Id);
        if (program != null && (!extra.Contains("--multiple") || program.Windows.Count == 2)) return program;
        await Task.Delay(100);
    }
    throw new Exception("Fixture window not discovered");
}
try
{
    async Task<RunningProgram> WaitForLaunched(string title)
    {
        for (int i = 0; i < 100; i++)
        {
            var found = (await windows.GetProgramsAsync()).FirstOrDefault(p => p.Windows.Any(w => w.Title == title));
            if (found != null)
            {
                var process = Process.GetProcessById(found.Identity.Pid);
                _ = process.Handle;
                Require(process.ProcessName == "AudioSwitcher.ProgramFixture" && process.StartTime.ToUniversalTime().ToFileTimeUtc() == found.Identity.Created, "Launched instance changed");
                owned.Add(process); return found;
            }
            await Task.Delay(100);
        }
        throw new Exception("ShellExecute fixture was not discovered");
    }
    await Check("Launch service opens own EXE with separate Unicode arguments", async () => {
        string title = "AudioSwitcher fixture Прямой запуск " + Guid.NewGuid().ToString("N");
        await new ProgramLaunchService().LaunchAsync(new(Guid.NewGuid(), "Fixture", LaunchKind.Executable, fixture, $"\"{title}\"", Path.GetDirectoryName(fixture)!));
        var found = await WaitForLaunched(title);
        Require(found.Windows[0].Title == title, "Arguments were not delivered");
    });
    await Check("Launch service opens own LNK using shortcut arguments", async () => {
        string title = "AudioSwitcher fixture Ярлык " + Guid.NewGuid().ToString("N");
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        object? shortcut = null;
        try
        {
            dynamic automation = shell; shortcut = automation.CreateShortcut(shortcutPath);
            dynamic link = shortcut; link.TargetPath = fixture; link.Arguments = $"\"{title}\"";
            link.WorkingDirectory = Path.GetDirectoryName(fixture); link.Save();
        }
        finally { if (shortcut != null) Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
        await new ProgramLaunchService().LaunchAsync(new(Guid.NewGuid(), "Shortcut fixture", LaunchKind.Shortcut, shortcutPath));
        Require((await WaitForLaunched(title)).Windows[0].Title == title, "Shortcut arguments lost");
    });
    await Check("Independent user windows are discovered without a launch catalog", async () => {
        var first = await Start("one"); var second = await Start("two");
        Require(first.Identity != second.Identity, "Separate processes were merged");
        Require(first.Windows.Count == 1 && first.Windows[0].Title.Contains("one"), "Wrong window");
    });
    await Check("Multiple windows are grouped by verified process instance", async () => {
        var program = await Start("multiple", "--multiple");
        Require(program.Windows.Count == 2, $"Expected two windows, got {program.Windows.Count}");
    });
    await Check("Changed creation time is rejected without killing the process", async () => {
        var program = await Start("stale");
        var result = await processes.TerminateAsync(program.Identity with { Created = program.Identity.Created + 1 });
        Require(result.Code == ProgramResultCode.TargetChanged, result.Message);
        Require(!owned.Last().HasExited, "Mismatched process was killed");
    });
    await Check("Terminating one instance preserves another identical EXE", async () => {
        var first = await Start("kill target"); var target = owned.Last();
        _ = await Start("survivor"); var survivor = owned.Last();
        var result = await processes.TerminateAsync(first.Identity);
        Require(result.Succeeded && target.HasExited, result.Message);
        Require(!survivor.HasExited, "Another instance was killed");
        result = await processes.TerminateAsync(first.Identity);
        Require(result.Code == ProgramResultCode.AlreadyExited, "Closed target was not handled");
    });
    await Check("Hung user window remains discoverable and can be forcibly terminated", async () => {
        var program = await Start("hung", $"--signal={signal}"); var target = owned.Last();
        File.WriteAllText(signal, "hang"); await Task.Delay(800);
        var snapshot = await windows.GetProgramsAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Require(snapshot.Any(p => p.Identity == program.Identity), "Hung fixture vanished from list");
        var result = await processes.TerminateAsync(program.Identity);
        Require(result.Succeeded && target.HasExited, result.Message);
    });
    await Check("Mismatched window owner rejects move without touching another window", async () => {
        var first = await Start("move owner"); var second = await Start("different owner");
        var result = await new ProgramWindowMover().MoveToPrimaryAsync(first.Windows[0] with { Handle = second.Windows[0].Handle });
        Require(result.Code == ProgramResultCode.TargetChanged, result.Message);
    });
    if (args.Contains("--move-fixture"))
    {
        var monitors = FixtureNative.Monitors();
        var other = monitors.FirstOrDefault(m => (m.Flags & 1) == 0);
        if (other.Size == 0) { skipped++; Console.WriteLine("SKIP Move fixture: second monitor not available"); }
        else foreach (string mode in new[] { "", "--minimized", "--maximized", "--borderless", "--dpi-unaware", "--system-aware", "--maximized --minimized" })
        await Check($"Owned fixture moves from second monitor to primary ({mode})", async () => {
            var program = await Start("move " + mode, mode); var window = program.Windows[0];
            if (mode == "--borderless") FixtureNative.SetWindowPos(window.Handle, 0, other.Monitor.Left, other.Monitor.Top, other.Monitor.Right - other.Monitor.Left, other.Monitor.Bottom - other.Monitor.Top, 0x4014);
            else
            {
                FixtureNative.ShowWindowAsync(window.Handle, 4);
                FixtureNative.SetWindowPos(window.Handle, 0, other.Work.Left + 80, other.Work.Top + 80, 640, 400, 0x4014);
                await Task.Delay(300);
                if (mode.Contains("--maximized")) { FixtureNative.ShowWindowAsync(window.Handle, 3); await Task.Delay(200); }
                if (mode.Contains("--minimized")) FixtureNative.ShowWindowAsync(window.Handle, 6);
            }
            await Task.Delay(500);
            Require(FixtureNative.MonitorFromWindow(window.Handle, 2) != FixtureNative.MonitorFromPoint(new(0, 0), 1), "Fixture did not reach second monitor before test");
            var observer = await Start("focus observer");
            for (int i = 0; i < 10 && FixtureNative.GetForegroundWindow() != observer.Windows[0].Handle; i++)
            {
                await Task.Delay(100); FixtureNative.SetForegroundWindow(observer.Windows[0].Handle);
            }
            await Task.Delay(100);
            nint foreground = FixtureNative.GetForegroundWindow();
            Require(foreground != window.Handle, "Test window must be in background before move");
            var result = await new ProgramWindowMover().MoveToPrimaryAsync(window);
            Require(result.Succeeded, result.Message + ": " + result.Details);
            Require(FixtureNative.GetForegroundWindow() == foreground, "Move stole input focus from the controlling window");
            Require(FixtureNative.MonitorFromWindow(window.Handle, 2) == FixtureNative.MonitorFromPoint(new(0, 0), 1), "Not on primary");
            if (mode.Contains("--maximized")) Require(FixtureNative.IsZoomed(window.Handle), "Maximized state lost");
            Require(!FixtureNative.IsIconic(window.Handle), "Minimized window was not restored");
            FixtureNative.GetWindowRect(window.Handle, out var firstBounds);
            result = await new ProgramWindowMover().MoveToPrimaryAsync(window);
            Require(result.Succeeded, "Repeated primary move failed: " + result.Message);
            FixtureNative.GetWindowRect(window.Handle, out var repeatedBounds);
            Require(Math.Abs((firstBounds.Right - firstBounds.Left) - (repeatedBounds.Right - repeatedBounds.Left)) <= 3 &&
                Math.Abs((firstBounds.Bottom - firstBounds.Top) - (repeatedBounds.Bottom - repeatedBounds.Top)) <= 3, "Repeated move to the same primary changed window size");
        });
    }
    else { skipped++; Console.WriteLine("SKIP Hardware move: use --move-fixture; no primary display changes are made"); }
}
finally
{
    foreach (var process in owned) { try { if (!process.HasExited) { process.Kill(); process.WaitForExit(3000); } } finally { process.Dispose(); } }
    if (File.Exists(signal)) File.Delete(signal);
    if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
}
Console.WriteLine($"Passed: {passed}, Failed: {failed}, Skipped: {skipped}");
return failed == 0 ? 0 : 1;

internal static class FixtureNative
{
    [StructLayout(LayoutKind.Sequential)] public record struct Point(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    private delegate bool Callback(nint monitor, nint dc, nint rect, nint data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint rect, Callback callback, nint data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] public static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] public static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] public static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(nint window, out Rect rect);
    public static List<MonitorInfo> Monitors()
    {
        var result = new List<MonitorInfo>();
        EnumDisplayMonitors(0, 0, (m, _, _, _) => { var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() }; if (GetMonitorInfo(m, ref info)) result.Add(info); return true; }, 0);
        return result;
    }
}
