namespace AudioSwitcher.Core;

public sealed record ProgramWindowFacts(bool Visible, bool Cloaked, bool ToolWindow, bool AppWindow,
    bool HasOwner, bool SameUserSession, bool Self, string ClassName, string ImagePath);

public static class ProgramFilter
{
    public static bool Include(ProgramWindowFacts window)
    {
        if (!window.Visible || window.Cloaked || window.ToolWindow || window.Self || !window.SameUserSession ||
            (window.HasOwner && !window.AppWindow)) return false;
        if (window.ClassName is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        // A copied executable with the same name is not a verified Windows host.
        string path = window.ImagePath.Replace('/', '\\');
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windows)) windows = "C:\\Windows";
        bool systemImage = path.StartsWith(windows + "\\System32\\", StringComparison.OrdinalIgnoreCase) ||
                           path.StartsWith(windows + "\\SystemApps\\", StringComparison.OrdinalIgnoreCase);
        string name = path[(path.LastIndexOf('\\') + 1)..];
        return !systemImage || !new[] { "SearchHost.exe", "SearchApp.exe", "SearchUI.exe", "StartMenuExperienceHost.exe",
            "ShellExperienceHost.exe", "TextInputHost.exe", "LockApp.exe", "ApplicationFrameHost.exe",
            "RuntimeBroker.exe", "sihost.exe", "dwm.exe", "SystemSettingsAdminFlows.exe" }
            .Contains(name, StringComparer.OrdinalIgnoreCase);
    }
}
