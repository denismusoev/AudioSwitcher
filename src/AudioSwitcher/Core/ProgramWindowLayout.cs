namespace AudioSwitcher.Core;

public static class ProgramWindowLayout
{
    public static WindowBounds Calculate(WindowBounds source, WindowBounds work, uint sourceDpi, uint targetDpi,
        bool fullMonitorBorderless, WindowBounds monitor)
    {
        if (fullMonitorBorderless) return monitor;
        double ratio = Math.Max(96, targetDpi) / (double)Math.Max(96, sourceDpi);
        int width = Math.Clamp((int)Math.Round(source.Width * ratio), 1, Math.Max(1, work.Width));
        int height = Math.Clamp((int)Math.Round(source.Height * ratio), 1, Math.Max(1, work.Height));
        return new(work.Left + (work.Width - width) / 2, work.Top + (work.Height - height) / 2, width, height);
    }
}
