using System.ComponentModel;
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
    private static readonly int[] Sizes = [12, 13, 14, 15, 16, 18, 22, 24];
    public double TextScale { get; private set; } = 1;

    public InterfaceSettings(Application app, Action changed)
    {
        this.app = app; this.changed = changed;
        foreach (string key in new[] { "WindowSurface", "Text", "SelectedText", "MutedText", "Line", "Accent", "SelectedSurface", "HoverSurface", "DialogSurface", "InputSurface", "ErrorText" })
            original[key] = app.Resources[key];
        SystemParameters.StaticPropertyChanged += OnSystemChanged;
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
        Refresh();
    }
    private void OnSystemChanged(object? sender, PropertyChangedEventArgs e) => Schedule();
    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => Schedule();
    private void Schedule() => app.Dispatcher.BeginInvoke(Refresh);
    private void Refresh()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
            TextScale = Math.Clamp(Convert.ToDouble(key?.GetValue("TextScaleFactor", 100)) / 100, 1, 2.25);
        }
        catch (System.Security.SecurityException) { TextScale = 1; }
        foreach (int size in Sizes) app.Resources[$"Font{size}"] = size * TextScale;
        app.Resources["BadgeSize"] = 18 * TextScale;
        foreach (var pair in original) app.Resources[pair.Key] = pair.Value;
        if (SystemParameters.HighContrast)
        {
            foreach (string key in new[] { "WindowSurface", "DialogSurface", "InputSurface" }) app.Resources[key] = SystemColors.WindowBrush;
            foreach (string key in new[] { "Text", "MutedText", "Line", "Accent", "ErrorText" }) app.Resources[key] = SystemColors.WindowTextBrush;
            app.Resources["SelectedSurface"] = SystemColors.HighlightBrush;
            app.Resources["SelectedText"] = SystemColors.HighlightTextBrush;
            app.Resources["HoverSurface"] = SystemColors.WindowBrush;
        }
        changed();
    }
    public void Dispose()
    {
        SystemParameters.StaticPropertyChanged -= OnSystemChanged;
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
    }
}
