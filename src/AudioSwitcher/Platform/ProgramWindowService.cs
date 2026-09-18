using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using AudioSwitcher.Core;

namespace AudioSwitcher.Platform;

public sealed class ProgramWindowService
{
    public Task<IReadOnlyList<RunningProgram>> GetProgramsAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => GetPrograms(cancellationToken), cancellationToken);

    internal static IReadOnlyList<RunningProgram> GetPrograms(CancellationToken cancellationToken)
    {
        var groups = new Dictionary<ProcessIdentity, (string Name, List<WindowTarget> Windows)>();
        var cache = new Dictionary<int, (ProcessIdentity Identity, string Image, bool Allowed)>();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool enumerated = ProgramNative.EnumWindows((window, _) => {
            if (cancellationToken.IsCancellationRequested) return false;
            try
            {
                if (!ProgramNative.IsWindowVisible(window)) return true;
                ProgramNative.GetWindowThreadProcessId(window, out int pid);
                if (pid == Environment.ProcessId || pid <= 0) return true;
                if (!cache.TryGetValue(pid, out var process))
                {
                    using var handle = ProgramNative.OpenProcess(ProgramNative.Query, false, pid);
                    if (handle.IsInvalid) { cache[pid] = default; return true; }
                    process = (ProgramNative.Identity(handle, pid), ProgramNative.Image(handle), ProgramNative.SameUserSession(handle, pid));
                    cache[pid] = process;
                }
                if (!process.Allowed) return true;
                int extended = ProgramNative.GetWindowLong(window, -20);
                var classText = new StringBuilder(256); ProgramNative.GetClassName(window, classText, classText.Capacity);
                bool cloaked = ProgramNative.DwmGetWindowAttribute(window, 14, out int cloak, sizeof(int)) == 0 && cloak != 0;
                var facts = new ProgramWindowFacts(true, cloaked, (extended & 0x80) != 0, (extended & 0x40000) != 0,
                    ProgramNative.GetWindow(window, 4) != 0, true, false, classText.ToString(), process.Image);
                if (!ProgramFilter.Include(facts)) return true;
                var title = new StringBuilder(1024); ProgramNative.GetWindowText(window, title, title.Capacity);
                string name = string.IsNullOrWhiteSpace(title.ToString()) ? Path.GetFileNameWithoutExtension(process.Image) : title.ToString();
                var monitor = ProgramNative.Monitor(ProgramNative.MonitorFromWindow(window, 2));
                string screen = monitor.Device.Replace("\\\\.\\DISPLAY", "Экран ") + ((monitor.Flags & 1) != 0 ? " · Главный" : "");
                if (!groups.TryGetValue(process.Identity, out var group))
                {
                    if (!names.TryGetValue(process.Image, out string? applicationName))
                    {
                        applicationName = ApplicationName(process.Image);
                        names.Add(process.Image, applicationName);
                    }
                    group = (applicationName, []); groups.Add(process.Identity, group);
                }
                group.Windows.Add(new(process.Identity, window, name, screen, ProgramNative.IsHungAppWindow(window)));
            }
            catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or ArgumentException) { /* Window exited or cannot be verified. */ }
            return true;
        }, 0);
        cancellationToken.ThrowIfCancellationRequested();
        if (!enumerated) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows не предоставила доступ к списку окон текущего рабочего стола");
        return groups.Select(g => new RunningProgram(g.Key, g.Value.Name, g.Value.Windows.OrderBy(w => w.Title, StringComparer.CurrentCultureIgnoreCase).ThenBy(w => w.Handle).ToArray()))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Identity.Pid).ToArray();
    }

    private static string ApplicationName(string image)
    {
        try
        {
            var metadata = FileVersionInfo.GetVersionInfo(image);
            if (!string.IsNullOrWhiteSpace(metadata.FileDescription)) return metadata.FileDescription.Trim();
            if (!string.IsNullOrWhiteSpace(metadata.ProductName)) return metadata.ProductName.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException) { /* Metadata is optional. */ }
        return Path.GetFileNameWithoutExtension(image);
    }
}
