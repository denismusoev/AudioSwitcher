using System.Reflection;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
        if (args.Contains("--selected-dpi")) return SelectedFixChecks.RunDpi();
        if (args.Contains("--selection-navigation")) return SelectedFixChecks.RunSelectionNavigation();
        if (args.Contains("--selected-fixes")) return SelectedFixChecks.Run();
        if (args.Contains("--targeted-visual")) return FixedDesignChecks.RunTargetedVisual();
        if (args.Contains("--fixed-design")) return FixedDesignChecks.Run();
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
                await Check("Short device cards remain compact at minimum window size", async () => {
                    try
                    {
                        window.Width = 340; window.Height = 400;
                        foreach (bool display in new[] { false, true })
                        {
                            devices.ItemsSource = Enumerable.Range(0, 5).Select(i => new DeviceOption($"density-{i}", display ? $"Экран {i + 1}" : "Динамики", "Короткая подпись", i == 0, display)).ToArray();
                            devices.SelectedIndex = 0;
                            await Dispatcher.Yield(DispatcherPriority.ContextIdle); window.UpdateLayout();
                            Child<ScrollViewer>(devices)!.ScrollToHome();
                            await Dispatcher.Yield(DispatcherPriority.ContextIdle); window.UpdateLayout();
                            var row = (ListBoxItem)devices.ItemContainerGenerator.ContainerFromIndex(0);
                            if (row.ActualHeight > 82) throw new Exception($"Short card stretched to {row.ActualHeight:F1} DIPs");
                            if (args.Contains("--capture-programs")) Capture((FrameworkElement)window.FindName("WindowFrame"), display ? "ui-density-displays-340.png" : "ui-density-audio-340.png");
                        }
                    }
                    finally { devices.ItemsSource = originalSource; devices.SelectedItem = originalSelection; }
                });
                await Check("Program list starts at the same position as device list and shows only essential text", async () => {
                    var switchSection = typeof(MainWindow).GetMethod("SwitchSection", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var programs = (ProgramsView)window.FindName("Programs");
                    try
                    {
                        window.Width = 480; window.Height = 550;
                        switchSection.Invoke(window, [AppSection.Audio]); window.UpdateLayout();
                        var deviceOrigin = devices.TranslatePoint(new Point(), window);
                        switchSection.Invoke(window, [AppSection.Programs]); programs.SelectMode(false);
                        var list = (ListBox)programs.FindName("RunningList");
                        var identity = new ProcessIdentity(27, 37);
                        var fixture = new RunningProgram(identity, "Редактор", new[] { new WindowTarget(identity, 321, "Секретный документ", "Экран 2", false) });
                        list.ItemsSource = new[] { fixture, fixture with { Name = "Браузер" }, fixture with { Name = "Проводник" } }; list.SelectedIndex = 0;
                        ((TextBlock)programs.FindName("EmptyPrograms")).Visibility = Visibility.Collapsed;
                        typeof(MainWindow).GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, ["Готово", null]);
                        await Dispatcher.Yield(DispatcherPriority.ContextIdle); window.UpdateLayout();
                        var origin = list.TranslatePoint(new Point(), window);
                        if (Math.Abs(origin.X - deviceOrigin.X) > 0.5 || Math.Abs(origin.Y - deviceOrigin.Y) > 0.5)
                            throw new Exception($"Program list starts at {origin}, devices at {deviceOrigin}");
                        var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                        if (Descendants<TextBlock>(row).Any(t => t.IsVisible && t.Text == fixture.DisplayDetails))
                            throw new Exception("Normal running row still exposes full technical details");
                        if (args.Contains("--capture-programs")) Capture((FrameworkElement)window.FindName("WindowFrame"), "ui-density-programs-480.png");
                    }
                    finally { switchSection.Invoke(window, [AppSection.Audio]); devices.ItemsSource = originalSource; devices.SelectedItem = originalSelection; }
                });
                await Check("Compact audio picker shows two complete short rows", async () => {
                    window.Width = 340; window.Height = 400;
                    devices.ItemsSource = Enumerable.Range(0, 5).Select(i => new DeviceOption($"compact-{i}", $"Устройство {i}", "Короткая подпись", i == 0)).ToArray();
                    devices.SelectedIndex = 0;
                    await Dispatcher.Yield(DispatcherPriority.ContextIdle); window.UpdateLayout();
                    var scroll = Child<ScrollViewer>(devices)!;
                    var row = (ListBoxItem)devices.ItemContainerGenerator.ContainerFromIndex(1);
                    var presenter = Child<ScrollContentPresenter>(scroll)!;
                    if (row.TranslatePoint(new Point(0, row.ActualHeight), presenter).Y > presenter.ActualHeight + 0.5)
                        throw new Exception("Second short device row is clipped in compact mode");
                    devices.ItemsSource = originalSource; devices.SelectedItem = originalSelection;
                });
                await Check("Program actions replace background list without backdrop", async () => {
                    typeof(MainWindow).GetMethod("SwitchSection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [AppSection.Programs]);
                    var programs = (ProgramsView)window.FindName("Programs");
                    await programs.RefreshAsync();
                    var list = (ListBox)programs.FindName("RunningList");
                    var identity = new ProcessIdentity(12345, 42);
                    list.ItemsSource = new[] { new RunningProgram(identity, "Тестовая программа", new[] { new WindowTarget(identity, 123, "Документ А", "Экран 1", false) }) };
                    list.SelectedIndex = 0;
                    await programs.ConfirmAsync(); window.UpdateLayout();
                    try {
                    if (((Grid)programs.FindName("ListsSurface")).Visibility != Visibility.Collapsed)
                        throw new Exception("Old list remains visible behind actions");
                    if (((Grid)programs.FindName("PanelSurface")).Background != null)
                        throw new Exception("Actions still use a dark backdrop");
                    if (args.Contains("--capture-programs"))
                    {
                        foreach (var size in new[] { (480, 550), (340, 400) })
                        {
                            window.Width = size.Item1; window.Height = size.Item2;
                            await Dispatcher.Yield(DispatcherPriority.ContextIdle); window.UpdateLayout();
                            Capture((FrameworkElement)window.FindName("WindowFrame"), $"ui-redesign-actions-{size.Item1}.png");
                        }
                        var choices = (ListBox)programs.FindName("ProgramActions");
                        choices.SelectedIndex = 1; await programs.ConfirmAsync();
                        await Dispatcher.Yield(DispatcherPriority.ContextIdle); window.UpdateLayout();
                        Capture((FrameworkElement)window.FindName("WindowFrame"), "ui-redesign-confirm-340.png");
                        programs.Back();
                    }
                    } finally { programs.Back();
                    typeof(MainWindow).GetMethod("SwitchSection", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [AppSection.Audio]);
                    devices.ItemsSource = originalSource; devices.SelectedItem = originalSelection; }
                });
                await Check("Catalog editor is an embedded screen", () => {
                    if (!typeof(UserControl).IsAssignableFrom(typeof(LaunchEntryDialog))) throw new Exception("Editor still opens another top-level window");
                    return Task.CompletedTask;
                });
                await Check("Section automation exposes selection separately from focus", () => {
                    var tab = (SectionButton)window.FindName("AudioTab");
                    var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(tab)!;
                    var selection = (System.Windows.Automation.Provider.ISelectionItemProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.SelectionItem)!;
                    if (!selection.IsSelected || peer.GetAutomationControlType() != System.Windows.Automation.Peers.AutomationControlType.TabItem)
                        throw new Exception("Selected section is not exposed as a selected tab");
                    return Task.CompletedTask;
                });
                await Check("Selected device details use the selected system foreground", async () => {
                    object original = app.Resources["SelectedText"];
                    try
                    {
                        app.Resources["SelectedText"] = Brushes.Yellow;
                        devices.ItemsSource = new[] { new DeviceOption("contrast-fixture", "Устройство", "Подпись", true) };
                        devices.SelectedIndex = 0;
                        await Dispatcher.Yield(DispatcherPriority.ContextIdle); window.UpdateLayout();
                        var row = (ListBoxItem)devices.ItemContainerGenerator.ContainerFromIndex(0);
                        var details = Descendants<TextBlock>(row).Single(t => t.Text == "Подпись");
                        if (details.Foreground != Brushes.Yellow) throw new Exception("Secondary text ignores selected foreground and can fail contrast themes");
                    }
                    finally { app.Resources["SelectedText"] = original; devices.ItemsSource = originalSource; devices.SelectedItem = originalSelection; }
                });
                await Check("Enlarged text wraps section navigation within window", async () => {
                    var saved = new Dictionary<string, object>();
                    try
                    {
                        window.Width = 340; window.Height = 800;
                        foreach (int size in new[] { 12, 13, 14, 15, 16, 18, 22, 24 })
                        { string key = $"Font{size}"; saved[key] = app.Resources[key]; app.Resources[key] = size * 2.0; }
                        saved["BadgeSize"] = app.Resources["BadgeSize"]; app.Resources["BadgeSize"] = 36.0;
                        typeof(MainWindow).GetMethod("UpdateAppearance", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                        await Dispatcher.Yield(DispatcherPriority.ContextIdle); window.UpdateLayout();
                        var frame = (FrameworkElement)window.FindName("WindowFrame");
                        foreach (string name in new[] { "AudioTab", "DisplayTab", "ProgramsTab" })
                        {
                            var tab = (Button)window.FindName(name);
                            if (tab.TranslatePoint(new Point(tab.ActualWidth, 0), frame).X > frame.ActualWidth - 8)
                                throw new Exception($"Enlarged tab overflows: {name}");
                        }
                        var selectedRow = (ListBoxItem)devices.ItemContainerGenerator.ContainerFromIndex(devices.SelectedIndex);
                        var deviceName = Descendants<TextBlock>(selectedRow).First(t => t.Text == ((DeviceOption)devices.SelectedItem).DisplayName);
                        if (deviceName.ActualWidth < 240)
                            throw new Exception("Active marker takes width away from enlarged device name");
                        if (args.Contains("--capture-programs")) Capture(frame, "ui-redesign-large-text.png");
                    }
                    finally
                    {
                        foreach (var pair in saved) app.Resources[pair.Key] = pair.Value;
                        window.Width = 480; window.Height = 550;
                        typeof(MainWindow).GetMethod("UpdateAppearance", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                    }
                });
                await Check("Program details identify its window before an action", () => {
                    var identity = new ProcessIdentity(17, 23);
                    var program = new RunningProgram(identity, "Редактор", new[] { new WindowTarget(identity, 123, "Документ А", "Экран 1", false) });
                    if (!program.DisplayDetails.Contains("Документ А")) throw new Exception("Distinctive window title missing from process details");
                    return Task.CompletedTask;
                });
                await Check("Error details open and scroll with controller commands without changing devices", async () => {
                    var setStatus = typeof(MainWindow).GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var execute = typeof(MainWindow).GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    string details = string.Join("\n", Enumerable.Repeat("Ошибка подключения. Проверьте доступность устройства.", 40));
                    setStatus.Invoke(window, ["Ошибка", details]);
                    execute.Invoke(window, [PadAction.Details]);
                    await Dispatcher.Yield(DispatcherPriority.ContextIdle); window.UpdateLayout();
                    if (((TextBlock)window.FindName("FullError")).Text != details || ((Grid)window.FindName("DeviceSurface")).IsVisible)
                        throw new Exception("Details screen did not replace the picker or lost text");
                    execute.Invoke(window, [PadAction.Down]);
                    await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                    if (((ScrollViewer)window.FindName("ErrorScroll")).VerticalOffset <= 0) throw new Exception("Controller cannot scroll long error details");
                    execute.Invoke(window, [PadAction.Close]);
                    if (((Grid)window.FindName("DetailsSurface")).IsVisible || !((Grid)window.FindName("DeviceSurface")).IsVisible) throw new Exception("Back did not restore picker");
                    setStatus.Invoke(window, ["Готово", null]);
                });
                await Check("Editor Back command restores actions without a second window", async () => {
                    var switchSection = typeof(MainWindow).GetMethod("SwitchSection", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var programs = (ProgramsView)window.FindName("Programs");
                    switchSection.Invoke(window, [AppSection.Programs]); programs.SelectMode(true);
                    int windowCount = app.Windows.Count;
                    typeof(ProgramsView).GetMethod("AddClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(programs, [programs, new RoutedEventArgs()]);
                    await Dispatcher.Yield(DispatcherPriority.ContextIdle); window.UpdateLayout();
                    if (!programs.Editing || app.Windows.Count != windowCount || programs.Editor?.IsVisible != true) throw new Exception("Editor is not embedded");
                    foreach (var key in new[] { Key.Left, Key.Right, Key.X })
                    {
                        var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, key);
                        typeof(MainWindow).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(window, [window, input]);
                        if (input.Handled) throw new Exception($"Global navigation consumed editor text key {key}");
                    }
                    programs.Move(1);
                    if (programs.Editor?.IsKeyboardFocusWithin != true) throw new Exception("Controller navigation left the editor");
                    var firstFocus = Keyboard.FocusedElement;
                    programs.Move(1);
                    if (Keyboard.FocusedElement == firstFocus) throw new Exception("Repeated controller Down restarts traversal instead of advancing");
                    programs.Move(-1);
                    if (Keyboard.FocusedElement != firstFocus) throw new Exception("Controller Up does not reverse traversal");
                    if (args.Contains("--capture-programs")) Capture((FrameworkElement)window.FindName("WindowFrame"), "ui-redesign-editor-340.png");
                    typeof(MainWindow).GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [PadAction.Close]);
                    if (programs.Editing || !((Grid)programs.FindName("ListsSurface")).IsVisible) throw new Exception("Controller Back did not restore catalog");
                    programs.SelectMode(false); switchSection.Invoke(window, [AppSection.Audio]);
                    devices.ItemsSource = originalSource; devices.SelectedItem = originalSelection;
                });
                await Check("Catalog error does not steal controller list toggle", async () => {
                    var switchSection = typeof(MainWindow).GetMethod("SwitchSection", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var programs = (ProgramsView)window.FindName("Programs");
                    switchSection.Invoke(window, [AppSection.Programs]); programs.SelectMode(false);
                    typeof(MainWindow).GetMethod("SetStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, ["Ошибка каталога", "Подробности"]);
                    try
                    {
                        typeof(MainWindow).GetMethod("Execute", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [PadAction.ToggleList]);
                        if (!programs.Navigation.LaunchList || ((Grid)window.FindName("DetailsSurface")).IsVisible) throw new Exception("Error steals X instead of changing program list");
                    }
                    finally
                    {
                        typeof(MainWindow).GetMethod("CloseDetails", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                        programs.SelectMode(false); switchSection.Invoke(window, [AppSection.Audio]);
                        devices.ItemsSource = originalSource; devices.SelectedItem = originalSelection;
                    }
                    await Task.CompletedTask;
                });
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
                await Check("Program close action opens window choices without confirmation and Back returns through panels", async () => {
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
                        if (((Button)window.FindName("ProgramsTab")).IsEnabled || ((FrameworkElement)window.FindName("SectionHint")).IsVisible)
                            throw new Exception("Modal must block section tabs and hide unavailable section navigation");
                        var actions = (ListBox)programs.FindName("ProgramActions");
                        actions.SelectedIndex = 1; await programs.ConfirmAsync();
                        if (programs.Navigation.Panel != ProgramPanel.CloseWindows || actions.Items.Count != 2) throw new Exception("Close window picker must contain the window and close-all choice");
                        if (!programs.Back() || programs.Navigation.Panel != ProgramPanel.Actions) throw new Exception("Back must return to actions");
                        if (!programs.Back() || programs.Navigation.Panel != ProgramPanel.List) throw new Exception("Second Back must return to list");
                        window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        if (!list.IsKeyboardFocusWithin || !((Button)window.FindName("ProgramsTab")).IsEnabled)
                            throw new Exception("Back must restore list focus and section navigation");
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
                await Check("Failed move preserves actions and presents a retryable inline result", async () => {
                    var switchSection = typeof(MainWindow).GetMethod("SwitchSection", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var programs = (ProgramsView)window.FindName("Programs");
                    try
                    {
                        switchSection.Invoke(window, [AppSection.Programs]); programs.SelectMode(false);
                        await Task.Delay(500);
                        var list = (ListBox)programs.FindName("RunningList");
                        // Moving this test process is explicitly unsupported; no other process is touched.
                        list.ItemsSource = new[] { new RunningProgram(new(Environment.ProcessId, 1), "Own process", new[] { new WindowTarget(new(Environment.ProcessId, 1), 0, "Fixture", "Экран 1", false) }) };
                        list.SelectedIndex = 0; await programs.ConfirmAsync();
                        await programs.ConfirmAsync();
                        if (programs.Navigation.Panel != ProgramPanel.Actions || programs.IsBusy) throw new Exception("Failure must retain the selected action for retry");
                        if (programs.FindName("PanelFeedback") is not TextBlock feedback || !feedback.IsVisible || string.IsNullOrWhiteSpace(feedback.Text))
                            throw new Exception("Failure must be readable inside the modal");
                        await programs.ConfirmAsync();
                        if (programs.Navigation.Panel != ProgramPanel.Actions || !programs.Back()) throw new Exception("Retry and Back must remain available");
                    }
                    finally { programs.Back(); switchSection.Invoke(window, [AppSection.Audio]); }
                });
                await Check("Window choices remain reachable with long titles at minimum and normal window sizes", async () => {
                    var switchSection = typeof(MainWindow).GetMethod("SwitchSection", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var programs = (ProgramsView)window.FindName("Programs");
                    try
                    {
                        switchSection.Invoke(window, [AppSection.Programs]); programs.SelectMode(false); await Task.Delay(500);
                        foreach (var size in new[] { (340, 400), (480, 550) })
                        {
                            window.Width = size.Item1; window.Height = size.Item2;
                            var list = (ListBox)programs.FindName("RunningList");
                            list.ItemsSource = new[] { new RunningProgram(new(Environment.ProcessId, 1), string.Join(" ", Enumerable.Repeat("Название программы", 20)), Enumerable.Range(0, 8).Select(i => new WindowTarget(new(Environment.ProcessId, 1), i + 1, $"Окно {i + 1}", "Экран 2 · Главный", false)).ToArray()) };
                            list.SelectedIndex = 0; await programs.ConfirmAsync(); await programs.ConfirmAsync();
                            if (programs.Navigation.Panel != ProgramPanel.Windows) throw new Exception("Window chooser missing");
                            var actions = (ListBox)programs.FindName("ProgramActions");
                            for (int i = 0; i < 8; i++) { programs.Move(1); await Dispatcher.Yield(DispatcherPriority.ContextIdle); }
                            window.UpdateLayout();
                            var presenter = Child<ScrollContentPresenter>(actions)!;
                            var row = (ListBoxItem)actions.ItemContainerGenerator.ContainerFromIndex(7);
                            double top = row.TranslatePoint(new Point(), presenter).Y;
                            if (presenter.ActualHeight < 44 || top < -0.5 || top + row.ActualHeight > presenter.ActualHeight + 0.5)
                                throw new Exception($"Final window is clipped at {size}: top={top}, viewport={presenter.ActualHeight}");
                            if (!((TextBlock)window.FindName("BackHint")).IsVisible) throw new Exception("Back control is obscured by the modal");
                            if (args.Contains("--capture-programs"))
                            {
                                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                                bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                                using var output = File.Create(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, $"../../../../../artifacts/ui-ux-window-choices-{size.Item1}.png"))); encoder.Save(output);
                            }
                            programs.Back(); programs.Back();
                        }
                    }
                    finally { programs.Back(); programs.Back(); switchSection.Invoke(window, [AppSection.Audio]); }
                });
                await Check("Editor fields have associated labels and retain visible keyboard focus", async () => {
                    var editor = new LaunchEntryDialog(null, _ => Task.CompletedTask);
                    var editorWindow = new Window { Owner = window, Content = editor, Width = 460, Height = 530 };
                    try
                    {
                        editorWindow.Show(); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        foreach (var name in new[] { "EntryName", "EntryTarget", "EntryArguments", "EntryDirectory" })
                        {
                            var input = (TextBox)editor.FindName(name);
                            var label = System.Windows.Automation.AutomationProperties.GetLabeledBy(input) as Label;
                            if (label?.Target != input) throw new Exception($"Unassociated field label: {name}");
                            input.Focus(); editor.UpdateLayout();
                            if (!input.IsKeyboardFocused || input.BorderBrush != app.FindResource("Accent")) throw new Exception($"Missing focus indicator: {name}");
                        }
                    }
                    finally { editorWindow.Close(); }
                });
                await Check("Single click opens catalog modal actions; repeated edits use latest saved values", async () => {
                    var directory = Path.Combine(Path.GetTempPath(), "AudioSwitcher-EditorChecks-" + Guid.NewGuid());
                    Directory.CreateDirectory(directory);
                    var store = new LaunchCatalogStore(Path.Combine(directory, "programs.json"));
                    var original = new LaunchEntry(Guid.NewGuid(), "Original fixture", LaunchKind.Executable, Environment.ProcessPath!, "--original", directory);
                    store.Save(new(1, [original]));
                    var view = new ProgramsView(store);
                    var hostContent = new Grid { Background = (Brush)app.FindResource("WindowSurface") }; hostContent.Children.Add(view);
                    var host = new Window { Content = hostContent, Width = 480, Height = 550, Owner = window, Background = (Brush)app.FindResource("WindowSurface"), Foreground = (Brush)app.FindResource("Text") };
                    try
                    {
                        host.Show(); view.Navigation.Section = AppSection.Programs; view.SelectMode(true); view.Enter();
                        ((ListBox)view.FindName("LaunchList")).SelectedIndex = 0;
                        // An unavailable target makes accidental immediate launch observable without starting a process.
                        store.Save(new(1, [original with { Target = Path.Combine(directory, "missing.exe") }]));
                        view.Leave(); view.Enter();
                        var catalogList = (ListBox)view.FindName("LaunchList");
                        host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        var catalogItem = (ListBoxItem)catalogList.ItemContainerGenerator.ContainerFromIndex(0);
                        catalogItem.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
                        var menu = (ListBox)view.FindName("ProgramActions");
                        if (view.Navigation.Panel != ProgramPanel.Actions) throw new Exception("Record must open actions instead of launching immediately");
                        var labels = menu.Items.Cast<object>().Select(item => (string)item.GetType().GetProperty("DisplayName")!.GetValue(item)!).ToArray();
                        if (!labels.Take(3).SequenceEqual(new[] { "Запустить", "Редактировать", "Удалить запись" })) throw new Exception("Record actions missing");
                        if (view.FindName("ConfigureProgram") != null) throw new Exception("Separate configure button must be removed");
                        host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        if (((Grid)view.FindName("ListsSurface")).IsHitTestVisible || !((Grid)view.FindName("PanelSurface")).IsVisible) throw new Exception("Actions must block the underlying list");
                        for (int cycle = 0; cycle < 5; cycle++)
                        {
                            if (((ListBox)view.FindName("ProgramActions")).Items.Count != 4 || view.FindName("PanelTitle") is not TextBlock title || string.IsNullOrWhiteSpace(title.Text))
                                throw new Exception($"Modal elements disappeared on cycle {cycle + 1}");
                            if (!view.Back() || view.Navigation.Panel != ProgramPanel.List) throw new Exception($"Modal Back failed on cycle {cycle + 1}");
                            ((ListBox)view.FindName("LaunchList")).SelectedIndex = 0;
                            await view.ConfirmAsync();
                            host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        }
                        if (args.Contains("--capture-programs"))
                        {
                            string imageDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts"));
                            Directory.CreateDirectory(imageDirectory);
                            foreach (var size in new[] { (440, 480, "ui-programs-catalog-modal.png"), (300, 340, "ui-programs-catalog-modal-small.png") })
                            {
                                host.Width = size.Item1; host.Height = size.Item2 + 40; host.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(hostContent.ActualWidth), (int)Math.Ceiling(hostContent.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                                bitmap.Render(hostContent); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                                using var output = File.Create(Path.Combine(imageDirectory, size.Item3)); encoder.Save(output);
                            }
                            host.Width = 480; host.Height = 550; host.UpdateLayout();
                        }
                        string? launchStatus = null; view.StatusChanged += (text, _) => launchStatus = text;
                        await view.ConfirmAsync();
                        if (launchStatus != "Файл программы или ярлыка не найден." || ((TextBlock)view.FindName("PanelFeedback")).Text != launchStatus || ((TextBlock)view.FindName("PanelDescription")).Text != "Выберите действие" || view.Navigation.Panel != ProgramPanel.Actions || ((Grid)view.FindName("ListsSurface")).IsEnabled)
                            throw new Exception("Launch action did not validate its target or released modal blocking");
                        // The actual test EXE is used only for editor validation, never launched here.
                        menu.SelectedIndex = 1;
                        var firstDrive = DriveEditor(host.Dispatcher, dialog => {
                            ((TextBox)dialog.FindName("EntryName")).Text = "Saved fixture 世界";
                            ((TextBox)dialog.FindName("EntryArguments")).Text = "--saved \"two words\"";
                            ((TextBox)dialog.FindName("EntryTarget")).Text = Environment.ProcessPath!;
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
                        menu.SelectedIndex = 1;
                        await view.ConfirmAsync(); await secondDrive;
                        menu.SelectedIndex = 2; await view.ConfirmAsync();
                        if (view.Navigation.Panel != ProgramPanel.ConfirmDelete || menu.SelectedIndex != 0) throw new Exception("Deletion must require confirmation defaulting to Cancel");
                        await view.ConfirmAsync();
                        if (store.Load().Catalog.Entries.Count != 1 || view.Navigation.Panel != ProgramPanel.Actions) throw new Exception("Cancel deleted the record");
                        menu.SelectedIndex = 2; await view.ConfirmAsync(); menu.SelectedIndex = 1; await view.ConfirmAsync();
                        if (store.Load().Catalog.Entries.Count != 0 || view.Navigation.Panel != ProgramPanel.List || !((Grid)view.FindName("ListsSurface")).IsEnabled) throw new Exception("Confirmed deletion failed or list remained blocked");
                    }
                    finally { view.Leave(); host.Close(); Directory.Delete(directory, true); }
                });
                await Check("Single click on running program move action reaches the primary monitor", async () => {
                    MonitorInfo secondary = default;
                    EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) => {
                        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                        if (GetMonitorInfo(monitor, ref info) && (info.Flags & 1) == 0) secondary = info;
                        return true;
                    }, IntPtr.Zero);
                    if (secondary.Size == 0) throw new CheckSkipped("Second monitor unavailable; no system topology changes");
                    string configuration = AppContext.BaseDirectory.Contains("Release") ? "Release" : "Debug";
                    string fixturePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, $"../../../../AudioSwitcher.ProgramFixture/bin/{configuration}/net10.0-windows/AudioSwitcher.ProgramFixture.exe"));
                    using var fixtureProcess = Process.Start(new ProcessStartInfo(fixturePath) { Arguments = "\"UI move fixture\"", UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden })!;
                    var programs = (ProgramsView)window.FindName("Programs");
                    var switchSection = typeof(MainWindow).GetMethod("SwitchSection", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    try
                    {
                        RunningProgram? fixtureProgram = null;
                        for (int i = 0; i < 100 && fixtureProgram == null; i++) { fixtureProgram = (await new ProgramWindowService().GetProgramsAsync()).FirstOrDefault(p => p.Identity.Pid == fixtureProcess.Id); if (fixtureProgram == null) await Task.Delay(100); }
                        if (fixtureProgram == null) throw new Exception("Own fixture not discovered");
                        nint fixtureWindow = fixtureProgram.Windows[0].Handle;
                        SetWindowPos(fixtureWindow, IntPtr.Zero, secondary.Work.Left + 60, secondary.Work.Top + 60, 640, 400, 0x4014);
                        await Task.Delay(700);
                        if (MonitorFromWindow(fixtureWindow, 2) == MonitorFromPoint(new PointNative(0, 0), 1)) throw new Exception("Fixture did not reach secondary monitor");
                        switchSection.Invoke(window, [AppSection.Programs]); programs.SelectMode(false); await programs.RefreshAsync(); await Task.Delay(500);
                        var list = (ListBox)programs.FindName("RunningList"); list.ItemsSource = new[] { fixtureProgram }; list.SelectedIndex = 0;
                        window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        ((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0)).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
                        if (programs.Navigation.Panel != ProgramPanel.Actions) throw new Exception("Single click did not open running actions");
                        var actions = (ListBox)programs.FindName("ProgramActions");
                        window.UpdateLayout(); await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                        ((ListBoxItem)actions.ItemContainerGenerator.ContainerFromIndex(0)).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseUpEvent });
                        // Await the complete UI transition, not just the native worker finishing.
                        for (int i = 0; i < 50 && (programs.IsBusy || programs.Navigation.Panel != ProgramPanel.List); i++) await Task.Delay(100);
                        if (programs.Navigation.Panel != ProgramPanel.List || MonitorFromWindow(fixtureWindow, 2) != MonitorFromPoint(new PointNative(0, 0), 1)) throw new Exception("Single-click move did not complete: " + ((TextBlock)window.FindName("Status")).Text);
                        if (args.Contains("--capture-programs"))
                        {
                            window.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using var output = File.Create(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui-programs-after-move.png"))); encoder.Save(output);
                        }
                    }
                    finally
                    {
                        if (!fixtureProcess.HasExited) { fixtureProcess.Kill(); fixtureProcess.WaitForExit(3000); }
                        programs.Navigation.Panel = ProgramPanel.List; switchSection.Invoke(window, [AppSection.Audio]);
                    }
                });
                await Check("Native file picker preserves selected shortcut and saved Shortcut target", async () => {
                    if (!args.Contains("--native-picker")) throw new CheckSkipped("This host's native picker provider did not expose filename ValuePattern; native message fallback left modal blocked. Actual shortcut pick check is opt-in --native-picker on an interactive desktop.");
                    var directory = Path.Combine(Path.GetTempPath(), "AudioSwitcher-PickerChecks-" + Guid.NewGuid());
                    Directory.CreateDirectory(directory);
                    var shortcut = Path.Combine(directory, "Own fixture shortcut.lnk");
                    CreateShortcut(shortcut, Environment.ProcessPath!, "--picker-fixture-never-launched");
                    var store = new LaunchCatalogStore(Path.Combine(directory, "programs.json"));
                    LaunchEntry? saved = null;
                    var dialog = new LaunchEntryDialog(null, entry => { store.Save(new(1, [entry])); saved = entry; return Task.CompletedTask; });
                    var dialogWindow = new Window { Owner = window, Content = dialog, Width = 460, Height = 530 };
                    var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(async () => {
                        try
                        {
                            var owner = new WindowInteropHelper(dialogWindow).Handle;
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
                        dialogWindow.Show(); await completion.Task;
                        var persisted = store.Load().Catalog.Entries.Single();
                        if (saved?.Kind != LaunchKind.Shortcut || persisted.Kind != LaunchKind.Shortcut || persisted.Target != shortcut) throw new Exception("Saved catalog lost selected shortcut kind or path");
                    }
                    finally { dialogWindow.Close(); Directory.Delete(directory, true); }
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
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void Capture(FrameworkElement view, string name)
    {
        string directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts"));
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name)); encoder.Save(output);
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
                dialog = Application.Current.Windows.Cast<Window>().Select(w => Child<LaunchEntryDialog>(w)).Single(w => w?.IsVisible == true)!;
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
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private record struct PointNative(int X, int Y);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr rect, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(PointNative point, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
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
