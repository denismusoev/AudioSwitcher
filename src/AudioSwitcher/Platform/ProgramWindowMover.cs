using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioSwitcher.Core;

namespace AudioSwitcher.Platform;

public sealed class ProgramWindowMover
{
    public Task<ProgramResult> MoveToPrimaryAsync(WindowTarget target, CancellationToken cancellationToken = default) =>
        Task.Run(() => {
            nint previousDpi = ProgramNative.SetThreadDpiAwarenessContext(-4);
            try { return Move(target, cancellationToken); }
            finally { if (previousDpi != 0) ProgramNative.SetThreadDpiAwarenessContext(previousDpi); }
        }, cancellationToken);

    private static ProgramResult Move(WindowTarget target, CancellationToken cancellationToken)
    {
        if (target.Process.Pid == Environment.ProcessId) return new(ProgramResultCode.Unsupported, "Это окно нельзя переместить из списка программ");
        using var process = ProgramNative.OpenProcess(ProgramNative.Query | ProgramNative.Synchronize, false, target.Process.Pid);
        if (process.IsInvalid) return ProgramNative.Error(Marshal.GetLastWin32Error());
        if (ProgramNative.Identity(process, target.Process.Pid) != target.Process || !ProgramNative.WindowMatches(target))
            return new(ProgramResultCode.TargetChanged, "Выбранное окно изменилось или уже закрыто");
        if (!ProgramNative.SameUserSession(process, target.Process.Pid)) return new(ProgramResultCode.AccessDenied, "Окно другого пользователя недоступно");
        var snapshot = ProgramWindowService.GetPrograms(cancellationToken);
        if (!snapshot.Any(p => p.Identity == target.Process && p.Windows.Any(w => w.Handle == target.Handle)))
            return new(ProgramResultCode.TargetChanged, "Пользовательское окно больше недоступно");
        if (!ProgramNative.GetWindowRect(target.Handle, out var original)) return ProgramNative.Error(Marshal.GetLastWin32Error());
        nint primary = ProgramNative.MonitorFromPoint(new(0, 0), 1);
        var destination = ProgramNative.Monitor(primary);
        nint sourceMonitor = ProgramNative.MonitorFromWindow(target.Handle, 2);
        var source = ProgramNative.Monitor(sourceMonitor);
        // GetWindowRect is physical in this worker's PMv2 context, even for unaware windows.
        // The source monitor DPI must use the same physical coordinate basis.
        if (ProgramNative.GetDpiForMonitor(sourceMonitor, 0, out uint sourceDpi, out _) != 0) sourceDpi = 96;
        if (ProgramNative.GetDpiForMonitor(primary, 0, out uint targetDpi, out _) != 0) targetDpi = 96;
        bool maximized = ProgramNative.IsZoomed(target.Handle), minimized = ProgramNative.IsIconic(target.Handle);
        var placement = new ProgramNative.Placement { Size = Marshal.SizeOf<ProgramNative.Placement>() };
        if (!ProgramNative.GetWindowPlacement(target.Handle, ref placement)) return ProgramNative.Error(Marshal.GetLastWin32Error());
        maximized |= minimized && (placement.Flags & 2) != 0;
        var bounds = original.Bounds;
        if (maximized || minimized) bounds = placement.Normal.Bounds;
        bool borderless = !maximized && !minimized && (ProgramNative.GetWindowLong(target.Handle, -16) & 0xC00000) == 0 &&
            Math.Abs(original.Left - source.Monitor.Left) <= 3 && Math.Abs(original.Top - source.Monitor.Top) <= 3 &&
            Math.Abs(original.Right - source.Monitor.Right) <= 3 && Math.Abs(original.Bottom - source.Monitor.Bottom) <= 3;
        var desired = ProgramWindowLayout.Calculate(bounds, destination.Work.Bounds, sourceDpi, targetDpi, borderless, destination.Monitor.Bounds);
        if (!ProgramNative.WindowMatches(target) || ProgramNative.WaitForSingleObject(process, 0) == 0)
            return new(ProgramResultCode.TargetChanged, "Выбранное окно уже закрыто");
        cancellationToken.ThrowIfCancellationRequested();
        if (maximized || minimized)
        {
            // WINDOWPLACEMENT uses workspace coordinates. Account for taskbars at top/left.
            int offsetX = destination.Work.Left - destination.Monitor.Left;
            int offsetY = destination.Work.Top - destination.Monitor.Top;
            placement.Normal = new() { Left = desired.Left - offsetX, Top = desired.Top - offsetY,
                Right = desired.Left + desired.Width - offsetX, Bottom = desired.Top + desired.Height - offsetY };
            placement.Flags = 4; // WPF_ASYNCWINDOWPLACEMENT
            placement.Show = maximized ? 3u : 4u; // maximized / restored without activation
            placement.Max = new(-1, -1);
            if (!ProgramNative.SetWindowPlacement(target.Handle, ref placement)) return ProgramNative.Error(Marshal.GetLastWin32Error());
            // A window already maximized can keep its current monitor despite new restore bounds.
            // Move its live rectangle too, while retaining the zoomed state and input focus.
            if (maximized && !ProgramNative.SetWindowPos(target.Handle, 0, destination.Work.Left, destination.Work.Top,
                destination.Work.Right - destination.Work.Left, destination.Work.Bottom - destination.Work.Top, 0x4014))
                return ProgramNative.Error(Marshal.GetLastWin32Error());
        }
        else if (!ProgramNative.SetWindowPos(target.Handle, 0, desired.Left, desired.Top, desired.Width, desired.Height, 0x4014))
            return ProgramNative.Error(Marshal.GetLastWin32Error()); // ASYNCWINDOWPOS | NOZORDER | NOACTIVATE
        var timeout = Stopwatch.StartNew();
        while (timeout.ElapsedMilliseconds < 2000)
        {
            Thread.Sleep(100);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ProgramNative.WindowMatches(target) || ProgramNative.WaitForSingleObject(process, 0) == 0)
                return new(ProgramResultCode.AlreadyExited, "Программа уже закрыта");
            nint currentPrimary = ProgramNative.MonitorFromPoint(new(0, 0), 1);
            var current = ProgramNative.Monitor(currentPrimary);
            if (currentPrimary != primary || current.Device != destination.Device || current.Monitor.Bounds != destination.Monitor.Bounds)
                return new(ProgramResultCode.TargetChanged, "Конфигурация экранов изменилась. Повторите перенос");
            if (ProgramNative.MonitorFromWindow(target.Handle, 2) != primary || ProgramNative.IsIconic(target.Handle)) continue;
            if (maximized && !ProgramNative.IsZoomed(target.Handle)) continue;
            if (!ProgramNative.GetWindowRect(target.Handle, out var actual)) continue;
            var area = borderless ? destination.Monitor : destination.Work;
            // Maximized windows include an invisible resize border beyond the work area.
            int tolerance = maximized ? Math.Max(16, (int)(targetDpi / 6)) : 3;
            if (!ProgramWindowLayout.IsSufficientlyOnScreen(actual.Bounds, area.Bounds, tolerance)) continue;
            if (borderless && (Math.Abs(actual.Left - area.Left) > 3 || Math.Abs(actual.Top - area.Top) > 3 ||
                Math.Abs(actual.Right - area.Right) > 3 || Math.Abs(actual.Bottom - area.Bottom) > 3)) continue;
            // Allow the application and DPI change to settle before declaring success.
            if (timeout.ElapsedMilliseconds >= 600)
            {
                if (!ProgramNative.WindowMatches(target)) return new(ProgramResultCode.TargetChanged, "Выбранное окно изменилось");
                ProgramNative.ShowWindowAsync(target.Handle, maximized ? 3 : 5);
                ProgramNative.SetForegroundWindow(target.Handle);
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!ProgramNative.WindowMatches(target)) return new(ProgramResultCode.AlreadyExited, "Окно уже закрыто");
                    if (ProgramNative.GetForegroundWindow() == target.Handle && ProgramNative.IsWindowVisible(target.Handle))
                        return new(ProgramResultCode.Success, "Приложение показано на главном экране");
                    ProgramNative.SetForegroundWindow(target.Handle);
                    Thread.Sleep(50);
                }
                return new(ProgramResultCode.TimedOut, "Окно перенесено, но Windows не подтвердила передачу фокуса");
            }
        }
        return new(ProgramResultCode.TimedOut, "Не удалось подтвердить перенос. Повторите попытку или включите оконный режим без рамки",
            "Окно не подтвердило размещение в течение 2 секунд; игра может выбирать экран самостоятельно или ограничивать размер.");
    }
}
