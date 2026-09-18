namespace AudioSwitcher.Core;

public static class ProgramWindowLayout
{
    public static bool IsSufficientlyOnScreen(WindowBounds actual, WindowBounds area, int tolerance)
    {
        if (actual.Left >= area.Left - tolerance && actual.Top >= area.Top - tolerance &&
            actual.Left + actual.Width <= area.Left + area.Width + tolerance &&
            actual.Top + actual.Height <= area.Top + area.Height + tolerance) return true;
        int left = Math.Max(actual.Left, area.Left), top = Math.Max(actual.Top, area.Top);
        int right = Math.Min(actual.Left + actual.Width, area.Left + area.Width);
        int bottom = Math.Min(actual.Top + actual.Height, area.Top + area.Height);
        long intersection = Math.Max(0, right - left) * (long)Math.Max(0, bottom - top);
        long window = Math.Max(1, actual.Width) * (long)Math.Max(1, actual.Height);
        return intersection * 2 >= window;
    }

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
