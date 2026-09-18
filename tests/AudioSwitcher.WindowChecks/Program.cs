using System.Reflection;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AudioSwitcher;
using AudioSwitcher.Controls;
using AudioSwitcher.Core;
using AudioSwitcher.Platform;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        int passed = 0, failed = 0, skipped = 0;
        var app = new App(); app.InitializeComponent();
        var window = new MainWindow(); window.Show();
        var devices = (ListBox)window.FindName("Devices");
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); Console.WriteLine($"PASS {name}"); passed++; }
            catch (CheckSkipped ex) { Console.WriteLine($"SKIP {name}: {ex.Message}"); skipped++; }
            catch (Exception ex) { Console.WriteLine($"FAIL {name}: {ex}"); failed++; }
        }
        window.Dispatcher.BeginInvoke(new Action(async () => {
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                // Keep fixtures independent of live device refresh and controller input.
                ((DispatcherTimer)typeof(MainWindow).GetField("refreshTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Stop();
                ((DispatcherTimer)typeof(MainWindow).GetField("padTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!).Stop();
                foreach (int height in new[] { 550, 400 })
                {
                    await Check($"Last audio row reaches viewport bottom at height {height}", async () => {
                        window.Height = height; window.UpdateLayout();
                        var scroll = Child<ScrollViewer>(devices)!;
                        scroll.ScrollToEnd();
                        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        window.UpdateLayout();
                        var presenter = Child<ScrollContentPresenter>(scroll)!;
                        var last = (ListBoxItem)devices.ItemContainerGenerator.ContainerFromIndex(devices.Items.Count - 1);
                        double gap = presenter.ActualHeight - last.TranslatePoint(new Point(0, last.ActualHeight), presenter).Y;
                        Console.WriteLine($"  Extent={scroll.ExtentHeight:F1}; viewport={scroll.ViewportHeight:F1}; offset={scroll.VerticalOffset:F1}; bottom gap={gap:F1} DIPs");
                        if (scroll.ExtentHeight > scroll.ViewportHeight && (gap > 8 || gap < -1)) throw new Exception($"Last row leaves {gap:F1} DIPs instead of the normal row spacing");
                    });
                }
                var originalSource = devices.ItemsSource;
                var originalSelection = devices.SelectedItem;
                var move = typeof(MainWindow).GetMethod("Move", BindingFlags.Instance | BindingFlags.NonPublic)!;
                await Check("No upper gap at end of seven-row list from user screenshots", async () => {
                    try
                    {
                        window.Width = 480; window.Height = 550; window.UpdateLayout();
                        devices.ItemsSource = Enumerable.Range(0, 7).Select(i => new DeviceOption($"screenshot-{i}", "Динамики", "Steam Streaming Speakers", i == 0)).ToArray();
                        devices.SelectedIndex = 0;
                        var scroll = Child<ScrollViewer>(devices)!;
                        scroll.ScrollToHome(); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        for (int i = 1; i < devices.Items.Count; i++) { move.Invoke(window, [1]); await Dispatcher.Yield(DispatcherPriority.ContextIdle); }
                        var presenter = Child<ScrollContentPresenter>(scroll)!;
                        double upperGap = Enumerable.Range(0, devices.Items.Count)
                            .Select(i => ((ListBoxItem)devices.ItemContainerGenerator.ContainerFromIndex(i)).TranslatePoint(new Point(), presenter).Y)
                            .First(top => top >= -0.5);
                        Console.WriteLine($"  First full row starts at {upperGap:F2} DIPs below viewport top");
                        if (Math.Abs(upperGap) > 0.5) throw new Exception($"Upper gap remains: {upperGap:F2} DIPs");
                    }
                    finally { devices.ItemsSource = originalSource; devices.SelectedItem = originalSelection; window.UpdateLayout(); }
                });
                foreach (var size in new[] { (480, 550), (480, 400), (340, 550), (340, 400) })
                await Check($"Stable row geometry and edge scrolling at {size.Item1}x{size.Item2}", async () => {
                    try
                    {
                        window.Width = size.Item1; window.Height = size.Item2; window.UpdateLayout();
                        // Different row heights expose extent estimation during first traversal.
                        devices.ItemsSource = Enumerable.Range(0, 20).Select(i => new DeviceOption($"fixture-{i}", $"Устройство {i + 1}", i % 3 == 0 ? string.Join(" ", Enumerable.Repeat("Длинное название аудиоустройства", 5)) : "Короткое название", i == 0)).ToArray();
                        devices.SelectedIndex = 0;
                        var scroll = Child<ScrollViewer>(devices)!;
                        var presenter = Child<ScrollContentPresenter>(scroll)!;
                        async Task Settle() { await Dispatcher.Yield(DispatcherPriority.ContextIdle); window.UpdateLayout(); }
                        scroll.ScrollToHome(); await Settle();
                        double extent = scroll.ExtentHeight;
                        void AssertNear(double actual, double expected, string label)
                        {
                            if (Math.Abs(actual - expected) > 0.5) throw new Exception($"{label}: expected {expected:F2}, actual {actual:F2}");
                        }
                        void AssertGeometry()
                        {
                            AssertNear(scroll.ExtentHeight, extent, "List height changed during navigation");
                            var row = (ListBoxItem)devices.ItemContainerGenerator.ContainerFromIndex(devices.SelectedIndex);
                            double top = row.TranslatePoint(new Point(), presenter).Y;
                            if (row.ActualHeight <= presenter.ActualHeight && (top < -0.5 || top + row.ActualHeight > presenter.ActualHeight + 0.5)) throw new Exception("Selected row is clipped");
                            var visibleRows = Enumerable.Range(0, devices.Items.Count)
                                .Select(index => (ListBoxItem)devices.ItemContainerGenerator.ContainerFromIndex(index))
                                .Select(item => (Item: item, Top: item.TranslatePoint(new Point(), presenter).Y))
                                .Where(item => item.Top + item.Item.ActualHeight > 0.5 && item.Top < presenter.ActualHeight - 0.5).ToArray();
                            AssertNear(visibleRows[0].Top, 0, "First visible row leaves a gap or is clipped at viewport top");
                        }
                        var downwardOffsets = new double[devices.Items.Count];
                        for (int cycle = 0; cycle < 2; cycle++)
                        {
                            for (int i = 1; i < devices.Items.Count; i++)
                            {
                                move.Invoke(window, [1]); await Settle(); AssertGeometry();
                                if (cycle == 0) downwardOffsets[i] = scroll.VerticalOffset;
                                else AssertNear(scroll.VerticalOffset, downwardOffsets[i], $"Row {i} moves on repeated traversal");
                            }
                            AssertNear(scroll.VerticalOffset, scroll.ScrollableHeight, "Last row does not pin viewport to end");
                            double endOffset = scroll.VerticalOffset;
                            var last = (ListBoxItem)devices.ItemContainerGenerator.ContainerFromIndex(devices.Items.Count - 1);
                            double lastTop = last.TranslatePoint(new Point(), presenter).Y;
                            for (int i = 0; i < 3; i++) { move.Invoke(window, [1]); await Settle(); AssertGeometry(); AssertNear(scroll.VerticalOffset, endOffset, "Repeated Down moves list at end"); AssertNear(last.TranslatePoint(new Point(), presenter).Y, lastTop, "Last row shifts at end"); }
                            scroll.ScrollToEnd(); await Settle(); AssertNear(scroll.VerticalOffset, endOffset, "Wheel and selection disagree at end");
                            AssertGeometry();
                            for (int i = devices.Items.Count - 2; i >= 0; i--) { move.Invoke(window, [-1]); await Settle(); AssertGeometry(); }
                            AssertNear(scroll.VerticalOffset, 0, "First row does not pin viewport to start");
                            for (int i = 0; i < 3; i++) { move.Invoke(window, [-1]); await Settle(); AssertGeometry(); AssertNear(scroll.VerticalOffset, 0, "Repeated Up moves list at start"); }
                        }
                    }
                    finally { devices.ItemsSource = originalSource; devices.SelectedItem = originalSelection; window.UpdateLayout(); }
                });
                await Check("Wrapped row taller than viewport remains fully reachable", async () => {
                    try
                    {
                        window.Width = 340; window.Height = 400; window.UpdateLayout();
                        devices.ItemsSource = new[] { new DeviceOption("oversized", "Динамики", string.Join(" ", Enumerable.Repeat("Очень длинное название аудиоустройства", 25)), false) };
                        devices.SelectedIndex = 0;
                        var scroll = Child<ScrollViewer>(devices)!;
                        var panel = Child<DeviceRowsPanel>(devices)!;
                        async Task Settle() { await Dispatcher.Yield(DispatcherPriority.ContextIdle); window.UpdateLayout(); }
                        scroll.ScrollToHome(); await Settle();
                        var row = (ListBoxItem)devices.ItemContainerGenerator.ContainerFromIndex(0);
                        if (row.ActualHeight <= scroll.ViewportHeight) throw new Exception("Fixture does not exceed viewport");
                        panel.LineDown(); await Settle();
                        if (scroll.VerticalOffset <= 0) throw new Exception("Cannot scroll inside oversized row");
                        scroll.ScrollToEnd(); await Settle();
                        if (Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) > 0.5) throw new Exception("Bottom content cannot be reached");
                        scroll.ScrollToHome(); await Settle();
                        panel.MakeVisible(row, new System.Windows.Rect(0, row.ActualHeight - 20, row.ActualWidth, 20)); await Settle();
                        var presenter = Child<ScrollContentPresenter>(scroll)!;
                        double bottom = row.TranslatePoint(new Point(0, row.ActualHeight), presenter).Y;
                        if (bottom > presenter.ActualHeight + 0.5 || bottom < 20) throw new Exception("Requested bottom rectangle remains clipped");
                    }
                    finally { devices.ItemsSource = originalSource; devices.SelectedItem = originalSelection; window.UpdateLayout(); }
                });
                await Check("Program action confirmation defaults to cancel and Back returns through panels", async () => {
                    var switchSection = typeof(MainWindow).GetMethod("SwitchSection", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var programs = (ProgramsView)window.FindName("Programs");
                    try
                    {
                        switchSection.Invoke(window, [AppSection.Programs]);
                        await Task.Delay(500);
                        var list = (ListBox)programs.FindName("RunningList");
                        list.ItemsSource = new[] { new RunningProgram(new(99999, 1), "Test game", new[] { new WindowTarget(new(99999, 1), 1, "Test game", "DISPLAY1", false) }) };
                        list.SelectedIndex = 0;
                        await programs.ConfirmAsync();
                        if (programs.Navigation.Panel != ProgramPanel.Actions) throw new Exception("Actions did not open");
                        var actions = (ListBox)programs.FindName("ProgramActions");
                        actions.SelectedIndex = 1; await programs.ConfirmAsync();
                        if (programs.Navigation.Panel != ProgramPanel.ConfirmTermination || actions.SelectedIndex != 0) throw new Exception("Termination confirmation must default to Cancel");
                        await programs.ConfirmAsync();
                        if (programs.Navigation.Panel != ProgramPanel.Actions) throw new Exception("Default confirmation did not cancel");
                        if (!programs.Back() || programs.Navigation.Panel != ProgramPanel.List) throw new Exception("Back must return to list");
                        programs.SelectMode(true);
                        if (((ListBox)programs.FindName("LaunchList")).Visibility != Visibility.Visible || list.Visibility != Visibility.Collapsed) throw new Exception("Catalog is not separate");
                        programs.SelectMode(false); await programs.ConfirmAsync();
                        actions.SelectedIndex = 1; await programs.ConfirmAsync();
                        if (!programs.Back() || programs.Navigation.Panel != ProgramPanel.Actions || !programs.Back() || programs.Navigation.Panel != ProgramPanel.List) throw new Exception("Nested Back failed");
                        if (programs.Back()) throw new Exception("Main screen must allow application close");
                    }
                    finally
                    {
                        programs.Navigation.Panel = ProgramPanel.List;
                        switchSection.Invoke(window, [AppSection.Audio]);
                    }
                });
                await Check("Saved catalog edit reopens with latest saved values from action panel", async () => {
                    var directory = Path.Combine(Path.GetTempPath(), "AudioSwitcher-EditorChecks-" + Guid.NewGuid());
                    Directory.CreateDirectory(directory);
                    var store = new LaunchCatalogStore(Path.Combine(directory, "programs.json"));
                    var original = new LaunchEntry(Guid.NewGuid(), "Original fixture", LaunchKind.Executable, Environment.ProcessPath!, "--original", directory);
                    store.Save(new(1, [original]));
                    var view = new ProgramsView(store);
                    var host = new Window { Content = view, Width = 480, Height = 550, Owner = window };
                    try
                    {
                        host.Show(); view.Navigation.Section = AppSection.Programs; view.SelectMode(true); view.Enter();
                        ((ListBox)view.FindName("LaunchList")).SelectedIndex = 0;
                        typeof(ProgramsView).GetMethod("ConfigureClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [view, new RoutedEventArgs()]);
                        var firstDrive = DriveEditor(host.Dispatcher, dialog => {
                            ((TextBox)dialog.FindName("EntryName")).Text = "Saved fixture 世界";
                            ((TextBox)dialog.FindName("EntryArguments")).Text = "--saved \"two words\"";
                            ((Button)dialog.FindName("SaveEntry")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        });
                        await view.ConfirmAsync(); await firstDrive;
                        var saved = store.Load().Catalog.Entries.Single();
                        if (saved.Name != "Saved fixture 世界" || saved.Arguments != "--saved \"two words\"") throw new Exception("First edit was not saved");
                        if (view.Navigation.Panel != ProgramPanel.Actions) throw new Exception("Editor did not return to actions");
                        var secondDrive = DriveEditor(host.Dispatcher, dialog => {
                            if (((TextBox)dialog.FindName("EntryName")).Text != saved.Name || ((TextBox)dialog.FindName("EntryArguments")).Text != saved.Arguments)
                                throw new Exception("Reopened editor contains stale values from original catalog entry");
                            dialog.Close();
                        });
                        await view.ConfirmAsync(); await secondDrive;
                    }
                    finally { view.Leave(); host.Close(); Directory.Delete(directory, true); }
                });
                await Check("Native file picker preserves selected shortcut and saved Shortcut target", async () => {
                    if (!args.Contains("--native-picker")) throw new CheckSkipped("This host's native picker provider did not expose filename ValuePattern; native message fallback left modal blocked. Actual shortcut pick check is opt-in --native-picker on an interactive desktop.");
                    var directory = Path.Combine(Path.GetTempPath(), "AudioSwitcher-PickerChecks-" + Guid.NewGuid());
                    Directory.CreateDirectory(directory);
                    var shortcut = Path.Combine(directory, "Own fixture shortcut.lnk");
                    CreateShortcut(shortcut, Environment.ProcessPath!, "--picker-fixture-never-launched");
                    var store = new LaunchCatalogStore(Path.Combine(directory, "programs.json"));
                    LaunchEntry? saved = null;
                    var dialog = new LaunchEntryDialog(null, entry => { store.Save(new(1, [entry])); saved = entry; return Task.CompletedTask; }) { Owner = window };
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(async () => {
                        try
                        {
                            var owner = new WindowInteropHelper(dialog).Handle;
                            var selection = Task.Run(() => SelectNativeFile(owner, shortcut));
                            typeof(LaunchEntryDialog).GetMethod("BrowseClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, [dialog, new RoutedEventArgs()]);
                            await selection;
                            var selected = ((TextBox)dialog.FindName("EntryTarget")).Text;
                            if (!string.Equals(selected, shortcut, StringComparison.OrdinalIgnoreCase)) throw new Exception($"Picker dereferenced shortcut: expected {shortcut}, got {selected}");
                            ((TextBox)dialog.FindName("EntryName")).Text = "Own shortcut";
                            ((Button)dialog.FindName("SaveEntry")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            await WaitForEditorClose(dialog);
                            completion.SetResult();
                        }
                        catch (Exception ex) { dialog.Close(); completion.TrySetException(ex); }
                    }));
                    try
                    {
                        dialog.ShowDialog(); await completion.Task;
                        var persisted = store.Load().Catalog.Entries.Single();
                        if (saved?.Kind != LaunchKind.Shortcut || persisted.Kind != LaunchKind.Shortcut || persisted.Target != shortcut) throw new Exception("Saved catalog lost selected shortcut kind or path");
                    }
                    finally { if (dialog.IsVisible) dialog.Close(); Directory.Delete(directory, true); }
                });
                if (args.Contains("--switch-and-restore"))
                {
                    await Check("Window moves and fits on first confirmation in both directions, including oversized window", async () => {
                        var display = new DisplayService();
                        var monitors = display.GetDevices();
                        string original = monitors.First(d => d.IsDefault).Id;
                        string alternate = monitors.First(d => !d.IsDefault).Id;
                        try
                        {
                            window.Height = 550;
                            typeof(MainWindow).GetMethod("SwitchTab", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [true]);
                            foreach (int width in new[] { 480, 3600 })
                            foreach (string target in new[] { alternate, original })
                            {
                                window.Width = width;
                                window.Height = width == 480 ? 550 : 1800;
                                window.UpdateLayout();
                                devices.SelectedItem = devices.Items.Cast<DeviceOption>().First(d => d.Id == target);
                                await (Task)typeof(MainWindow).GetMethod("ApplySelected", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
                                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                                if (!GetMonitorInfo(MonitorFromWindow(new WindowInteropHelper(window).Handle, 2), ref info)) throw new Exception("Cannot query window monitor");
                                Console.WriteLine($"  Expected={target}; actual={info.Name}");
                                if (info.Name != target) throw new Exception("Window remains on previous monitor after first apply");
                                if (!GetWindowRect(new WindowInteropHelper(window).Handle, out var bounds)) throw new Exception("Cannot query window bounds");
                                if (bounds.Left < info.Work.Left - 1 || bounds.Top < info.Work.Top - 1 || bounds.Right > info.Work.Right + 1 || bounds.Bottom > info.Work.Bottom + 1) throw new Exception("Window is not fully inside target work area after DPI/size adjustment");
                            }
                        }
                        finally { display.SetPrimaryDisplay(original); }
                    });
                }
            }
            catch (Exception ex) { Console.WriteLine(ex); failed++; }
            finally { Console.WriteLine($"Passed: {passed}, Failed: {failed}, Skipped: {skipped + (args.Contains("--switch-and-restore") ? 0 : 1)}"); window.Close(); app.Shutdown(); Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
        }));
        Dispatcher.Run();
        return failed == 0 ? 0 : 1;
    }
    private static T? Child<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (Child<T>(child) is T nested) return nested;
        }
        return null;
    }
    private static Task DriveEditor(Dispatcher dispatcher, Action<LaunchEntryDialog> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.BeginInvoke(new Action(async () => {
            LaunchEntryDialog? dialog = null;
            try
            {
                dialog = Application.Current.Windows.OfType<LaunchEntryDialog>().Single(w => w.IsVisible);
                action(dialog); await WaitForEditorClose(dialog); completion.SetResult();
            }
            catch (Exception ex) { dialog?.Close(); completion.TrySetException(ex); }
        }), DispatcherPriority.ContextIdle);
        return completion.Task;
    }
    private static async Task WaitForEditorClose(LaunchEntryDialog dialog)
    {
        var deadline = Stopwatch.StartNew();
        while (dialog.IsVisible && deadline.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(50);
        if (dialog.IsVisible) throw new Exception("Editor did not close after save: " + ((TextBlock)dialog.FindName("EditorError")).Text);
    }
    private static void CreateShortcut(string path, string target, string arguments)
    {
        object? shell = null, link = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell") ?? throw new Exception("WScript.Shell unavailable"))!;
            link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path])!;
            link.GetType().InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, [target]);
            link.GetType().InvokeMember("Arguments", BindingFlags.SetProperty, null, link, [arguments]);
            link.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        }
        finally { if (link != null) Marshal.FinalReleaseComObject(link); if (shell != null) Marshal.FinalReleaseComObject(shell); }
    }
    private static void SelectNativeFile(IntPtr owner, string path)
    {
        IntPtr picker = IntPtr.Zero;
        var deadline = Stopwatch.StartNew();
        while (picker == IntPtr.Zero && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            EnumWindows((handle, _) => {
                GetWindowThreadProcessId(handle, out uint pid);
                var className = new System.Text.StringBuilder(256); GetClassName(handle, className, className.Capacity);
                // This test has exactly one native modal file dialog. Restrict to our process;
                // COM's file dialog can insert an intermediate owner window.
                if (pid == Environment.ProcessId && IsWindowVisible(handle) && className.ToString() == "#32770") picker = handle;
                return true;
            }, IntPtr.Zero);
            if (picker == IntPtr.Zero) Thread.Sleep(50);
        }
        if (picker == IntPtr.Zero) { PostMessage(owner, 0x0010, IntPtr.Zero, IntPtr.Zero); throw new Exception("Same-process native file picker not found within 10 seconds"); }
        using var cleanup = new CancellationTokenSource();
        var cleanupToken = cleanup.Token;
        _ = Task.Run(async () => {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), cleanupToken);
                EnumWindows((handle, _) => {
                    GetWindowThreadProcessId(handle, out uint pid);
                    var className = new System.Text.StringBuilder(256); GetClassName(handle, className, className.Capacity);
                    if (pid == Environment.ProcessId && IsWindowVisible(handle) && className.ToString() == "#32770") PostMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
                    return true;
                }, IntPtr.Zero);
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            IntPtr filename = IntPtr.Zero, openButton = IntPtr.Zero;
            var controls = new List<string>();
            deadline.Restart();
            while (filename == IntPtr.Zero && deadline.Elapsed < TimeSpan.FromSeconds(3))
            {
                controls.Clear();
                EnumChildWindows(picker, (handle, _) => {
                    var className = new System.Text.StringBuilder(256); GetClassName(handle, className, className.Capacity);
                    int id = GetDlgCtrlID(handle);
                    controls.Add(id + ":" + className);
                    if (id == 1148 && className.ToString() is "ComboBox" or "Edit" or "ComboBoxEx32") filename = handle;
                    if (id == 1 && className.ToString() == "Button") openButton = handle;
                    return true;
                }, IntPtr.Zero);
                if (filename == IntPtr.Zero) Thread.Sleep(50);
            }
            if (filename == IntPtr.Zero) throw new Exception("Native filename control unavailable: " + string.Join(", ", controls));
            if (SendMessageTimeout(filename, 0x000C, IntPtr.Zero, path, 2, 2000, out _) == IntPtr.Zero)
                throw new Exception("Native filename WM_SETTEXT timed out");
            if (openButton == IntPtr.Zero) throw new Exception("Native Open button unavailable");
            PostMessage(openButton, 0x00F5, IntPtr.Zero, IntPtr.Zero);
            deadline.Restart();
            while (IsWindow(picker) && deadline.Elapsed < TimeSpan.FromSeconds(3)) Thread.Sleep(50);
            if (IsWindow(picker)) throw new Exception("Native picker did not accept selected shortcut within 3 seconds");
        }
        catch { PostMessage(picker, 0x0010, IntPtr.Zero, IntPtr.Zero); throw; }
        finally { cleanup.Cancel(); }
    }
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    private sealed class CheckSkipped(string message) : Exception(message);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder className, int count);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MonitorInfo
    {
        public int Size; public Rect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
    }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
