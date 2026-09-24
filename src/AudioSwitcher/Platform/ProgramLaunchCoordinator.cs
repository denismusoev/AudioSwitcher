using System.Diagnostics;
using System.IO;
using AudioSwitcher.Core;

namespace AudioSwitcher.Platform;

public sealed class ProgramLaunchCoordinator
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private readonly ProgramLaunchService launcher = new();
    private readonly ProgramWindowService windows = new();
    private readonly ProgramWindowMover mover = new();

    public Task<ProgramResult> LaunchMoveAndActivateAsync(LaunchEntry entry, CancellationToken cancellationToken = default) =>
        LaunchMoveAndActivateAsync(entry, DefaultTimeout, cancellationToken);

    public async Task<ProgramResult> LaunchMoveAndActivateAsync(LaunchEntry entry, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        var before = await windows.GetProgramsAsync(cancellationToken);
        var previousHandles = before.SelectMany(program => program.Windows).Select(window => window.Handle).ToHashSet();
        nint previousForeground = ProgramNative.GetForegroundWindow();
        int? launchedProcess = await launcher.LaunchAsync(entry, cancellationToken);
        var elapsed = Stopwatch.StartNew();

        while (elapsed.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await windows.GetProgramsAsync(cancellationToken);
            nint foreground = ProgramNative.GetForegroundWindow();
            var target = SelectTarget(entry, launchedProcess, previousHandles, previousForeground, foreground, snapshot);
            if (target != null)
            {
                var result = await mover.MoveToPrimaryAsync(target, cancellationToken, activate: true);
                return result.Succeeded
                    ? result with { Message = $"{entry.Name} запущена и перенесена на главный экран" }
                    : result;
            }
            await Task.Delay(150, cancellationToken);
        }

        return new(ProgramResultCode.TimedOut, "Программа запущена, но её окно не найдено",
            $"Окно не появилось в течение {timeout.TotalSeconds:0} секунд. AudioSwitcher оставлен открытым.");
    }

    private static WindowTarget? SelectTarget(LaunchEntry entry, int? launchedProcess, HashSet<nint> previousHandles,
        nint previousForeground, nint foreground, IReadOnlyList<RunningProgram> programs)
    {
        var all = programs.SelectMany(program => program.Windows.Select(window => (program, window))).ToArray();
        var newlyVisible = all.Where(item => !previousHandles.Contains(item.window.Handle)).ToArray();
        var byProcess = launchedProcess is int pid
            ? newlyVisible.FirstOrDefault(item => item.window.Process.Pid == pid).window
            : null;
        if (byProcess != null) return byProcess;

        string? expectedImage = entry.Kind == LaunchKind.Executable ? Path.GetFullPath(entry.Target) : null;
        var byImage = expectedImage == null ? null : newlyVisible.FirstOrDefault(item =>
            string.Equals(item.program.ApplicationKey, expectedImage, StringComparison.OrdinalIgnoreCase)).window;
        if (byImage != null) return byImage;

        if (foreground != 0 && foreground != previousForeground)
        {
            var active = all.FirstOrDefault(item => item.window.Handle == foreground);
            if (active.window != null && (expectedImage == null ||
                string.Equals(active.program.ApplicationKey, expectedImage, StringComparison.OrdinalIgnoreCase)))
                return active.window;
        }

        return newlyVisible.Length == 1 ? newlyVisible[0].window : null;
    }
}
