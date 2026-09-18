namespace AudioSwitcher.Core;

// Creation time is the native FILETIME value, read from the held process handle.
public readonly record struct ProcessIdentity(int Pid, long Created);
public sealed record WindowTarget(ProcessIdentity Process, nint Handle, string Title, string Screen, bool NotResponding)
{
    public string DisplayName => Title;
    public string DisplayDetails => Screen + (NotResponding ? " · Не отвечает" : "");
}
public sealed record RunningProgram(ProcessIdentity Identity, string Name, IReadOnlyList<WindowTarget> Windows)
{
    public string DisplayName => Name;
    public string DisplayDetails => Windows.Count == 1 ? Windows[0].DisplayDetails : $"Окна: {Windows.Count} · {string.Join(", ", Windows.Select(w => w.Screen).Distinct())}" + (Windows.Any(w => w.NotResponding) ? " · Не отвечает" : "");
}
public enum ProgramResultCode { Success, AlreadyExited, TargetChanged, AccessDenied, TimedOut, Unsupported, Failed }
public sealed record ProgramResult(ProgramResultCode Code, string Message, string? Details = null)
{
    public bool Succeeded => Code == ProgramResultCode.Success;
}
public readonly record struct WindowBounds(int Left, int Top, int Width, int Height);
