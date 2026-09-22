using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AudioSwitcher.Platform;
public static class WindowPlacement
{
    private const double MaximumWorkAreaFraction = 0.88;
    private const double AspectRatio = 16.0 / 9.0;

    public readonly record struct WorkArea(int Left, int Top, int Width, int Height, double Scale);
    internal readonly record struct PixelBounds(int X, int Y, int Width, int Height);

    public static WorkArea PrimaryWorkArea()
    {
        // Query Windows directly: WPF's cached SystemParameters.WorkArea can lag
        // behind ChangeDisplaySettingsEx and the resulting display notifications.
        var monitor = MonitorFromPoint(new Point(0, 0), 1);
        return WorkAreaForMonitor(monitor);
    }

    public static IntPtr MonitorForWindow(IntPtr window) => MonitorFromWindow(window, 2);

    public static WorkArea WorkAreaForMonitor(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        uint dpi = GetDpiForMonitor(monitor, 0, out uint x, out _) == 0 ? x : 96;
        return new(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top, Math.Max(96, dpi) / 96.0);
    }

    public static double Scale(IntPtr window) => Math.Max(96, GetDpiForWindow(window)) / 96.0;

    internal static PixelBounds CenteredBounds(WorkArea area, double widthDip, double heightDip)
    {
        _ = heightDip; // The preferred surface is always kept at the app's 16:9 design ratio.
        double preferredWidth = widthDip * area.Scale;
        double preferredHeight = preferredWidth / AspectRatio;
        int maximumWidth = Math.Max(1, (int)Math.Floor(area.Width * MaximumWorkAreaFraction));
        int maximumHeight = Math.Max(1, (int)Math.Floor(area.Height * MaximumWorkAreaFraction));
        double fit = Math.Min(1, Math.Min(maximumWidth / preferredWidth, maximumHeight / preferredHeight));
        int width = Math.Min(maximumWidth, Math.Max(1, (int)Math.Round(preferredWidth * fit)));
        int height = Math.Min(maximumHeight, Math.Max(1, (int)Math.Round(preferredHeight * fit)));
        return new(area.Left + Math.Max(0, (area.Width - width) / 2), area.Top + Math.Max(0, (area.Height - height) / 2), width, height);
    }
    public static void PlaceCentered(IntPtr window, WorkArea area, double widthDip, double heightDip)
    {
        var bounds = CenteredBounds(area, widthDip, heightDip);
        if (!SetWindowPos(window, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x14))
            throw new Win32Exception(Marshal.GetLastWin32Error()); // NOZORDER | NOACTIVATE
    }
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
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
