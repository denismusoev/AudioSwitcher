using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AudioSwitcher;
using AudioSwitcher.Controls;
using AudioSwitcher.Core;
using AudioSwitcher.Platform;

internal static class ApprovedDesignChecks
{
    public static int Run()
    {
        int passed = 0, failed = 0;
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            string root = Path.Combine(Path.GetTempPath(), "AudioSwitcher-approved-design-" + Guid.NewGuid().ToString("N"));
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
                    ((DispatcherTimer)typeof(MainWindow).GetField("refreshTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Stop();
                    ((DispatcherTimer)typeof(MainWindow).GetField("padTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Stop();

                    Check("Default surface is the approved compact 16:9 size", () =>
                    {
                        Require(Math.Abs(window.Width - 1360) < 0.5 && Math.Abs(window.Height - 765) < 0.5,
                            $"Window is {window.Width:0.##}x{window.Height:0.##} instead of 1360x765");
                    });

                    Check("Secondary status text uses the larger readable token", () =>
                    {
                        var status = (TextBlock)window.FindName("Status");
                        Require(Math.Abs(status.FontSize - (double)app.FindResource("Font28")) < 0.01,
                            $"Status font is {status.FontSize:0.##}");
                    });

                    Check("Controller commands are visible buttons with an icon-only Menu badge", () =>
                    {
                        var primary = (Button)window.FindName("PrimaryCommand");
                        var settings = (Button)window.FindName("SettingsCommand");
                        Require(primary.MinHeight >= 42, $"Primary command height is {primary.MinHeight}");
                        Require(primary.Background is SolidColorBrush background && background.Color.A > 0,
                            "Primary command has no visible resting surface");
                        Require(settings.Tag?.ToString() is "☰", $"Menu badge is still '{settings.Tag}'");
                    });

                    Check("Existing focus outline and fill tokens remain unchanged", () =>
                    {
                        Require((Thickness)app.FindResource("FocusBorderThickness") == new Thickness(2), "Focus outline thickness changed");
                        Require(app.FindResource("SelectedSurface") is SolidColorBrush brush && brush.Color == Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF),
                            "Focused background fill changed");
                    });

                    Check("Active-device marker is slightly larger", () =>
                    {
                        var devices = (ListBox)window.FindName("Devices");
                        ((FrameworkElement)window.FindName("OverlayShade")).Visibility = Visibility.Visible;
                        ((FrameworkElement)window.FindName("DevicePickerOverlay")).Visibility = Visibility.Visible;
                        devices.ItemsSource = new[] { new DeviceOption("approved", "Устройство", "Описание", true) };
                        devices.SelectedIndex = 0;
                        window.UpdateLayout();
                        var row = (ListBoxItem)devices.ItemContainerGenerator.ContainerFromIndex(0);
                        var marker = Descendants<TextBlock>(row).Single(text => text.Text == "Активно");
                        Require(Math.Abs(marker.FontSize - (double)app.FindResource("Font24")) < 0.01,
                            $"Active marker font is {marker.FontSize:0.##}");
                        ((FrameworkElement)window.FindName("DevicePickerOverlay")).Visibility = Visibility.Collapsed;
                        ((FrameworkElement)window.FindName("OverlayShade")).Visibility = Visibility.Collapsed;
                    });

                    Check("Long editable path has an ellipsized resting presentation", () =>
                    {
                        var editor = new LaunchEntryDialog(null, _ => Task.CompletedTask);
                        var display = editor.FindName("TargetDisplay") as TextBlock;
                        Require(display?.TextTrimming == TextTrimming.CharacterEllipsis,
                            "Editor path has no ellipsized resting presentation");
                    });

                    Check("Editor fields use one aligned two-column grid", () =>
                    {
                        var editor = new LaunchEntryDialog(null, _ => Task.CompletedTask);
                        var name = (Border)editor.FindName("NameAttribute");
                        var target = (Border)editor.FindName("TargetAttribute");
                        var arguments = (Border)editor.FindName("ArgumentsAttribute");
                        var directory = (Border)editor.FindName("DirectoryAttribute");
                        Require(ReferenceEquals(name.Parent, arguments.Parent) && ReferenceEquals(target.Parent, directory.Parent),
                            "Editor columns use different layout containers");
                        Require(Grid.GetRow(name) == Grid.GetRow(arguments) && Grid.GetRow(target) == Grid.GetRow(directory),
                            "Editor fields do not share aligned rows");
                    });

                    Check("Internal window-picker heading keeps the parent heading origin", () =>
                    {
                        Call(window, "SwitchSection", AppSection.Running);
                        var programs = (ProgramsView)window.FindName("Programs");
                        var listTitle = (TextBlock)programs.FindName("ListTitle");
                        window.UpdateLayout();
                        Point parentOrigin = listTitle.TranslatePoint(new Point(), programs);

                        programs.Leave();
                        using var process = Process.GetCurrentProcess();
                        var identity = new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc());
                        var running = (ListBox)programs.FindName("RunningList");
                        running.ItemsSource = new[]
                        {
                            new RunningProgram(identity, "Проводник",
                            [
                                new WindowTarget(identity, (nint)101, "Первое окно", "Экран 1", false),
                                new WindowTarget(identity, (nint)102, "Второе окно", "Экран 2", false)
                            ])
                        };
                        Call(window, "Execute", PadAction.Confirm);
                        programs.ConfirmAsync().GetAwaiter().GetResult();
                        window.UpdateLayout();
                        var panelTitle = (TextBlock)programs.FindName("PanelTitle");
                        Point childOrigin = panelTitle.TranslatePoint(new Point(), programs);
                        Require(Math.Abs(parentOrigin.X - childOrigin.X) < 0.5 && Math.Abs(parentOrigin.Y - childOrigin.Y) < 0.5,
                            $"Parent heading is at {parentOrigin}; internal heading is at {childOrigin}");
                    });
                }
                finally
                {
                    Console.WriteLine($"Passed: {passed}, Failed: {failed}, Skipped: 0");
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

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static object? Call(object instance, string name, params object?[] args) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);

    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
