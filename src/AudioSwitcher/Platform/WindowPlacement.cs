using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AudioSwitcher.Platform;
public static class WindowPlacement
{
    public readonly record struct WorkArea(int Left, int Top, int Width, int Height);
    public static WorkArea PrimaryWorkArea()
    {
        // Query Windows directly: WPF's cached SystemParameters.WorkArea can lag
        // behind ChangeDisplaySettingsEx and the resulting display notifications.
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromPoint(new Point(0, 0), 1), ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return new(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
    }
    public static double Scale(IntPtr window) => Math.Max(96, GetDpiForWindow(window)) / 96.0;
    public static void Center(IntPtr window)
    {
        var area = PrimaryWorkArea();
        if (!GetWindowRect(window, out var bounds)) throw new Win32Exception(Marshal.GetLastWin32Error());
        int x = area.Left + Math.Max(0, (area.Width - (bounds.Right - bounds.Left)) / 2);
        int y = area.Top + Math.Max(0, (area.Height - (bounds.Bottom - bounds.Top)) / 2);
        if (!SetWindowPos(window, IntPtr.Zero, x, y, 0, 0, 0x15)) throw new Win32Exception(Marshal.GetLastWin32Error()); // NOSIZE | NOZORDER | NOACTIVATE
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct Point(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
