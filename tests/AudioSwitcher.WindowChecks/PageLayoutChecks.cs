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

internal static class PageLayoutChecks
{
    public static int Run()
    {
        int passed = 0, failed = 0;
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            string root = Path.Combine(Path.GetTempPath(), "AudioSwitcher-page-layout-" + Guid.NewGuid().ToString("N"));
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

                    Check("All primary page titles match Control typography", () =>
                    {
                        var controlSurface = (Grid)window.FindName("ControlSurface");
                        var controlTitle = Descendants<TextBlock>(controlSurface).Single(text => text.Text == "Быстрые действия");
                        var programs = (ProgramsView)window.FindName("Programs");
                        var listTitle = (TextBlock)programs.FindName("ListTitle");
                        var settingsSurface = (Border)window.FindName("SettingsSurface");
                        var settingsTitle = Descendants<TextBlock>(settingsSurface).Single(text => text.Text == "Настройки");

                        foreach (string section in new[] { "Running", "Launch" })
                        {
                            Call(window, "SwitchSection", section == "Running" ? AppSection.Running : AppSection.Launch);
                            window.UpdateLayout();
                            Require(Math.Abs(listTitle.FontSize - controlTitle.FontSize) < 0.01,
                                $"{section} title font size is {listTitle.FontSize:0.##}; Control is {controlTitle.FontSize:0.##}");
                            Require(listTitle.FontWeight == controlTitle.FontWeight,
                                $"{section} title weight is {listTitle.FontWeight}; Control is {controlTitle.FontWeight}");
                        }

                        Require(Math.Abs(settingsTitle.FontSize - controlTitle.FontSize) < 0.01,
                            $"Settings title font size is {settingsTitle.FontSize:0.##}; Control is {controlTitle.FontSize:0.##}");
                        Require(settingsTitle.FontWeight == controlTitle.FontWeight,
                            $"Settings title weight is {settingsTitle.FontWeight}; Control is {controlTitle.FontWeight}");
                    });

                    Check("Primary page titles align visually with the first menu row", () =>
                    {
                        var appRoot = (FrameworkElement)window.FindName("AppRoot");
                        var controlTab = (FrameworkElement)window.FindName("ControlTab");
                        var controlSurface = (Grid)window.FindName("ControlSurface");
                        var controlTitle = Descendants<TextBlock>(controlSurface).Single(text => text.Text == "Быстрые действия");
                        var programs = (ProgramsView)window.FindName("Programs");
                        var listTitle = (TextBlock)programs.FindName("ListTitle");
                        var settingsSurface = (FrameworkElement)window.FindName("SettingsSurface");
                        var settingsTitle = Descendants<TextBlock>(settingsSurface).Single(text => text.Text == "Настройки");

                        foreach (double width in new[] { 1360d, 1100d })
                        {
                            window.Width = width;
                            Call(window, "UpdateAppearance");
                            Call(window, "SwitchSection", AppSection.Control);
                            window.UpdateLayout();

                            double menuCenter = CenterY(controlTab, appRoot);
                            double controlCenter = CenterY(controlTitle, appRoot);
                            Call(window, "SwitchSection", AppSection.Running);
                            window.UpdateLayout();
                            double programsCenter = CenterY(listTitle, appRoot);
                            Call(window, "OpenSettings");
                            window.UpdateLayout();
                            double settingsCenter = CenterY(settingsTitle, appRoot);

                            Require(Math.Abs(controlCenter - menuCenter) < 0.5
                                && Math.Abs(programsCenter - menuCenter) < 0.5
                                && Math.Abs(settingsCenter - menuCenter) < 0.5,
                                $"At width {width:0}, menu center is {menuCenter:0.##}; Control is {controlCenter:0.##}; Programs is {programsCenter:0.##}; Settings is {settingsCenter:0.##}");

                            Call(window, "CloseDetails", true);
                        }
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

    private static double CenterY(FrameworkElement element, UIElement relativeTo) =>
        element.TranslatePoint(new Point(0, element.ActualHeight / 2), relativeTo).Y;

    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
