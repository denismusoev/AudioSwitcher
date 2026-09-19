using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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
                    Check("Three root sections replace device and settings tabs", () =>
                    {
                        Require(window.FindName("ControlTab") is Button control && control.IsVisible && window.FindName("RunningTab") is Button running && running.IsVisible && window.FindName("LaunchTab") is Button launch && launch.IsVisible, "Root sections missing");
                        Require(window.FindName("AudioTab") == null && window.FindName("DisplayTab") == null && window.FindName("SettingsTab") == null, "Old tabs remain");
                    });
                    Check("Control root exposes exactly two large task cards", () =>
                    {
                        var audio = (Button)window.FindName("AudioControlCard");
                        var display = (Button)window.FindName("DisplayControlCard");
                        Require(audio.ActualHeight >= 118 && display.ActualHeight >= 118, "Task cards are not TV sized");
                        Require(((FrameworkElement)window.FindName("ControlSurface")).IsVisible, "Control surface hidden");
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
                        await programs.ConfirmAsync(); window.UpdateLayout();
                        Check("A opens a window picker directly when an app has several windows", () => Require(programs.Navigation.Panel == ProgramPanel.Windows, "Intermediate actions menu opened"));
                        programs.Back();
                        await programs.SecondaryAsync(); window.UpdateLayout();
                        Check("X opens close confirmation with cancel selected", () => Require(programs.Navigation.Panel == ProgramPanel.ConfirmTermination && ((ListBox)programs.FindName("ProgramActions")).SelectedIndex == 0, "Safe confirmation missing"));
                        Capture(window, "tv-implemented-confirm-close.png");
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
                    Check("Audio selection opens as a focused overlay", () => Require(((FrameworkElement)window.FindName("DevicePickerOverlay")).IsVisible && ((ListBox)window.FindName("Devices")).IsKeyboardFocusWithin, "Device picker not focused"));
                    Capture(window, "tv-implemented-audio-picker.png");
                    Call(window, "Execute", PadAction.Close);

                    Call(window, "SwitchSection", AppSection.Launch);
                    await programs.CreateAsync();
                    window.UpdateLayout();
                    Check("Y route opens the catalogue editor", () => Require(programs.Editing && programs.Editor != null && programs.Editor.IsVisible, "Editor did not open"));
                    Check("Editor exposes gamepad and mouse save, delete and back actions", () => Require(((Button)window.FindName("CreateCommand")).IsVisible && ((Button)window.FindName("SecondaryCommand")).IsVisible && ((Button)window.FindName("BackCommand")).IsVisible, "Editor commands missing"));
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
    private static object? Call(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static void Capture(Window window, string name)
    {
        var frame = (FrameworkElement)window.FindName("WindowFrame");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(frame.ActualWidth), (int)Math.Ceiling(frame.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(frame);
        Directory.CreateDirectory("artifacts");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine("artifacts", name));
        encoder.Save(file);
    }
}
