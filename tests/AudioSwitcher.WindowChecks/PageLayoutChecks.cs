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

                    Check("Preferred window removes unused width and keeps Settings in the content column", () =>
                    {
                        var appRoot = (FrameworkElement)window.FindName("AppRoot");
                        var controlSurface = (FrameworkElement)window.FindName("ControlSurface");
                        var settingsSurface = (FrameworkElement)window.FindName("SettingsSurface");
                        window.UpdateLayout();

                        Rect contentBounds = controlSurface.TransformToAncestor(appRoot).TransformBounds(new Rect(controlSurface.RenderSize));
                        Require(Math.Abs(window.Width - 1200) < 0.5 && Math.Abs(window.Height - 765) < 0.5,
                            $"Preferred window is {window.Width:0.##}x{window.Height:0.##} instead of 1200x765");
                        Require(contentBounds.Width is >= 680 and <= 700,
                            $"Primary content remains {contentBounds.Width:0.##} DIPs wide");

                        Call(window, "OpenSettings");
                        window.UpdateLayout();
                        Rect settingsBounds = settingsSurface.TransformToAncestor(appRoot).TransformBounds(new Rect(settingsSurface.RenderSize));
                        Require(Math.Abs(settingsBounds.Left - contentBounds.Left) < 0.5
                            && Math.Abs(settingsBounds.Right - contentBounds.Right) < 0.5,
                            $"Settings bounds {settingsBounds} do not match content bounds {contentBounds}");
                        Call(window, "CloseDetails", true);
                    });

                    Check("Control values align with the trailing edge of their rows", () =>
                    {
                        Call(window, "SwitchSection", AppSection.Control);
                        window.UpdateLayout();

                        foreach (var (cardName, valueName) in new[]
                        {
                            ("AudioControlCard", "AudioSummary"),
                            ("DisplayControlCard", "DisplaySummary")
                        })
                        {
                            var card = (FrameworkElement)window.FindName(cardName);
                            var value = (FrameworkElement)window.FindName(valueName);
                            double cardRight = card.TranslatePoint(new Point(card.ActualWidth, 0), window).X;
                            double valueRight = value.TranslatePoint(new Point(value.ActualWidth, 0), window).X;
                            Require(Math.Abs(cardRight - valueRight - 18) < 0.5,
                                $"{valueName} ends {cardRight - valueRight:0.##} DIPs before the row edge instead of 18");
                        }
                    });

                    Check("Multiple-window move choices open the shared modal picker", () =>
                    {
                        Call(window, "SwitchSection", AppSection.Running);
                        var programs = (ProgramsView)window.FindName("Programs");
                        var runningList = (ListBox)programs.FindName("RunningList");
                        var identity = new ProcessIdentity(4242, 17);
                        var fixture = new RunningProgram(identity, "Редактор", new[]
                        {
                            new WindowTarget(identity, 101, "Документ А", "Экран 1", false),
                            new WindowTarget(identity, 102, "Документ Б", "Экран 2", false)
                        });
                        runningList.ItemsSource = new[] { fixture };
                        runningList.SelectedIndex = 0;
                        try
                        {
                            programs.ConfirmAsync().GetAwaiter().GetResult();
                            var picker = window.FindName("WindowPickerOverlay") as FrameworkElement;
                            var choices = window.FindName("WindowChoices") as ListBox;
                            Require(picker?.Visibility == Visibility.Visible && choices?.Items.Count == 2,
                                "Move choices did not open the shared modal picker");
                            Require(programs.Navigation.Panel == ProgramPanel.List,
                                $"Move choices replaced the list with nested panel {programs.Navigation.Panel}");
                        }
                        finally
                        {
                            Call(window, "CloseDetails", true);
                            if (programs.InPanel) programs.Back();
                        }
                    });

                    Check("Multiple-window close choices open the shared modal picker", () =>
                    {
                        Call(window, "SwitchSection", AppSection.Running);
                        var programs = (ProgramsView)window.FindName("Programs");
                        var runningList = (ListBox)programs.FindName("RunningList");
                        var identity = new ProcessIdentity(4343, 18);
                        var fixture = new RunningProgram(identity, "Браузер", new[]
                        {
                            new WindowTarget(identity, 201, "Вкладка А", "Экран 1", false),
                            new WindowTarget(identity, 202, "Вкладка Б", "Экран 2", false)
                        });
                        runningList.ItemsSource = new[] { fixture };
                        runningList.SelectedIndex = 0;
                        try
                        {
                            programs.SecondaryAsync().GetAwaiter().GetResult();
                            var picker = window.FindName("WindowPickerOverlay") as FrameworkElement;
                            var choices = window.FindName("WindowChoices") as ListBox;
                            Require(picker?.Visibility == Visibility.Visible && choices?.Items.Count == 3,
                                "Close choices did not open the shared modal picker with Close all");
                            Require(programs.Navigation.Panel == ProgramPanel.List,
                                $"Close choices replaced the list with nested panel {programs.Navigation.Panel}");
                        }
                        finally
                        {
                            Call(window, "CloseDetails", true);
                            if (programs.InPanel) programs.Back();
                        }
                    });

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

                        foreach (double width in new[] { 1200d, 1100d })
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
                    Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
                    ready.Set();
                }
            });
            Dispatcher.Run();
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
