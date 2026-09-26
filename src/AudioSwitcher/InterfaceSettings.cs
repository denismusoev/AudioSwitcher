using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace AudioSwitcher;

/// <summary>Applies Windows contrast and independent text-size preferences to WPF resources.</summary>
internal sealed class InterfaceSettings : IDisposable
{
    private readonly Application app;
    private readonly Dictionary<string, object> original = new();
    private readonly Action changed;
    private int disposed;
    private static readonly int[] Sizes = [12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 34, 38];
    public double TextScale { get; private set; } = 1;

    public InterfaceSettings(Application app, Action changed)
    {
        this.app = app; this.changed = changed;
        foreach (string key in new[] { "WindowSurface", "Text", "SelectedText", "MutedText", "Line", "Accent", "FocusOutline", "SelectedSurface", "HoverSurface", "DialogSurface", "InputSurface", "ErrorText" })
            original[key] = app.Resources[key];
        SystemParameters.StaticPropertyChanged += OnSystemChanged;
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        Refresh();
    }
    private void OnSystemChanged(object? sender, PropertyChangedEventArgs e) => Schedule();
    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => Schedule();
    private void Schedule()
    {
        if (Volatile.Read(ref disposed) != 0 || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished) return;
        try { app.Dispatcher.BeginInvoke(new Action(Refresh)); }
        catch (InvalidOperationException) when (Volatile.Read(ref disposed) != 0 || app.Dispatcher.HasShutdownStarted) { }
    }

    private static double ConvertTextScale(object? value)
    {
        try { return Math.Clamp(Convert.ToDouble(value ?? 100, CultureInfo.InvariantCulture) / 100, 1, 2.25); }
        catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException) { return 1; }
    }

    private void Refresh()
    {
        if (Volatile.Read(ref disposed) != 0 || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
            TextScale = ConvertTextScale(key?.GetValue("TextScaleFactor", 100));
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException or FormatException or InvalidCastException or OverflowException)
        {
            TextScale = 1;
        }
        if (Volatile.Read(ref disposed) != 0 || app.Dispatcher.HasShutdownStarted || app.Dispatcher.HasShutdownFinished) return;
        foreach (int size in Sizes) app.Resources[$"Font{size}"] = size * 0.625 * TextScale;
        app.Resources["HeadingFont26"] = 26 * TextScale;
        app.Resources["BrandFont36"] = 36 * TextScale;
        app.Resources["BadgeSize"] = 34 * 0.625 * TextScale;
        foreach (var pair in original) app.Resources[pair.Key] = pair.Value;
        if (SystemParameters.HighContrast)
        {
            foreach (string key in new[] { "WindowSurface", "DialogSurface", "InputSurface" }) app.Resources[key] = SystemColors.WindowBrush;
            foreach (string key in new[] { "Text", "MutedText", "Line", "Accent", "FocusOutline", "ErrorText" }) app.Resources[key] = SystemColors.WindowTextBrush;
            app.Resources["SelectedSurface"] = SystemColors.HighlightBrush;
            app.Resources["SelectedText"] = SystemColors.HighlightTextBrush;
            app.Resources["HoverSurface"] = SystemColors.WindowBrush;
        }
        if (Volatile.Read(ref disposed) == 0) changed();
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        SystemParameters.StaticPropertyChanged -= OnSystemChanged;
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
    }
}
