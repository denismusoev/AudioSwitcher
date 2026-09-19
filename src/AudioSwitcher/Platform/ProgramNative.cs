using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using AudioSwitcher.Core;

namespace AudioSwitcher.Platform;

internal static class ProgramNative
{
    internal const uint Query = 0x1000, Synchronize = 0x100000;
    internal delegate bool WindowCallback(nint window, nint parameter);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect
    {
        public int Left, Top, Right, Bottom;
        public readonly WindowBounds Bounds => new(Left, Top, Right - Left, Bottom - Top);
    }
    [StructLayout(LayoutKind.Sequential)] internal record struct Point(int X, int Y);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct MonitorInfo
    {
        public int Size; public Rect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Placement
    {
        public int Size; public uint Flags, Show; public Point Min, Max; public Rect Normal;
    }
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool EnumWindows(WindowCallback callback, nint parameter);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] internal static extern bool IsWindow(nint window);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] internal static extern bool IsHungAppWindow(nint window);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out int process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint window, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint window, StringBuilder text, int capacity);
    [DllImport("user32.dll")] internal static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")] internal static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool GetWindowPlacement(nint window, ref Placement placement);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPlacement(nint window, ref Placement placement);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] internal static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] internal static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] internal static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int process);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] internal static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] internal static extern bool ProcessIdToSessionId(int process, out uint session);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    internal static readonly string? CurrentUser = ReadCurrentUser();
    private static string? ReadCurrentUser() { using var identity = WindowsIdentity.GetCurrent(); return identity.User?.Value; }
    internal static bool SameUserSession(SafeProcessHandle process, int pid)
    {
        if (CurrentUser == null || !ProcessIdToSessionId(pid, out uint session) ||
            !ProcessIdToSessionId(Environment.ProcessId, out uint ownSession) || session != ownSession) return false;
        if (!OpenProcessToken(process, 8, out var token)) return false;
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle())) return identity.User?.Value == CurrentUser;
    }
    internal static ProcessIdentity Identity(SafeProcessHandle process, int pid)
    {
        if (!GetProcessTimes(process, out long created, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(pid, created);
    }
    internal static string Image(SafeProcessHandle process)
    {
        var text = new StringBuilder(32768); int capacity = text.Capacity;
        if (!QueryFullProcessImageName(process, 0, text, ref capacity)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return text.ToString();
    }
    internal static MonitorInfo Monitor(nint monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
        if (!GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return info;
    }
    internal static bool WindowMatches(WindowTarget target)
    {
        return IsWindow(target.Handle) && GetWindowThreadProcessId(target.Handle, out int pid) != 0 && pid == target.Process.Pid;
    }
    internal static ProgramResult Error(int error) => error switch
    {
        5 => new(ProgramResultCode.AccessDenied, "Недостаточно прав для управления программой", "Windows отказала в доступе. Программа может быть запущена с повышенными правами."),
        87 or 1168 => new(ProgramResultCode.AlreadyExited, "Программа уже закрыта"),
        _ => new(ProgramResultCode.Failed, "Не удалось выполнить действие", new Win32Exception(error).Message)
    };
}
