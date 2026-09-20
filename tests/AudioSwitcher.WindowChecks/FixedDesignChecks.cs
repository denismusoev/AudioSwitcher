using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using AudioSwitcher;
using AudioSwitcher.Controls;
using AudioSwitcher.Core;
using AudioSwitcher.Platform;

internal static class FixedDesignChecks
{
    public static int RunTargetedVisual()
    {
        App? app = null;
        MainWindow? window = null;
        string root = Path.Combine(Path.GetTempPath(), "AudioSwitcher-targeted-design-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            window = new MainWindow(new ApplicationSettingsStore(Path.Combine(root, "settings.json")));
            window.Show();
            var appRoot = (FrameworkElement)window.FindName("AppRoot");
            window.UpdateLayout();
            var background = window.FindName("DecorativeBackground") as Image;
            var backgroundBitmap = background?.Source as BitmapSource;
            Require(background != null
                && backgroundBitmap != null
                && background.Stretch == Stretch.UniformToFill,
                $"Application background is not the bundled full-bleed reference image: element={background?.GetType().Name ?? "null"}, source={background?.Source?.GetType().Name ?? "null"}, pixels={backgroundBitmap?.PixelWidth ?? 0}x{backgroundBitmap?.PixelHeight ?? 0}");
            Require(background!.Effect is BlurEffect { Radius: 15 }
                && background.Margin == new Thickness(-15),
                "Application background does not use the requested 15-level blur with protected edges");
            Console.WriteLine("PASS Application background uses the bundled full-bleed reference image");
            Require(!VisualAncestors(appRoot).OfType<Viewbox>().Any(), "Interactive root is still scaled by a Viewbox");
            Require(Math.Abs(appRoot.ActualWidth - window.ActualWidth) < 1 && Math.Abs(appRoot.ActualHeight - window.ActualHeight) < 1,
                $"Interactive root {appRoot.ActualWidth:0.##}x{appRoot.ActualHeight:0.##} does not fill window {window.ActualWidth:0.##}x{window.ActualHeight:0.##}");
            var focusOutline = ((SolidColorBrush)app.FindResource("FocusOutline")).Color;
            var focusThickness = (Thickness)app.FindResource("FocusBorderThickness");
            var focusCorner = (CornerRadius)app.FindResource("FocusCornerRadius");

            var hints = (FrameworkElement)window.FindName("ControllerHints");
            Require(hints.Visibility == Visibility.Visible, "Gamepad hints are hidden");
            var status = (TextBlock)window.FindName("Status");
            Require(Grid.GetColumn(status) == 0 && status.HorizontalAlignment == HorizontalAlignment.Left, "Status is not aligned in the lower-left column");

            var controlTab = (Button)window.FindName("ControlTab");
            controlTab.GetType().GetProperty("IsSelected")!.SetValue(controlTab, true);
            controlTab.ApplyTemplate();
            var sectionMarker = controlTab.Template.FindName("SectionMarker", controlTab) as FrameworkElement;
            Require(sectionMarker?.Visibility == Visibility.Visible, "Selected section marker is missing");

            var footer = window.FindName("FooterSurface") as FrameworkElement;
            var overlay = window.FindName("OverlayShade") as FrameworkElement;
            Require(footer != null && overlay != null && Panel.GetZIndex(footer) > Panel.GetZIndex(overlay), "Footer is not above overlays");

            var settingsToggle = (ToggleButton)window.FindName("SettingsToggle");
            settingsToggle.ApplyTemplate();
            var toggleState = settingsToggle.Template.FindName("ToggleState", settingsToggle) as TextBlock;
            Require(toggleState?.Text == "Выкл.", "Toggle has no explicit off label");
            settingsToggle.IsChecked = true;
            Require(toggleState!.Text == "Вкл.", "Toggle has no explicit on label");
            Require((double)app.FindResource("BadgeSize") >= 21, "Gamepad badges are too small for TV viewing");
            Require(window.Width <= 1500, $"Window remains too wide at {window.Width}");

            var cleanApplicationName = typeof(ProgramWindowService).GetMethod("CleanApplicationName", BindingFlags.Static | BindingFlags.NonPublic);
            Require(cleanApplicationName?.Invoke(null, ["mspaint.exe"]) as string == "mspaint", "Technical executable suffix is visible");

            var programsView = (ProgramsView)window.FindName("Programs");
            var launchList = (ListBox)programsView.FindName("LaunchList");
            Require(Grid.GetRow(launchList) == 1, "Launch catalogue can overlap its heading");

            var primaryCommand = (Button)window.FindName("PrimaryCommand");
            primaryCommand.ApplyTemplate();
            primaryCommand.Focus();
            window.UpdateLayout();
            var commandFrame = (Border)primaryCommand.Template.FindName("Surface", primaryCommand);
            RequireFocusFrame(commandFrame, focusOutline, focusThickness, focusCorner, "Command");

            controlTab.Focus();
            window.UpdateLayout();
            var sectionFrame = (Border)controlTab.Template.FindName("SelectionFrame", controlTab);
            RequireFocusFrame(sectionFrame, focusOutline, focusThickness, focusCorner, "Section");
            RequirePixelAlignedVerticalEdges(sectionFrame, window, "Section");
            RaiseRightClick(window, controlTab);
            var programs = programsView;
            Require(programs.Navigation.SectionActive, "Right click did not activate the selected element");
            var audio = (Button)window.FindName("AudioControlCard");
            audio.ApplyTemplate();
            window.UpdateLayout();
            var controlFrame = (Border)audio.Template.FindName("FocusFrame", audio);
            RequireFocusFrame(controlFrame, focusOutline, focusThickness, focusCorner, "Content");
            RequirePixelAlignedVerticalEdges(controlFrame, window, "Content");
            Call(window, "Execute", PadAction.Close);

            var display = (Button)window.FindName("DisplayControlCard");
            var divider = window.FindName("ControlRowDivider") as Border ?? throw new Exception("Shared row divider missing");
            var audioCenter = audio.TranslatePoint(new Point(0, audio.ActualHeight / 2), appRoot).Y;
            var displayCenter = display.TranslatePoint(new Point(0, display.ActualHeight / 2), appRoot).Y;
            var dividerCenter = divider.TranslatePoint(new Point(0, divider.ActualHeight / 2), appRoot).Y;
            Require(Math.Abs((dividerCenter - audioCenter) - (displayCenter - dividerCenter)) < 0.6, "Control row divider is not centered");
            var lineColor = ((SolidColorBrush)app.FindResource("Line")).Color;
            Require(VisualBorders((DependencyObject)window.FindName("ControlSurface")).Count(border => border.ActualHeight is > 0 and <= 1.5 && border.Background is SolidColorBrush brush && brush.Color == lineColor) == 1, "Control surface must contain exactly one separator");
            Capture(window, "tv-targeted-control.png");

            Call(window, "SwitchSection", AppSection.Running);
            Require(((TextBlock)window.FindName("Status")).Text == "Загрузка приложений…", "Selecting Running did not start loading its contents");
            Require(!programs.Navigation.SectionActive, "Selecting Running entered its contents");
            Call(window, "Execute", PadAction.Confirm);
            Require(programs.Navigation.SectionActive, "Confirm did not enter the selected section");

            Color focusedRowColor;
            CornerRadius focusedRowCorner;
            programs.Leave();
            using (var process = Process.GetCurrentProcess())
            {
                var running = (ListBox)programs.FindName("RunningList");
                var identity = new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc());
                var first = new RunningProgram(identity, "Первый", [new WindowTarget(identity, (nint)101, "Первый", "Экран 1", false)]);
                var second = new RunningProgram(identity, "Второй", [new WindowTarget(identity, (nint)102, "Второй", "Экран 1", false)]);
                running.ItemsSource = new[] { first, second };
                running.SelectedIndex = 0;
                running.UpdateLayout();
                programs.RestoreFocus();
                programs.Move(1);
                window.UpdateLayout();
                var selectedRow = (ListBoxItem)running.ItemContainerGenerator.ContainerFromIndex(1);
                Require(selectedRow.IsKeyboardFocused, "Moving in a section leaves focus on the list instead of the selected row");
                var rowFrame = VisualChild<Border>(selectedRow) ?? throw new Exception("Selected row frame missing");
                RequireFocusFrame(rowFrame, focusOutline, focusThickness, focusCorner, "List row");
                RequirePixelAlignedVerticalEdges(rowFrame, window, "List row");
                focusedRowColor = (rowFrame.BorderBrush as SolidColorBrush)?.Color ?? throw new Exception("Selected row outline missing");
                focusedRowCorner = rowFrame.CornerRadius;

                Call(window, "Execute", PadAction.Close);
                window.UpdateLayout();
                Require(rowFrame.Background is SolidColorBrush inactiveBackground && inactiveBackground.Color.A == 0,
                    "Selected row remains highlighted after returning to section navigation");
            }
            Require(!programs.Navigation.SectionActive, "Close did not return to section selection");
            Call(window, "SwitchSection", AppSection.Control);
            Call(window, "Execute", PadAction.Confirm);
            window.UpdateLayout();
            var audioFrame = VisualChild<Border>(audio) ?? throw new Exception("Control card frame missing");
            Require(audioFrame.BorderBrush is SolidColorBrush audioBrush && focusedRowColor == audioBrush.Color,
                "Focused rows and control cards use different outline colors");
            Require(focusedRowCorner == audioFrame.CornerRadius,
                "Focused rows and control cards use different corner radii");
            Require(audioFrame.BorderThickness == focusThickness,
                $"Focused control thickness {audioFrame.BorderThickness} differs from token {focusThickness}");

            Require(!VisualText((DependencyObject)window.FindName("ControlSurface")).Any(text => text is "Активно" or "Главный"),
                "Control cards still show redundant state labels");

            Call(window, "Execute", PadAction.Settings);
            window.UpdateLayout();
            Require(settingsToggle.IsKeyboardFocused, "Settings focus did not reach its toggle row");
            var settingsFrame = (Border)settingsToggle.Template.FindName("Row", settingsToggle);
            RequireFocusFrame(settingsFrame, focusOutline, focusThickness, focusCorner, "Settings");
            RequirePixelAlignedVerticalEdges(settingsFrame, window, "Settings");
            Call(window, "Execute", PadAction.Close);
            window.UpdateLayout();

            ((FrameworkElement)window.FindName("OverlayShade")).Visibility = Visibility.Visible;
            var picker = (Border)window.FindName("DevicePickerOverlay");
            picker.Visibility = Visibility.Visible;
            var devices = (ListBox)window.FindName("Devices");
            devices.ItemsSource = new[]
            {
                new DeviceOption("first", "Первое устройство", "Тест", true),
                new DeviceOption("second", "Второе устройство", "Тест", false)
            };
            devices.SelectedIndex = 0;
            window.UpdateLayout();
            Call(window, "FocusSelection", devices);
            window.UpdateLayout();
            var initialDeviceRow = (ListBoxItem)devices.ItemContainerGenerator.ContainerFromIndex(0);
            Require(initialDeviceRow.IsKeyboardFocused, "Device picker does not focus its initial selection");
            Call(window, "MoveOverlay", 1);
            window.UpdateLayout();
            var selectedDeviceRow = (ListBoxItem)devices.ItemContainerGenerator.ContainerFromIndex(1);
            Require(devices.SelectedIndex == 1 && selectedDeviceRow.IsKeyboardFocused,
                "Device picker changes selection but leaves keyboard focus on the list");
            var deviceFrame = VisualChild<Border>(selectedDeviceRow) ?? throw new Exception("Selected device frame missing");
            RequireFocusFrame(deviceFrame, focusOutline, focusThickness, focusCorner, "Device");
            Require(picker.ActualWidth is >= 440 and <= 460, $"Picker width is {picker.ActualWidth}");
            Capture(window, "tv-targeted-picker.png");
            Console.WriteLine("PASS Focus indicators use shared brush and geometry");
            Console.WriteLine("PASS Device picker focuses its initial and moved selection");
            Console.WriteLine("Passed: 3, Failed: 0");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            window?.Close();
            app?.Shutdown();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static IEnumerable<DependencyObject> VisualAncestors(DependencyObject child)
    {
        for (var current = VisualTreeHelper.GetParent(child); current != null; current = VisualTreeHelper.GetParent(current))
            yield return current;
    }

    private static void RequirePixelAlignedVerticalEdges(FrameworkElement frame, Window window, string name)
    {
        var bounds = frame.TransformToAncestor(window).TransformBounds(new Rect(frame.RenderSize));
        var scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
        static bool IsPixelAligned(double value) => Math.Abs(value - Math.Round(value)) < 0.02;
        Require(IsPixelAligned(bounds.Left * scale) && IsPixelAligned(bounds.Right * scale),
            $"{name} vertical edges fall between device pixels: {bounds.Left * scale:0.###}, {bounds.Right * scale:0.###}");
    }

    private static void RaiseRightClick(Window window, UIElement element)
    {
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        {
            RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent,
            Source = element
        };
        Call(window, "RightPointerDown", window, down);
        var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        {
            RoutedEvent = UIElement.PreviewMouseRightButtonUpEvent,
            Source = element
        };
        Call(window, "RightPointerUp", window, up);
    }

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
                    Check("Reference viewport is fixed at 2400 by 1350", () => Require(window.ActualWidth == 2400 && window.ActualHeight == 1350, $"{window.ActualWidth}x{window.ActualHeight}"));
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
                        Require(controlOrigin.X is >= 208 and <= 218, $"Navigation starts at {controlOrigin.X}");
                        Require(control.ActualWidth is >= 492 and <= 502 && running.ActualWidth == control.ActualWidth && launch.ActualWidth == control.ActualWidth, $"Navigation width is {control.ActualWidth}");
                        Require(control.ActualHeight is >= 128 and <= 136, $"Navigation row height is {control.ActualHeight}");
                    });
                    Check("Control actions form stacked settings rows in the content pane", () =>
                    {
                        var audio = (Button)window.FindName("AudioControlCard");
                        var display = (Button)window.FindName("DisplayControlCard");
                        var navigation = (Button)window.FindName("ControlTab");
                        var audioOrigin = audio.TranslatePoint(new Point(), window);
                        var displayOrigin = display.TranslatePoint(new Point(), window);
                        var navigationOrigin = navigation.TranslatePoint(new Point(), window);
                        Require(audioOrigin.X is >= 835 and <= 845, $"Content starts at {audioOrigin.X}");
                        Require(Math.Abs(audioOrigin.X - displayOrigin.X) < 0.5 && displayOrigin.Y >= audioOrigin.Y + audio.ActualHeight, "Settings rows are not vertically aligned");
                        Require(audio.ActualWidth is >= 1310 and <= 1330 && display.ActualWidth == audio.ActualWidth, $"Settings row width is {audio.ActualWidth}");
                        Require(audio.ActualHeight is >= 120 and <= 132 && display.ActualHeight is >= 120 and <= 132, $"Settings row height is {audio.ActualHeight}");
                        Require(((FrameworkElement)window.FindName("ControlSurface")).IsVisible, "Control surface hidden");
                        Require(!VisualText(window).Any(text => text is "Быстрое управление" or "Звук и основной экран — без выхода на рабочий стол"), "Removed control-page introduction is still rendered");
                    });
                    Check("Control actions have one separator centered between the rows", () =>
                    {
                        var audio = (Button)window.FindName("AudioControlCard");
                        var display = (Button)window.FindName("DisplayControlCard");
                        var divider = window.FindName("ControlRowDivider") as Border ?? throw new Exception("Shared row divider missing");
                        var audioCenter = audio.TranslatePoint(new Point(0, audio.ActualHeight / 2), window).Y;
                        var displayCenter = display.TranslatePoint(new Point(0, display.ActualHeight / 2), window).Y;
                        var dividerCenter = divider.TranslatePoint(new Point(0, divider.ActualHeight / 2), window).Y;
                        Require(Math.Abs((dividerCenter - audioCenter) - (displayCenter - dividerCenter)) < 0.6, $"Divider at {dividerCenter} is not centered between {audioCenter} and {displayCenter}");
                        var lineColor = ((SolidColorBrush)app.FindResource("Line")).Color;
                        Require(VisualBorders((DependencyObject)window.FindName("ControlSurface")).Count(border => border.ActualHeight is > 0 and <= 1.5 && border.Background is SolidColorBrush brush && brush.Color == lineColor) == 1, "Expected exactly one visible row separator");
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
                            Require(rowBorder.BorderThickness == restingBorder && restingBorder == new Thickness(3), $"Focus border changes from {restingBorder} to {rowBorder.BorderThickness}");
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
                    Check("Menu opens reference-aligned settings pane with one toggle row", () =>
                    {
                        Require(((FrameworkElement)window.FindName("OverlayShade")).IsVisible && ((FrameworkElement)window.FindName("SettingsSurface")).IsVisible, "Settings overlay missing");
                        Require(((ListBox)window.FindName("SettingsChoices")).SelectedIndex == 0, "Safe keep-open choice is not selected");
                        var settings = (FrameworkElement)window.FindName("SettingsSurface");
                        var settingsOrigin = settings.TranslatePoint(new Point(), window);
                        Require(settingsOrigin.X is >= 835 and <= 845 && settings.ActualWidth is >= 1310 and <= 1330, $"Settings pane is {settingsOrigin.X}, {settings.ActualWidth}");
                        Require(window.FindName("SettingsToggle") is ToggleButton toggle && toggle.IsVisible && toggle.IsKeyboardFocused, "Single settings toggle is not focused");
                        Require(!programs.IsVisible, "Underlying programme surface remains visible through settings pane");
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
                        Require(window.FindName("ModalBackdrop") is FrameworkElement backdrop && backdrop.IsVisible, "Device picker backdrop missing");
                        Require(card.ActualWidth is >= 700 and <= 740 && card.ActualHeight >= 1100, $"Picker is {card.ActualWidth}x{card.ActualHeight}");
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
    private static IEnumerable<Border> VisualBorders(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Border border) yield return border;
            foreach (var nested in VisualBorders(child)) yield return nested;
        }
    }
    private static object? Call(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static void RequireFocusFrame(Border frame, Color outline, Thickness thickness, CornerRadius corner, string name)
    {
        Require(frame.BorderBrush is SolidColorBrush brush && brush.Color == outline,
            $"{name} focus does not use FocusOutline: {frame.BorderBrush}");
        Require(frame.BorderThickness == thickness,
            $"{name} focus thickness {frame.BorderThickness} differs from token {thickness}");
        Require(frame.CornerRadius == corner,
            $"{name} focus corner {frame.CornerRadius} differs from token {corner}");
    }
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
