using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AudioSwitcher;
using AudioSwitcher.Controls;
using AudioSwitcher.Core;
using AudioSwitcher.Platform;

internal static class FixedDesignChecks
{
    public static int Run()
    {
        int passed = 0, failed = 0;
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            string root = Path.Combine(Path.GetTempPath(), "AudioSwitcher-tv-design-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var window = new MainWindow(new ApplicationSettingsStore(Path.Combine(root, "settings.json")));
            window.Show();
            window.Dispatcher.InvokeAsync(async () =>
            {
                void Check(string name, Action test)
                {
                    try { test(); Console.WriteLine("PASS " + name); passed++; }
                    catch (Exception error) { Console.WriteLine("FAIL " + name + ": " + error.Message); failed++; }
                }
                try
                {
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    Check("TV shell is fixed at 1220 by 760", () => Require(window.ActualWidth == 1220 && window.ActualHeight == 760, $"{window.ActualWidth}x{window.ActualHeight}"));
                    Check("PS5 shell uses a full-bleed background", () =>
                    {
                        var frame = (Border)window.FindName("WindowFrame");
                        var origin = frame.TranslatePoint(new Point(), window);
                        Require(Math.Abs(origin.X) < 0.5 && Math.Abs(origin.Y) < 0.5, $"Shell starts at {origin}");
                        Require(Math.Abs(frame.ActualWidth - window.ActualWidth) < 0.5 && Math.Abs(frame.ActualHeight - window.ActualHeight) < 0.5, $"Shell is {frame.ActualWidth}x{frame.ActualHeight}");
                        Require(frame.CornerRadius == new CornerRadius(0), $"Shell radius is {frame.CornerRadius}");
                    });
                    Check("Three root sections replace device and settings tabs", () =>
                    {
                        Require(window.FindName("ControlTab") is Button control && control.IsVisible && window.FindName("RunningTab") is Button running && running.IsVisible && window.FindName("LaunchTab") is Button launch && launch.IsVisible, "Root sections missing");
                        Require(window.FindName("AudioTab") == null && window.FindName("DisplayTab") == null && window.FindName("SettingsTab") == null, "Old tabs remain");
                    });
                    Check("Root navigation forms a PS5-style vertical rail", () =>
                    {
                        var control = (Button)window.FindName("ControlTab");
                        var running = (Button)window.FindName("RunningTab");
                        var launch = (Button)window.FindName("LaunchTab");
                        var controlOrigin = control.TranslatePoint(new Point(), window);
                        var runningOrigin = running.TranslatePoint(new Point(), window);
                        var launchOrigin = launch.TranslatePoint(new Point(), window);
                        Require(Math.Abs(controlOrigin.X - runningOrigin.X) < 0.5 && Math.Abs(runningOrigin.X - launchOrigin.X) < 0.5, "Root items do not share a vertical axis");
                        Require(runningOrigin.Y >= controlOrigin.Y + control.ActualHeight && launchOrigin.Y >= runningOrigin.Y + running.ActualHeight, "Root items are not stacked vertically");
                        Require(control.ActualWidth >= 260 && running.ActualWidth >= 260 && launch.ActualWidth >= 260, "Navigation rail is too narrow");
                    });
                    Check("Control actions form stacked settings rows in the content pane", () =>
                    {
                        var audio = (Button)window.FindName("AudioControlCard");
                        var display = (Button)window.FindName("DisplayControlCard");
                        var navigation = (Button)window.FindName("ControlTab");
                        var audioOrigin = audio.TranslatePoint(new Point(), window);
                        var displayOrigin = display.TranslatePoint(new Point(), window);
                        var navigationOrigin = navigation.TranslatePoint(new Point(), window);
                        Require(audioOrigin.X >= navigationOrigin.X + navigation.ActualWidth + 40, "Settings rows do not start in the right pane");
                        Require(Math.Abs(audioOrigin.X - displayOrigin.X) < 0.5 && displayOrigin.Y >= audioOrigin.Y + audio.ActualHeight, "Settings rows are not vertically aligned");
                        Require(audio.ActualWidth >= 650 && display.ActualWidth >= 650, "Settings rows are too narrow");
                        Require(audio.ActualHeight is >= 76 and <= 108 && display.ActualHeight is >= 76 and <= 108, "Settings rows do not use PS5 proportions");
                        Require(((FrameworkElement)window.FindName("ControlSurface")).IsVisible, "Control surface hidden");
                        Require(!VisualText(window).Any(text => text is "Быстрое управление" or "Звук и основной экран — без выхода на рабочий стол"), "Removed control-page introduction is still rendered");
                    });
                    Capture(window, "tv-implemented-control.png");

                    Call(window, "SwitchSection", AppSection.Running);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    var programs = (ProgramsView)window.FindName("Programs");
                    Check("Running applications are a root section without nested mode tabs", () =>
                    {
                        Require(programs.IsVisible && !programs.Navigation.LaunchList, "Running root not active");
                        Require(!((FrameworkElement)programs.FindName("RunningMode")).IsVisible && !((FrameworkElement)programs.FindName("LaunchMode")).IsVisible, "Nested tabs remain visible");
                    });
                    Capture(window, "tv-implemented-running.png");
                    programs.Leave();
                    using (var process = Process.GetCurrentProcess())
                    {
                        var running = (ListBox)programs.FindName("RunningList");
                        var sample = new RunningProgram(new(process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc()), "Тестовое приложение",
                            new[] { new WindowTarget(default, (nint)101, "Первое окно", "Экран 1", false), new WindowTarget(default, (nint)102, "Второе окно", "Экран 2", false) });
                        running.ItemsSource = new[] { sample }; running.SelectedItem = sample;
                        running.UpdateLayout();
                        var row = (ListBoxItem)running.ItemContainerGenerator.ContainerFromIndex(0);
                        var rowBorder = VisualChild<Border>(row) ?? throw new Exception("Running row border missing");
                        var restingBorder = rowBorder.BorderThickness;
                        row.Focus(); window.UpdateLayout();
                        Check("Focused rows keep a pixel-aligned constant border", () =>
                        {
                            Require(window.UseLayoutRounding && window.SnapsToDevicePixels, "Window is not aligned to device pixels");
                            Require(rowBorder.BorderThickness == restingBorder && restingBorder == new Thickness(2), $"Focus border changes from {restingBorder} to {rowBorder.BorderThickness}");
                        });
                        await programs.ConfirmAsync(); window.UpdateLayout();
                        Check("A opens a window picker directly when an app has several windows", () => Require(programs.Navigation.Panel == ProgramPanel.Windows, "Intermediate actions menu opened"));
                        programs.Back();
                        await programs.SecondaryAsync(); window.UpdateLayout();
                        Check("X opens direct close choices for each window and all windows", () =>
                        {
                            var actions = (ListBox)programs.FindName("ProgramActions");
                            Require(programs.Navigation.Panel == ProgramPanel.CloseWindows, "Close confirmation was not replaced by a window picker");
                            Require(actions.Items.Count == 3, $"Expected two windows plus close-all, got {actions.Items.Count}");
                            Require(actions.Items.Cast<object>().Any(item => item.GetType().GetProperty("DisplayName")?.GetValue(item)?.ToString() == "Закрыть все окна"), "Close-all choice missing");
                        });
                        Capture(window, "tv-implemented-close-picker.png");
                        programs.Back();
                    }

                    Call(window, "SwitchSection", AppSection.Launch);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    Check("Launch catalogue is its own root section", () => Require(programs.IsVisible && programs.Navigation.LaunchList && ((ListBox)programs.FindName("LaunchList")).IsVisible, "Launch root not active"));
                    Capture(window, "tv-implemented-launch.png");

                    Call(window, "Execute", PadAction.Settings);
                    window.UpdateLayout();
                    Check("Menu opens one settings overlay with safe choice selected", () =>
                    {
                        Require(((FrameworkElement)window.FindName("OverlayShade")).IsVisible && ((FrameworkElement)window.FindName("SettingsSurface")).IsVisible, "Settings overlay missing");
                        Require(((ListBox)window.FindName("SettingsChoices")).SelectedIndex == 0, "Safe keep-open choice is not selected");
                        Require(window.FindName("DevicePickerOverlay") is FrameworkElement picker && !picker.IsVisible, "Two overlays are visible");
                    });
                    Capture(window, "tv-implemented-settings.png");
                    Call(window, "Execute", PadAction.Close);

                    Call(window, "OpenDevicePicker", false);
                    window.UpdateLayout();
                    Check("Audio selection opens as a full-window focused overlay", () =>
                    {
                        var shade = (FrameworkElement)window.FindName("OverlayShade");
                        var card = (Border)window.FindName("DevicePickerOverlay");
                        var origin = shade.TranslatePoint(new Point(), window);
                        Require(card.IsVisible && ((ListBox)window.FindName("Devices")).IsKeyboardFocusWithin, "Device picker not focused");
                        Require(Math.Abs(origin.X) < 0.5 && Math.Abs(origin.Y) < 0.5 && Math.Abs(shade.ActualWidth - window.ActualWidth) < 0.5 && Math.Abs(shade.ActualHeight - window.ActualHeight) < 0.5, "Backdrop does not cover the full window");
                        Require(card.Background is SolidColorBrush brush && brush.Color.A == 255, "Modal surface is translucent");
                    });
                    Capture(window, "tv-implemented-audio-picker.png");
                    Call(window, "Execute", PadAction.Close);

                    Call(window, "SwitchSection", AppSection.Launch);
                    await programs.CreateAsync();
                    window.UpdateLayout();
                    Check("Y route opens the catalogue editor", () => Require(programs.Editing && programs.Editor != null && programs.Editor.IsVisible, "Editor did not open"));
                    Check("Editor exposes gamepad and mouse save, delete and back actions", () => Require(((Button)window.FindName("CreateCommand")).IsVisible && ((Button)window.FindName("SecondaryCommand")).IsVisible && ((Button)window.FindName("BackCommand")).IsVisible, "Editor commands missing"));
                    Call(window, "Execute", PadAction.Right);
                    Call(window, "Execute", PadAction.Confirm);
                    Check("Editor reaches Arguments with horizontal controller navigation", () => Require(Keyboard.FocusedElement == programs.Editor!.FindName("EntryArguments"), "Right then A did not focus Arguments"));
                    programs.Editor!.EndFieldInput();
                    Call(window, "Execute", PadAction.Down);
                    Call(window, "Execute", PadAction.Confirm);
                    Check("Editor reaches Working directory from Arguments", () => Require(Keyboard.FocusedElement == programs.Editor!.FindName("EntryDirectory"), "Down then A did not focus Working directory"));
                    Capture(window, "tv-implemented-editor.png");
                    programs.Back();
                }
                catch (Exception error) { Console.WriteLine("FAIL setup: " + error); failed++; }
                finally
                {
                    Console.WriteLine($"Passed: {passed}, Failed: {failed}");
                    window.Close();
                    try { Directory.Delete(root, true); } catch { }
                    app.Shutdown();
                    ready.Set();
                }
            });
            app.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(30));
        thread.Join();
        return failed == 0 ? 0 : 1;
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static IEnumerable<string> VisualText(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock text && text.IsVisible) yield return text.Text;
            foreach (var nested in VisualText(child)) yield return nested;
        }
    }
    private static T? VisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (VisualChild<T>(child) is T nested) return nested;
        }
        return null;
    }
    private static object? Call(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static void Capture(Window window, string name)
    {
        var frame = (FrameworkElement)window.FindName("AppRoot");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(frame.ActualWidth), (int)Math.Ceiling(frame.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(frame);
        Directory.CreateDirectory("artifacts");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine("artifacts", name));
        encoder.Save(file);
    }
}
