using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AudioSwitcher;
using AudioSwitcher.Controls;
using AudioSwitcher.Core;
using AudioSwitcher.Platform;

internal static class SelectedFixChecks
{
    public static int RunSelectionNavigation()
    {
        int passed = 0, failed = 0;
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            string root = Path.Combine(Path.GetTempPath(), "AudioSwitcher-selection-navigation-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var window = new MainWindow(new ApplicationSettingsStore(Path.Combine(root, "settings.json")));
            window.Show();
            window.Dispatcher.InvokeAsync(async () =>
            {
                async Task Check(string name, Func<Task> test)
                {
                    try { await test(); Console.WriteLine("PASS " + name); passed++; }
                    catch (Exception error) { Console.WriteLine("FAIL " + name + ": " + error.Message); failed++; }
                }

                try
                {
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    ((DispatcherTimer)typeof(MainWindow).GetField("refreshTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Stop();
                    ((DispatcherTimer)typeof(MainWindow).GetField("padTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Stop();
                    Call(window, "SwitchSection", AppSection.Running);
                    var programs = (ProgramsView)window.FindName("Programs");
                    programs.Leave();
                    var list = (ListBox)programs.FindName("RunningList");
                    var firstId = new ProcessIdentity(7001, 1);
                    var secondId = new ProcessIdentity(7002, 1);
                    var first = new RunningProgram(firstId, "Первый", [new(firstId, (nint)101, "Первое окно", "Экран 1", false)]);
                    var second = new RunningProgram(secondId, "Второй",
                    [
                        new(secondId, (nint)201, "Второе окно", "Экран 1", false),
                        new(secondId, (nint)202, "Третье окно", "Экран 2", false)
                    ]);
                    list.ItemsSource = new[] { first, second };

                    await Check("A newly entered list starts at its first item and clears on exit", () =>
                    {
                        list.SelectedIndex = 1;
                        Call(window, "Execute", PadAction.Right);
                        Require(list.SelectedIndex == 0, $"New entry selected row {list.SelectedIndex} instead of the first row");
                        list.SelectedIndex = 1;
                        Call(window, "Execute", PadAction.Left);
                        Require(list.SelectedIndex == -1, "The list kept a selection after it was exited");
                        return Task.CompletedTask;
                    });

                    await Check("Internal pages clear on exit while Back restores the invoking row", async () =>
                    {
                        Call(window, "Execute", PadAction.Right);
                        list.SelectedIndex = 1;
                        await programs.ConfirmAsync();
                        var actions = (ListBox)programs.FindName("ProgramActions");
                        Require(list.SelectedIndex == -1, "The hidden program list kept its selected row");
                        Require(actions.SelectedIndex == 0, $"The internal page opened at row {actions.SelectedIndex} instead of the first row");
                        actions.SelectedIndex = 1;
                        Require(programs.Back(), "Back did not close the internal page");
                        Require(actions.SelectedIndex == -1, "The internal page kept its selection after exit");
                        Require(list.SelectedIndex == 1, $"Back restored row {list.SelectedIndex} instead of the invoking row");
                    });
                }
                finally
                {
                    Console.WriteLine($"Passed: {passed}, Failed: {failed}, Skipped: 0");
                    window.Close();
                    try { Directory.Delete(root, true); } catch { }
                    app.Shutdown(); ready.Set();
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

    public static int RunDpi()
    {
        try
        {
            var area = new WindowPlacement.WorkArea(0, 0, 2560, 1440, 1.5);
            var method = typeof(WindowPlacement).GetMethod("CenteredBounds", BindingFlags.Static | BindingFlags.NonPublic);
            Require(method != null, "Window placement has no target-DPI physical bounds calculation");
            object bounds = method!.Invoke(null, [area, 1500d, 844d])!;
            int x = (int)bounds.GetType().GetProperty("X")!.GetValue(bounds)!;
            int y = (int)bounds.GetType().GetProperty("Y")!.GetValue(bounds)!;
            int width = (int)bounds.GetType().GetProperty("Width")!.GetValue(bounds)!;
            int height = (int)bounds.GetType().GetProperty("Height")!.GetValue(bounds)!;
            Require((x, y, width, height) == (155, 87, 2250, 1266), $"Got {x},{y} {width}x{height}");
            Console.WriteLine("PASS Mixed-DPI centering uses target physical bounds");
            Console.WriteLine("Passed: 1, Failed: 0, Skipped: 0");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("FAIL Mixed-DPI centering uses target physical bounds: " + error.Message);
            Console.WriteLine("Passed: 0, Failed: 1, Skipped: 0");
            return 1;
        }
    }

    public static int Run()
    {
        int passed = 0, failed = 0;
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            string root = Path.Combine(Path.GetTempPath(), "AudioSwitcher-selected-fixes-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var window = new MainWindow(new ApplicationSettingsStore(Path.Combine(root, "settings.json")));
            window.Show();
            window.Dispatcher.InvokeAsync(async () =>
            {
                async Task Check(string name, Func<Task> test)
                {
                    try { await test(); Console.WriteLine("PASS " + name); passed++; }
                    catch (Exception error) { Console.WriteLine("FAIL " + name + ": " + error.Message); failed++; }
                }

                try
                {
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    await Check("A later active controller is not masked by an idle controller", () =>
                    {
                        var select = typeof(Gamepad).GetMethod("SelectAction", BindingFlags.Static | BindingFlags.NonPublic);
                        Require(select != null, "Gamepad has no multi-controller action selector");
                        var action = (PadAction)select!.Invoke(null, [new[] { PadAction.None, PadAction.Down }])!;
                        Require(action == PadAction.Down, $"Expected Down, got {action}");
                        return Task.CompletedTask;
                    });

                    await Check("Primary work area carries the target monitor DPI", () =>
                    {
                        var area = WindowPlacement.PrimaryWorkArea();
                        var scale = area.GetType().GetProperty("Scale")?.GetValue(area) as double?;
                        Require(scale is >= 1 and <= 8, "Primary work area has no usable target DPI scale");
                        return Task.CompletedTask;
                    });

                    await Check("Compact settings stay inside the window", () =>
                    {
                        window.Width = 900; window.Height = 506; window.UpdateLayout();
                        Call(window, "OpenSettings"); window.UpdateLayout();
                        var settings = (FrameworkElement)window.FindName("SettingsSurface");
                        var bounds = settings.TransformToAncestor(window).TransformBounds(new Rect(settings.RenderSize));
                        Require(bounds.Left >= -0.5 && bounds.Right <= window.ActualWidth + 0.5,
                            $"Settings bounds {bounds} exceed window width {window.ActualWidth}");
                        Call(window, "CloseDetails");
                        return Task.CompletedTask;
                    });

                    await Check("Large text does not clip settings or footer content", () =>
                    {
                        foreach (int size in new[] { 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 34, 38 })
                            app.Resources[$"Font{size}"] = size * 0.625 * 2.25;
                        app.Resources["BadgeSize"] = 34 * 0.625 * 2.25;
                        window.Width = 1500; window.Height = 844; window.UpdateLayout();
                        Call(window, "OpenSettings"); window.UpdateLayout();
                        var toggle = (FrameworkElement)window.FindName("SettingsToggle");
                        Require(VisibleDescendants(toggle).All(child => Inside(toggle, child)), "Settings text is clipped by its row");
                        var footer = (FrameworkElement)window.FindName("FooterSurface");
                        Require(VisibleDescendants(footer).All(child => Inside(footer, child)), "Footer content is clipped");
                        Call(window, "CloseDetails");
                        return Task.CompletedTask;
                    });

                    await Check("Error closes back to the invoking device picker", () =>
                    {
                        Call(window, "OpenDevicePicker", false);
                        Call(window, "ShowError", "Ошибка", "Подробности");
                        Call(window, "Execute", PadAction.Close); window.UpdateLayout();
                        var picker = (FrameworkElement)window.FindName("DevicePickerOverlay");
                        var devices = (ListBox)window.FindName("Devices");
                        Require(picker.IsVisible && devices.IsKeyboardFocusWithin, "Device picker context or focus was not restored");
                        Call(window, "CloseDetails");
                        return Task.CompletedTask;
                    });

                    await Check("Overlay disables background focus navigation", () =>
                    {
                        Call(window, "OpenDevicePicker", false); window.UpdateLayout();
                        var frame = (FrameworkElement)window.FindName("WindowFrame");
                        var rootElement = (FrameworkElement)window.FindName("AppRoot");
                        Require(!frame.IsEnabled, "Background surface remains enabled under the overlay");
                        Require(KeyboardNavigation.GetTabNavigation(rootElement) == KeyboardNavigationMode.Cycle,
                            "Window focus traversal is not cyclic while modal UI is present");
                        Call(window, "CloseDetails");
                        return Task.CompletedTask;
                    });

                    await Check("Running snapshot refresh preserves the focused row", async () =>
                    {
                        Call(window, "SwitchSection", AppSection.Running);
                        var programs = (ProgramsView)window.FindName("Programs");
                        programs.Leave();
                        programs.Navigation.Section = AppSection.Running;
                        var list = (ListBox)programs.FindName("RunningList");
                        var identity = new ProcessIdentity(1234, 1);
                        var first = new RunningProgram(identity, "Тест", [new(identity, (nint)10, "Окно", "Экран 1", false)]);
                        list.ItemsSource = new[] { first }; list.SelectedItem = first; list.UpdateLayout();
                        ((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0)).Focus();
                        var updated = first with { Windows = [new(identity, (nint)10, "Новое имя", "Экран 1", false)] };
                        Call(programs, "ApplyRunningSnapshot", new[] { updated }, true);
                        await Dispatcher.Yield(DispatcherPriority.ContextIdle); window.UpdateLayout();
                        var selected = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                        Require(selected.IsKeyboardFocused, "The recreated selected row did not regain keyboard focus");
                    });

                    await Check("Refresh state is scoped to its lifetime generation", () =>
                    {
                        var method = typeof(ProgramsView).GetMethod("CanStartRefresh", BindingFlags.Static | BindingFlags.NonPublic);
                        Require(method != null, "Programs refresh has no generation-aware gate");
                        Require((bool)method!.Invoke(null, [2, 1])!, "An old generation incorrectly blocks a new refresh");
                        Require(!(bool)method.Invoke(null, [2, 2])!, "The same generation starts duplicate refreshes");
                        return Task.CompletedTask;
                    });

                    await Check("Control device refresh exposes an asynchronous UI contract", async () =>
                    {
                        var method = typeof(MainWindow).GetMethod("RefreshControlAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                        Require(method != null && typeof(Task).IsAssignableFrom(method.ReturnType), "Control refresh still runs synchronously on the UI thread");
                        await (Task)method!.Invoke(window, [true])!;
                    });
                }
                finally
                {
                    Console.WriteLine($"Passed: {passed}, Failed: {failed}, Skipped: 0");
                    window.Close();
                    try { Directory.Delete(root, true); } catch { }
                    app.Shutdown(); ready.Set();
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

    private static IEnumerable<FrameworkElement> VisibleDescendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement element && element.IsVisible) yield return element;
            foreach (var nested in VisibleDescendants(child)) yield return nested;
        }
    }

    private static bool Inside(FrameworkElement parent, FrameworkElement child)
    {
        if (child.ActualWidth <= 0 || child.ActualHeight <= 0) return true;
        var bounds = child.TransformToAncestor(parent).TransformBounds(new Rect(child.RenderSize));
        return bounds.Left >= -0.5 && bounds.Top >= -0.5 && bounds.Right <= parent.ActualWidth + 0.5 && bounds.Bottom <= parent.ActualHeight + 0.5;
    }

    private static object? Call(object instance, string name, params object?[] args) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);

    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
