using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using AudioSwitcher;
using AudioSwitcher.Controls;
using AudioSwitcher.Core;
using AudioSwitcher.Platform;

internal static class FixedDesignChecks
{
    public static int Run()
    {
        var app = new Application();
        var resources = XDocument.Load("src/AudioSwitcher/App.xaml").Root!.Elements().Single();
        var dictionary = new XElement(XName.Get("ResourceDictionary", "http://schemas.microsoft.com/winfx/2006/xaml/presentation"),
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            new XAttribute(XNamespace.Xmlns + "sys", "clr-namespace:System;assembly=System.Runtime"), resources.Elements());
        dictionary.Add(new XAttribute(XNamespace.Xmlns + "controls", "clr-namespace:AudioSwitcher.Controls;assembly=AudioSwitcher"));
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        var preferencePath = Path.Combine(Path.GetTempPath(), "AudioSwitcher-settings-" + Guid.NewGuid().ToString("N") + ".json");
        var preferences = new ApplicationSettingsStore(preferencePath);
        var window = new MainWindow(preferences); window.Show();
        int failed = 0, passed = 0;
        void Check(string name, Action action)
        {
            try { action(); Console.WriteLine("PASS " + name); passed++; }
            catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e); }
        }
        window.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                foreach (string timer in new[] { "padTimer", "refreshTimer" }) ((DispatcherTimer)Field(window, timer)!).Stop();
                var devices = (ListBox)window.FindName("Devices");
                devices.ItemsSource = Enumerable.Range(0, 12).Select(i => new DeviceOption("design-" + i, "Устройство " + i, "Подпись устройства", i == 0, false)).ToArray();
                devices.SelectedIndex = 0; window.UpdateLayout();
                Check("Window stays 640x700 and cannot resize or maximize", () =>
                {
                    window.Width = 900; window.Height = 900; window.UpdateLayout();
                    Require(window.ActualWidth == 640 && window.ActualHeight == 700 && window.ResizeMode == ResizeMode.NoResize, "Window dimensions/resize mode changed");
                });
                var scroll = Descendants<ScrollViewer>(devices).First();
                Check("Overflowing device list shows a thin right scrollbar and clips hidden rows", () =>
                {
                    scroll.ScrollToBottom(); window.UpdateLayout();
                    Require(scroll.VerticalOffset > 0, "Devices do not scroll");
                    var bar = Descendants<ScrollBar>(devices).Single(s => s.IsVisible);
                    Require(bar.ActualWidth == 4 && bar.TranslatePoint(new Point(), scroll).X > scroll.ActualWidth - 14, "Indicator is not thin/on the right");
                    Require(Descendants<ScrollContentPresenter>(scroll).First().ClipToBounds, "Overflowing rows are not clipped");
                    scroll.ScrollToTop(); window.UpdateLayout();
                });
                Capture(window, "ps5-implemented-sound.png");
                var programs = (ProgramsView)window.FindName("Programs");
                programs.Leave(); programs.Navigation.Section = AppSection.Programs; programs.Navigation.LaunchList = true;
                var entry = new LaunchEntry(Guid.NewGuid(), "Steam", LaunchKind.Executable, Environment.ProcessPath!);
                SetField(programs, "catalog", new LaunchCatalogLoad(new LaunchCatalog(1, new[] { entry }), true, null));
                Call(programs, "ShowCatalog", new object?[] { null }); Call(programs, "UpdateLists");
                ((FrameworkElement)window.FindName("DeviceSurface")).Visibility = Visibility.Collapsed;
                programs.Visibility = Visibility.Visible; Call(window, "UpdateHints"); window.UpdateLayout();
                var list = (ListBox)programs.FindName("LaunchList");
                var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                Check("Short program list has no scroll indicator", () => Require(!Descendants<ScrollBar>(list).Any(s => s.IsVisible), "Short list shows an indicator"));
                Check("Mouse commands remain available without right-click and hover uses gamepad border", () =>
                {
                    Require(((Button)window.FindName("CatalogCommand")).Visibility == Visibility.Visible, "Catalogue inaccessible to mouse");
                    Require(((Button)window.FindName("SaveCommand")).Visibility == Visibility.Collapsed, "Save outside editor");
                    Require(((FrameworkElement)window.FindName("WindowFrame")).ContextMenu == null, "Right-click menu remains");
                    var right = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent };
                    window.RaiseEvent(right); Require(right.Handled, "Right button not blocked");
                    Require(first.Template.Triggers.OfType<Trigger>().Any(t => t.Property == UIElement.IsMouseOverProperty && t.Setters.OfType<Setter>().Any(s => s.TargetName == "Row" && s.Property == Border.BorderBrushProperty)), "Hover has no selection border");
                });
                var reference = Descendants<TextBlock>(first).First().TranslatePoint(new Point(), window);
                var tabs = ((FrameworkElement)window.FindName("SectionHeader")).TranslatePoint(new Point(), window);
                var mode = ((FrameworkElement)programs.FindName("RunningMode")).TranslatePoint(new Point(), window);
                Check("Mode tabs have no underline; mouse and gamepad selection share an unfilled border", () =>
                {
                    var tab = (Button)programs.FindName("RunningMode"); tab.Focus(); window.UpdateLayout();
                    Require(((Border)tab.Template.FindName("TabLine", tab)).BorderThickness == new Thickness(0), "Mode underline remains");
                    Require(((SolidColorBrush)((Border)tab.Template.FindName("TabFocus", tab)).BorderBrush).Color.A == 0, "Tab focus ring remains");
                    list.SelectedIndex = 0; window.UpdateLayout();
                    var row = (Border)first.Template.FindName("Row", first);
                    Require(((SolidColorBrush)row.BorderBrush).Color.A != 0, "Selection outline missing");
                    Require(((SolidColorBrush)row.Background).Color.A == 0, "Selection still has fill");
                });
                Check("Program modes sit above list and status has a divider", () =>
                {
                    Require(mode.Y < reference.Y, "Mode tabs remain below the list");
                    var divider = Ancestor<Border>((FrameworkElement)window.FindName("StatusSurface"));
                    Require(divider?.BorderThickness.Top == 1, "Status divider absent");
                });
                await programs.CatalogAsync(); window.UpdateLayout();
                Check("Nested catalogue title matches first row and preserves app chrome", () =>
                {
                    var title = (TextBlock)programs.FindName("PanelTitle");
                    var point = title.TranslatePoint(new Point(), window);
                    Require(Math.Abs(point.X - reference.X) < 0.2 && Math.Abs(point.Y - reference.Y) < 0.2, $"Title {point} differs from row {reference}");
                    Require(((FrameworkElement)window.FindName("SectionHeader")).TranslatePoint(new Point(), window) == tabs, "Tabs moved");
                    Require(((FrameworkElement)programs.FindName("RunningMode")).IsVisible && ((FrameworkElement)programs.FindName("RunningMode")).TranslatePoint(new Point(), window) == mode, "Mode strip moved/hidden");
                });
                Capture(window, "ps5-implemented-catalogue.png");
                var actions = (ListBox)programs.FindName("ProgramActions");
                var deleteItem = (ListBoxItem)actions.ItemContainerGenerator.ContainerFromIndex(1);
                Click(programs, actions, deleteItem); window.UpdateLayout();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle); window.UpdateLayout();
                Check("One mouse click opens the selected action page", () => Require(programs.Navigation.Panel == ProgramPanel.ConfirmDelete, "First click did not open confirmation"));
                Check("Delete confirmation defaults to cancel and title remains aligned", () =>
                {
                    Require(actions.SelectedIndex == 0 && programs.Navigation.Panel == ProgramPanel.ConfirmDelete, "Delete confirmation default incorrect");
                    Require(!((TextBlock)programs.FindName("PanelWarning")).IsVisible, "Deletion warning remains visible");
                    var point = ((TextBlock)programs.FindName("PanelTitle")).TranslatePoint(new Point(), window);
                    Require(Math.Abs(point.Y - reference.Y) < 0.2, "Confirmation title moved");
                    programs.Back(); window.UpdateLayout(); Require(programs.Navigation.Panel == ProgramPanel.Actions, "Back skipped catalogue");
                });
                actions.SelectedIndex = 0; await programs.ConfirmAsync();
                await Dispatcher.Yield(DispatcherPriority.ContextIdle); window.UpdateLayout();
                var editor = programs.Editor!;
                Check("All editor fields fit without scrolling and title aligns with list", () =>
                {
                    var title = (TextBlock)editor.FindName("EditorTitle");
                    var start = title.TranslatePoint(new Point(), window);
                    Require(Math.Abs(start.X - reference.X) < .2 && Math.Abs(start.Y - reference.Y) < .2, "Editor title differs from first row");
                    var contentScroll = Descendants<ScrollViewer>(editor).First(s => !s.Name.StartsWith("PART_"));
                    contentScroll.ScrollToBottom(); window.UpdateLayout();
                    Require(contentScroll.ScrollableHeight < .2 && contentScroll.VerticalOffset == 0 && title.TranslatePoint(new Point(), window) == start, "Fields require scrolling or title moved");
                    var input = (TextBox)editor.FindName("EntryDirectory"); input.Focus(); window.UpdateLayout();
                    Require(input.BorderThickness == new Thickness(0), "Text input has its own border");
                    var attribute = Ancestor<Border>(input); Require(attribute?.BorderThickness == new Thickness(2), "Attribute border missing");
                    Require(!Descendants<ScrollBar>(window).Any(s => s.IsVisible), "Editor scrollbar visible");
                });
                Capture(window, "ps5-implemented-editor.png");
                Check("Gamepad selects attribute without caret; A activates input and directions stop moving fields", () =>
                {
                    editor.EndFieldInput();
                    programs.Move(-1);
                    Require(!editor.IsTextEditing && System.Windows.Input.Keyboard.FocusedElement is Border, "Selection activates text input");
                    editor.ConfirmField();
                    var input = System.Windows.Input.Keyboard.FocusedElement;
                    Require(editor.IsTextEditing && input is TextBox, "A did not activate input");
                    editor.ConfirmField(); editor.ConfirmField();
                    Require(editor.IsTextEditing && ReferenceEquals(input, System.Windows.Input.Keyboard.FocusedElement), "Repeated A cancelled editing");
                    programs.Move(-1); programs.Move(1);
                    Require(ReferenceEquals(input, System.Windows.Input.Keyboard.FocusedElement), "Directions changed fields during input");
                    Require(programs.Back() && programs.Editing && !editor.IsTextEditing, "B must stop input before leaving editor");
                    programs.Move(-1);
                    Require(System.Windows.Input.Keyboard.FocusedElement is Border, "Navigation did not resume");
                });
                Check("Mouse activates text input with a single click", () =>
                {
                    var attribute = (Border)editor.FindName("NameAttribute");
                    attribute.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left)
                    { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
                    Require(editor.IsTextEditing && ReferenceEquals(System.Windows.Input.Keyboard.FocusedElement, editor.FindName("EntryName")), "Mouse required a second click");
                    editor.EndFieldInput();
                });
                Check("Leaving input has no dotted focus adorner; keyboard arrows Enter Escape mirror gamepad", () =>
                {
                    var attribute = (Border)editor.FindName("NameAttribute");
                    Require(attribute.FocusVisualStyle == null, "System dotted focus visual remains");
                    Key(editor, System.Windows.Input.Key.Down);
                    Require(ReferenceEquals(System.Windows.Input.Keyboard.FocusedElement, editor.FindName("TargetAttribute")), "Down did not select next attribute");
                    Key(editor, System.Windows.Input.Key.Enter);
                    Require(editor.IsTextEditing && ReferenceEquals(System.Windows.Input.Keyboard.FocusedElement, editor.FindName("EntryTarget")), "Enter did not activate input");
                    Key(window, System.Windows.Input.Key.Escape);
                    Require(!editor.IsTextEditing && programs.Editing && ReferenceEquals(System.Windows.Input.Keyboard.FocusedElement, editor.FindName("TargetAttribute")), "Escape did not stop input");
                    Require(((Button)window.FindName("SaveCommand")).Visibility == Visibility.Visible, "Save inaccessible to mouse");
                });
                ((TextBox)editor.FindName("EntryTarget")).Text = "";
                await programs.CatalogAsync();
                Check("Gamepad Y save validates editor; Back restores catalogue", () =>
                {
                    Require(!string.IsNullOrEmpty(((TextBlock)editor.FindName("EditorError")).Text), "Save did not validate");
                    programs.Back(); window.UpdateLayout(); Require(!programs.Editing && programs.InPanel, "Back did not restore catalogue");
                });
                SetField(programs, "currentProgram", new RunningProgram(default, "Steam", new[] { new WindowTarget(default, 1, "WINDOW TITLE MUST NOT APPEAR", "Экран 1", false) }));
                programs.Navigation.LaunchList = false;
                Call(programs, "ShowActions"); window.UpdateLayout();
                Check("Running program card subtitle excludes window title", () =>
                {
                    Require(((TextBlock)programs.FindName("PanelDescription")).Text == "Экран 1", "Card still exposes window title");
                });
                var targets = Enumerable.Range(0, 20).Select(i => new WindowTarget(default, (nint)i, "Окно " + i, "Экран 1", false)).ToArray();
                Call(programs, "ShowPanel", new object?[] { ProgramPanel.Windows, "Какое окно переместить?", "Steam", targets });
                window.UpdateLayout();
                Check("Action page header remains stationary while long window list scrolls", () =>
                {
                    var title = (TextBlock)programs.FindName("PanelTitle"); var start = title.TranslatePoint(new Point(), window);
                    var actionScroll = Descendants<ScrollViewer>(actions).First(); actionScroll.ScrollToBottom(); window.UpdateLayout();
                    Require(actionScroll.VerticalOffset > 0, "Window list cannot scroll");
                    Require(title.TranslatePoint(new Point(), window) == start, "Action header scrolls");
                    Require(Descendants<ScrollBar>(actions).Any(s => s.IsVisible && s.ActualWidth == 4), "Action page indicator missing");
                    var bottom = actions.TranslatePoint(new Point(0, actions.ActualHeight), window).Y;
                    var divider = Ancestor<Border>((FrameworkElement)window.FindName("StatusSurface"))!;
                    Require(divider.TranslatePoint(new Point(), window).Y - bottom >= 20, "List touches status divider");
                });
                Check("Open window list refreshes additions captions and removals while preserving selection and header", () =>
                {
                    actions.SelectedIndex = 5;
                    var header = ((TextBlock)programs.FindName("PanelTitle")).TranslatePoint(new Point(), window);
                    var changed = new[] { targets[1], targets[5] with { Title = "Новое название" }, targets[0] with { Handle = 99, Title = "Новое окно" } };
                    Call(programs, "ApplyRunningSnapshot", new object?[] { new[] { new RunningProgram(default, "Steam", changed) }, true }); window.UpdateLayout();
                    Require(actions.Items.Count == 3 && ((WindowTarget)actions.SelectedItem).Handle == 5 && ((WindowTarget)actions.SelectedItem).Title == "Новое название", "Window snapshot/selection did not update");
                    Require(((TextBlock)programs.FindName("PanelTitle")).TranslatePoint(new Point(), window) == header, "Refresh moved header");
                    Call(programs, "ApplyRunningSnapshot", new object?[] { new[] { new RunningProgram(default, "Steam", new[] { changed[0], changed[2] }) }, true });
                    Require(actions.Items.Count == 2 && ((WindowTarget)actions.SelectedItem).Handle == 1, "Closed selected window remains in list");
                });
                Check("Application card receives latest windows and returns to running list when process disappears", () =>
                {
                    Call(programs, "ShowActions");
                    var remaining = targets[1] with { Screen = "Экран 2" };
                    Call(programs, "ApplyRunningSnapshot", new object?[] { new[] { new RunningProgram(default, "Steam", new[] { remaining }) }, true });
                    Require(((RunningProgram)Field(programs, "currentProgram")!).Windows.Count == 1 && ((TextBlock)programs.FindName("PanelDescription")).Text == "Экран 2", "Actions retain stale windows");
                    Call(programs, "ApplyRunningSnapshot", new object?[] { Array.Empty<RunningProgram>(), true });
                    Require(!programs.InPanel && ((ListBox)programs.FindName("RunningList")).Items.Count == 0, "Closed process leaves stale action page");
                });
                Call(window, "SwitchSection", new object?[] { AppSection.Settings }); window.UpdateLayout();
                Check("Fourth settings section fits fixed window and keeps device surfaces hidden", () =>
                {
                    var tab = (FrameworkElement)window.FindName("SettingsTab");
                    Require(tab.IsVisible && tab.TranslatePoint(new Point(tab.ActualWidth, 0), window).X <= 604, "Fourth tab does not fit");
                    Require(((FrameworkElement)window.FindName("SettingsSurface")).IsVisible && !programs.IsVisible && !devices.IsVisible, "Settings overlap other section");
                    Require(window.ActualWidth == 640 && window.ActualHeight == 700, "Window dimensions changed");
                });
                Check("Settings navigation does not apply until confirm; both choices persist across reload", () =>
                {
                    var toggle = (ToggleButton)window.FindName("MoveBehaviorToggle");
                    var description = (TextBlock)window.FindName("MoveBehaviorDescription");
                    Require(programs.MoveBehavior == WindowMoveBehavior.KeepUtilityFocused, "Default closes utility");
                    Call(window, "Execute", new object?[] { PadAction.Up });
                    Require(toggle.IsChecked == false && programs.MoveBehavior == WindowMoveBehavior.KeepUtilityFocused, "Navigation changed behavior before confirm");
                    Call(window, "Execute", new object?[] { PadAction.Confirm });
                    Require(programs.MoveBehavior == WindowMoveBehavior.ActivateAndClose && preferences.Load().MoveBehavior == WindowMoveBehavior.ActivateAndClose, "Activate/close choice not saved");
                    Require(toggle.IsChecked == true && description.Text.Contains("получит фокус") && description.Text.Contains("закроется"), "Enabled explanation did not update");
                    Call(window, "Execute", new object?[] { PadAction.Down }); Call(window, "Execute", new object?[] { PadAction.Confirm });
                    Require(programs.MoveBehavior == WindowMoveBehavior.KeepUtilityFocused && preferences.Load().MoveBehavior == WindowMoveBehavior.KeepUtilityFocused, "Keep-focus choice not saved");
                    Require(toggle.IsChecked == false && description.Text.Contains("без передачи фокуса") && description.Text.Contains("останется открытым"), "Disabled explanation did not update");
                    Key(window, System.Windows.Input.Key.Enter);
                    Require(toggle.IsChecked == true && preferences.Load().MoveBehavior == WindowMoveBehavior.ActivateAndClose, "Enter did not switch/save setting");
                    var peer = new System.Windows.Automation.Peers.ToggleButtonAutomationPeer(toggle);
                    ((System.Windows.Automation.Provider.IToggleProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle)).Toggle();
                    Require(toggle.IsChecked == false && preferences.Load().MoveBehavior == WindowMoveBehavior.KeepUtilityFocused && description.Text.Contains("без передачи фокуса"), "Native toggle change did not save/update explanation");
                    File.WriteAllText(preferencePath, "invalid json"); Require(preferences.Load().MoveBehavior == WindowMoveBehavior.KeepUtilityFocused, "Malformed settings have unsafe fallback");
                });
                Capture(window, "ps5-implemented-settings.png");
            }
            catch (Exception e) { failed++; Console.WriteLine("FAIL setup: " + e); }
            finally { Console.WriteLine($"Passed: {passed}, Failed: {failed}"); window.Close(); if (File.Exists(preferencePath)) File.Delete(preferencePath); app.Shutdown(); }
        }));
        app.Run(); return failed == 0 ? 0 : 1;
    }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Key(UIElement element, System.Windows.Input.Key key)
    {
        element.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(element), Environment.TickCount, key)
        { RoutedEvent = UIElement.PreviewKeyDownEvent });
    }
    private static void Click(ProgramsView view, ListBox list, ListBoxItem element)
    {
        foreach (var handler in new[] { "ListPress", "ListClick" })
            Call(view, handler, new object?[] { list, new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, System.Windows.Input.MouseButton.Left) { RoutedEvent = handler == "ListPress" ? UIElement.PreviewMouseLeftButtonDownEvent : UIElement.PreviewMouseLeftButtonUpEvent, Source = element } });
    }
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);
    private static void SetField(object instance, string name, object value) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static void Call(object instance, string name, object?[]? args = null) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value; foreach (var next in Descendants<T>(child)) yield return next; }
    }
    private static T? Ancestor<T>(DependencyObject item) where T : DependencyObject { while ((item = VisualTreeHelper.GetParent(item)) != null) if (item is T value) return value; return null; }
    private static void Capture(Window window, string name)
    {
        var frame = (FrameworkElement)window.FindName("WindowFrame");
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(frame.ActualWidth), (int)Math.Ceiling(frame.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(frame);
        Directory.CreateDirectory("artifacts"); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine("artifacts", name)); encoder.Save(file);
    }
}
