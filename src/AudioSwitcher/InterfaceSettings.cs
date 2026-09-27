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
    private static readonly string[] PaletteKeys =
    [
        "WindowSurface", "Text", "SelectedText", "MutedText", "Line", "Accent", "FocusOutline",
        "SelectedSurface", "HoverSurface", "DialogSurface", "InputSurface", "ErrorText",
        "Ps5WindowBackground", "Ps5PanelSurface", "Ps5FocusedSurface", "Ps5HoverSurface",
        "Ps5PrimaryText", "Ps5SecondaryText", "Ps5Divider", "Ps5FocusOutline", "Ps5FocusFlash",
        "Ps5ErrorText", "Ps5InputSurface", "Ps5ToggleTrack", "Ps5ToggleThumbBorder",
        "Ps5ToggleThumbOn", "Ps5ModalBackdropBrush"
    ];
    public double TextScale { get; private set; } = 1;

    public InterfaceSettings(Application app, Action changed)
    {
        this.app = app; this.changed = changed;
        foreach (string key in PaletteKeys)
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
        app.Resources["Ps5ScreenTitleFontSize"] = 36 * TextScale;
        app.Resources["Ps5SettingLabelFontSize"] = 24 * TextScale;
        app.Resources["Ps5PageTitleFontSize"] = 26 * TextScale;
        app.Resources["Ps5SettingValueFontSize"] = 18 * TextScale;
        app.Resources["Ps5AccessoryFontSize"] = 15 * TextScale;
        ApplySystemPalette(SystemParameters.HighContrast);
        if (Volatile.Read(ref disposed) == 0) changed();
    }

    private void ApplySystemPalette(bool highContrast)
    {
        foreach (var pair in original) app.Resources[pair.Key] = pair.Value;
        if (!highContrast) return;

        foreach (string key in new[] { "WindowSurface", "DialogSurface", "InputSurface", "Ps5WindowBackground", "Ps5PanelSurface", "Ps5InputSurface", "Ps5ToggleTrack", "Ps5ModalBackdropBrush" })
            app.Resources[key] = SystemColors.WindowBrush;
        foreach (string key in new[] { "Text", "MutedText", "Line", "Accent", "FocusOutline", "ErrorText", "Ps5PrimaryText", "Ps5SecondaryText", "Ps5Divider", "Ps5FocusOutline", "Ps5FocusFlash", "Ps5ErrorText", "Ps5ToggleThumbBorder" })
            app.Resources[key] = SystemColors.WindowTextBrush;
        app.Resources["SelectedSurface"] = SystemColors.HighlightBrush;
        app.Resources["Ps5FocusedSurface"] = SystemColors.HighlightBrush;
        app.Resources["SelectedText"] = SystemColors.HighlightTextBrush;
        app.Resources["Ps5ToggleThumbOn"] = SystemColors.HighlightTextBrush;
        app.Resources["HoverSurface"] = SystemColors.WindowBrush;
        app.Resources["Ps5HoverSurface"] = SystemColors.WindowBrush;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        SystemParameters.StaticPropertyChanged -= OnSystemChanged;
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
    }
}
