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

                    await Check("Window drawer returns to the invoking row", async () =>
                    {
                        Call(window, "Execute", PadAction.Right);
                        programs.Move(1);
                        Require(list.SelectedIndex == 1 && list.ItemContainerGenerator.ContainerFromIndex(1) is ListBoxItem invokingRow && invokingRow.IsKeyboardFocused,
                            "Test setup did not focus the invoking row");
                        await programs.ConfirmAsync();
                        Require(((FrameworkElement)window.FindName("WindowPickerOverlay")).IsVisible,
                            "Multiple windows did not open the shared window drawer");
                        Call(window, "Execute", PadAction.Close);
                        Require(!((FrameworkElement)window.FindName("WindowPickerOverlay")).IsVisible,
                            "Back did not close the window drawer");
                        Require(list.SelectedIndex == 1 && list.ItemContainerGenerator.ContainerFromIndex(1) is ListBoxItem row && row.IsKeyboardFocused,
                            $"Back restored row {list.SelectedIndex}; focused element is {(Keyboard.FocusedElement as FrameworkElement)?.Name ?? Keyboard.FocusedElement?.GetType().Name ?? "null"}; row 0 focused={((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0)).IsKeyboardFocused}");
                    });

                    await Check("Manifest games show their source and hide the edit command", () =>
                    {
                        Call(window, "Execute", PadAction.Left);
                        Call(window, "SwitchSection", AppSection.Launch);
                        Call(window, "Execute", PadAction.Right);
                        var manual = new LaunchEntry(Guid.NewGuid(), "Manual", LaunchKind.Executable, @"C:\manual.exe");
                        var game = new LaunchEntry(Guid.NewGuid(), "Game", LaunchKind.Executable, @"E:\Games\Game\game.exe", "", @"E:\Games\Game",
                            LaunchEntrySource.GameManifest, @"E:\Games\Game");
                        typeof(ProgramsView).GetField("catalog", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .SetValue(programs, new LaunchCatalogLoad(new LaunchCatalog(1, [manual, game]), true, null));
                        Call(programs, "ShowCatalog");
                        var launchList = (ListBox)programs.FindName("LaunchList");
                        var secondary = (Button)window.FindName("SecondaryCommand");

                        launchList.SelectedIndex = 1;
                        Call(window, "UpdateChrome");
                        Require(secondary.Visibility == Visibility.Collapsed, "X edit command remained visible for a manifest game");
                        var details = (string)launchList.Items[1].GetType().GetProperty("DisplayDetails")!.GetValue(launchList.Items[1])!;
                        Require(details == @"Игра · E:\Games\Game\game.exe", "Manifest game row has the wrong source label");

                        launchList.SelectedIndex = 0;
                        Call(window, "UpdateChrome");
                        Require(secondary.Visibility == Visibility.Visible, "X edit command disappeared for a manual entry");
                        return Task.CompletedTask;
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
        App? app = null;
        MainWindow? window = null;
        try
        {
            var method = typeof(WindowPlacement).GetMethod("PreferredSizeInDips", BindingFlags.Static | BindingFlags.NonPublic);
            Require(method != null, "Window placement has no WPF logical-size calculation");

            var cases = new[]
            {
                ("2K 100%", new WindowPlacement.WorkArea(0, 0, 2560, 1440, 1.0), (1200d, 765d)),
                ("4K 100%", new WindowPlacement.WorkArea(0, 0, 3840, 2160, 1.0), (1200d, 765d)),
                ("4K 150%", new WindowPlacement.WorkArea(0, 0, 3840, 2160, 1.5), (1200d, 765d)),
                ("4K 200%", new WindowPlacement.WorkArea(0, 0, 3840, 2160, 2.0), (1200d, 765d)),
                ("4K 300%", new WindowPlacement.WorkArea(0, 0, 3840, 2160, 3.0), (993.8823529411765d, 633.6d)),
                ("left-side 4K 150%", new WindowPlacement.WorkArea(-3840, 0, 3840, 2160, 1.5), (1200d, 765d))
            };

            foreach (var (name, area, expected) in cases)
            {
                object size = method!.Invoke(null, [area, 1200d, 765d])!;
                double width = (double)size.GetType().GetProperty("Width")!.GetValue(size)!;
                double height = (double)size.GetType().GetProperty("Height")!.GetValue(size)!;
                Require(Math.Abs(width - expected.Item1) < 0.01 && Math.Abs(height - expected.Item2) < 0.01,
                    $"{name}: got {width:0.##}x{height:0.##} DIP; expected {expected.Item1:0.##}x{expected.Item2:0.##} DIP");
            }

            Console.WriteLine("PASS WPF logical sizing only clamps windows that exceed the target work area");

            app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            window = new MainWindow(new ApplicationSettingsStore(Path.Combine(Path.GetTempPath(), "AudioSwitcher-dpi-settings.json")));
            window.Show();
            window.Width = 900;
            window.Height = 506;
            window.UpdateLayout();

            var constructor = typeof(DpiChangedEventArgs).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [typeof(DpiScale), typeof(DpiScale), typeof(RoutedEvent), typeof(object)],
                modifiers: null);
            Require(constructor != null, "WPF DPI event constructor is unavailable");
            var change = (DpiChangedEventArgs)constructor!.Invoke(
                [new DpiScale(1, 1), new DpiScale(1.5, 1.5), Window.DpiChangedEvent, window]);

            window.RaiseEvent(change);
            window.UpdateLayout();

            Require(Math.Abs(window.Width - 900) < 0.01 && Math.Abs(window.Height - 506) < 0.01,
                $"DPI notification reset the logical window size to {window.Width:0.##}x{window.Height:0.##} DIP");
            Console.WriteLine("PASS DPI notification leaves logical window geometry under WPF control");
            Console.WriteLine("Passed: 2, Failed: 0, Skipped: 0");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("FAIL selected DPI behavior: " + error.Message);
            Console.WriteLine("Passed: 1, Failed: 1, Skipped: 0");
            return 1;
        }
        finally
        {
            window?.Close();
            app?.Shutdown();
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

                    await Check("Error closes back to the invoking device picker", async () =>
                    {
                        await (Task)Call(window, "OpenDevicePickerAsync", false)!;
                        Call(window, "ShowError", "Ошибка", "Подробности");
                        Call(window, "Execute", PadAction.Close); window.UpdateLayout();
                        var picker = (FrameworkElement)window.FindName("DevicePickerOverlay");
                        var devices = (ListBox)window.FindName("Devices");
                        Require(picker.IsVisible && devices.IsKeyboardFocusWithin, "Device picker context or focus was not restored");
                        Call(window, "CloseDetails");
                    });

                    await Check("Overlay disables background focus navigation", async () =>
                    {
                        await (Task)Call(window, "OpenDevicePickerAsync", false)!; window.UpdateLayout();
                        var frame = (FrameworkElement)window.FindName("WindowFrame");
                        var rootElement = (FrameworkElement)window.FindName("AppRoot");
                        Require(!frame.IsEnabled, "Background surface remains enabled under the overlay");
                        Require(KeyboardNavigation.GetTabNavigation(rootElement) == KeyboardNavigationMode.Cycle,
                            "Window focus traversal is not cyclic while modal UI is present");
                        Call(window, "CloseDetails");
                    });

                    await Check("Running snapshot refresh preserves the focused row", async () =>
                    {
                        Call(window, "SwitchSection", AppSection.Running);
                        var programs = (ProgramsView)window.FindName("Programs");
                        programs.Leave();
                        programs.Navigation.Section = AppSection.Running;
                        programs.Navigation.EnterSection();
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

                    await Check("Device picker exposes an asynchronous UI contract", () =>
                    {
                        var method = typeof(MainWindow).GetMethod("OpenDevicePickerAsync", BindingFlags.Instance | BindingFlags.NonPublic);
                        Require(method != null && typeof(Task).IsAssignableFrom(method.ReturnType), "Device picker still enumerates devices synchronously on the UI thread");
                        return Task.CompletedTask;
                    });

                    await Check("Device apply exposes an asynchronous UI contract", () =>
                    {
                        var method = typeof(MainWindow).GetMethod("ApplySelected", BindingFlags.Instance | BindingFlags.NonPublic);
                        Require(method != null && typeof(Task).IsAssignableFrom(method.ReturnType), "Device apply no longer exposes an awaitable UI contract");
                        return Task.CompletedTask;
                    });

                    await Check("Faulted background tasks are observed exactly once", () =>
                    {
                        var observer = typeof(MainWindow).Assembly.GetType("AudioSwitcher.TaskObserver")
                            ?? throw new Exception("Task observer is missing");
                        var observe = observer.GetMethod("Observe", BindingFlags.Static | BindingFlags.NonPublic)
                            ?? throw new Exception("Task observer entry point is missing");
                        Exception? observed = null;
                        observe.Invoke(null, [Task.FromException(new InvalidOperationException("background failure")), (Action<Exception>)(error => observed = error)]);
                        Require(observed is InvalidOperationException { Message: "background failure" }, "Faulted task was not observed through the failure callback");
                        return Task.CompletedTask;
                    });

                    await Check("Diagnostic logging includes crash context and bounds repeated writes", () =>
                    {
                        var logger = typeof(MainWindow).Assembly.GetType("AudioSwitcher.DiagnosticLog")
                            ?? throw new Exception("Diagnostic logger is missing");
                        var format = logger.GetMethod("FormatEntry", BindingFlags.Static | BindingFlags.NonPublic)
                            ?? throw new Exception("Diagnostic entry formatter is missing");
                        Exception captured;
                        try { throw new InvalidOperationException("outer failure", new FormatException("inner failure")); }
                        catch (Exception error) { captured = error; }
                        var context = new Dictionary<string, object?> { ["pid"] = 42, ["hwnd"] = "0x123", ["deviceId"] = "endpoint-1" };
                        string entry = (string)format.Invoke(null, ["ERROR", "test.operation", captured, context, 3])!;
                        foreach (string required in new[] { "severity=ERROR", "operation=test.operation", "InvalidOperationException", "outer failure", "FormatException", "inner failure", "hresult=0x", "processId=", "managedThreadId=", "appVersion=", "windowsVersion=", "pid=42", "hwnd=0x123", "deviceId=endpoint-1", "suppressed=3" })
                            Require(entry.Contains(required, StringComparison.Ordinal), "Diagnostic entry omitted " + required);

                        string key = Guid.NewGuid().ToString("N");
                        var shouldWrite = logger.GetMethod("ShouldWrite", BindingFlags.Static | BindingFlags.NonPublic)
                            ?? throw new Exception("Diagnostic rate limiter is missing");
                        object?[] first = [key, 1_000L, 0];
                        object?[] repeated = [key, 1_001L, 0];
                        object?[] resumed = [key, 31_001L, 0];
                        Require((bool)shouldWrite.Invoke(null, first)!, "First diagnostic was suppressed");
                        Require(!(bool)shouldWrite.Invoke(null, repeated)!, "Repeated diagnostic was not suppressed");
                        Require((bool)shouldWrite.Invoke(null, resumed)! && (int)resumed[2]! == 1, "Suppressed diagnostic count was lost");
                        return Task.CompletedTask;
                    });

                    await Check("Diagnostic log rotation retains at most five files", () =>
                    {
                        var logger = typeof(MainWindow).Assembly.GetType("AudioSwitcher.DiagnosticLog")!;
                        var append = logger.GetMethod("TryAppend", BindingFlags.Static | BindingFlags.NonPublic)
                            ?? throw new Exception("Diagnostic append boundary is missing");
                        string logRoot = Path.Combine(root, "logs");
                        for (int i = 0; i < 8; i++)
                            Require((bool)append.Invoke(null, [logRoot, new string((char)('a' + i), 96) + Environment.NewLine, 128L, 5])!, "Test log append failed");
                        int files = Directory.GetFiles(logRoot, "AudioSwitcher*.log").Length;
                        Require(files is >= 2 and <= 5, $"Rotation retained {files} files");
                        return Task.CompletedTask;
                    });

                    await Check("Application exposes all three global exception handlers", () =>
                    {
                        foreach (string handler in new[] { "OnDispatcherUnhandledException", "OnDomainUnhandledException", "OnUnobservedTaskException" })
                            Require(typeof(App).GetMethod(handler, BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic) != null,
                                "Missing global handler " + handler);
                        return Task.CompletedTask;
                    });

                    await Check("Malformed interface text scale falls back to 100 percent", () =>
                    {
                        var settingsType = typeof(MainWindow).Assembly.GetType("AudioSwitcher.InterfaceSettings")
                            ?? throw new Exception("InterfaceSettings type is missing");
                        var convert = settingsType.GetMethod("ConvertTextScale", BindingFlags.Static | BindingFlags.NonPublic)
                            ?? throw new Exception("Text scale conversion boundary is missing");
                        foreach (object malformed in new object[] { "not-a-number", new object(), DBNull.Value })
                        {
                            var scale = (double)convert.Invoke(null, [malformed])!;
                            Require(scale == 1, $"Malformed text scale {malformed} produced {scale}");
                        }
                        return Task.CompletedTask;
                    });

                    await Check("Disposed interface settings ignore queued callbacks", async () =>
                    {
                        var settingsType = typeof(MainWindow).Assembly.GetType("AudioSwitcher.InterfaceSettings")
                            ?? throw new Exception("InterfaceSettings type is missing");
                        int changes = 0;
                        var settings = (IDisposable)Activator.CreateInstance(settingsType, [app, (Action)(() => changes++)])!;
                        int beforeDispose = changes;
                        settings.Dispose();
                        settings.Dispose();
                        settingsType.GetMethod("Schedule", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(settings, null);
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        Require(changes == beforeDispose, "A disposed InterfaceSettings instance invoked its callback");
                    });

                    await Check("Monitor API failure preserves XAML startup placement", () =>
                    {
                        var apply = typeof(MainWindow).GetMethod("ApplyPreferredSize", BindingFlags.Instance | BindingFlags.NonPublic,
                            null, [typeof(Func<(double Width, double Height)>)], null)
                            ?? throw new Exception("Testable preferred-size boundary is missing");
                        double width = window.Width, height = window.Height;
                        var startup = window.WindowStartupLocation;
                        Func<(double Width, double Height)> fail = () => throw new System.ComponentModel.Win32Exception(5);
                        apply.Invoke(window, [fail]);
                        Require(window.Width == width && window.Height == height, "Monitor failure changed the XAML window size");
                        Require(window.WindowStartupLocation == startup, "Monitor failure changed WindowStartupLocation");
                        return Task.CompletedTask;
                    });

                    await Check("Game synchronization is inert after window lifetime cancellation", async () =>
                    {
                        window.Close();
                        await window.SynchronizeGamesAsync(quiet: true);
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

    private static object? Call(object instance, string name, params object?[] args)
    {
        var method = instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var parameters = method.GetParameters();
        if (args.Length < parameters.Length)
        {
            var expanded = new object?[parameters.Length];
            Array.Copy(args, expanded, args.Length);
            for (int i = args.Length; i < parameters.Length; i++)
                expanded[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : Type.Missing;
            args = expanded;
        }
        return method.Invoke(instance, args);
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new Exception(message);
    }
}
