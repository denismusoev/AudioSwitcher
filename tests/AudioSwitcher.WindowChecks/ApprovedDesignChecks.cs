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

                    Check("Default surface uses the approved narrower size", () =>
                    {
                        Require(Math.Abs(window.Width - 1200) < 0.5 && Math.Abs(window.Height - 765) < 0.5,
                            $"Window is {window.Width:0.##}x{window.Height:0.##} instead of 1200x765");
                    });

                    Check("Secondary status text uses the larger readable token", () =>
                    {
                        var status = (TextBlock)window.FindName("Status");
                        Require(Math.Abs(status.FontSize - (double)app.FindResource("Font28")) < 0.01,
                            $"Status font is {status.FontSize:0.##}");
                    });

                    Check("Controller hints have no outer or keycap frames", () =>
                    {
                        var primary = (Button)window.FindName("PrimaryCommand");
                        var settings = (Button)window.FindName("SettingsCommand");
                        primary.ApplyTemplate();
                        window.UpdateLayout();
                        Require(primary.MinHeight >= 42, $"Primary command height is {primary.MinHeight}");
                        Require(primary.Background is SolidColorBrush background && background.Color.A == 0,
                            "Primary command still has a resting fill");
                        Require(primary.BorderBrush is SolidColorBrush outline && outline.Color.A == 0,
                            "Primary command still has a resting outline");
                        var keycap = Descendants<Border>(primary).First(border => Math.Abs(border.MinWidth - (double)app.FindResource("BadgeSize")) < 0.01);
                        Require(keycap.BorderThickness == new Thickness(0), $"Keycap border is still {keycap.BorderThickness}");
                        Require(settings.Tag?.ToString() is "☰", $"Menu badge is still '{settings.Tag}'");
                    });

                    Check("Approved focus outline and fill tokens remain unchanged", () =>
                    {
                        Require((Thickness)app.FindResource("FocusBorderThickness") == new Thickness(3), "Focus outline thickness changed");
                        Require(app.FindResource("SelectedSurface") is SolidColorBrush brush && brush.Color == Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF),
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

                    Check("Launch root has no subtitle and main pages use the menu-height header band", () =>
                    {
                        var controlSurface = (Grid)window.FindName("ControlSurface");
                        Require(Math.Abs(controlSurface.RowDefinitions[0].MinHeight - 80) < 0.01,
                            $"Control header remains {controlSurface.RowDefinitions[0].MinHeight}");
                        Call(window, "SwitchSection", AppSection.Launch);
                        var programs = (ProgramsView)window.FindName("Programs");
                        var listsSurface = (Grid)programs.FindName("ListsSurface");
                        Require(Math.Abs(listsSurface.RowDefinitions[0].MinHeight - 80) < 0.01,
                            $"Program-list header remains {listsSurface.RowDefinitions[0].MinHeight}");
                        Require(programs.FindName("ListSubtitle") == null,
                            "Launch subtitle still occupies the programme header");
                    });

                    Check("Launch path grows the row below an unchanged program title", () =>
                    {
                        Call(window, "SwitchSection", AppSection.Running);
                        var programs = (ProgramsView)window.FindName("Programs");
                        var runningList = (ListBox)programs.FindName("RunningList");
                        runningList.ItemsSource = new[] { new { DisplayName = "Программа" } };
                        window.UpdateLayout();
                        var runningTitle = (TextBlock)programs.FindName("ListTitle");
                        var runningRow = (ListBoxItem)runningList.ItemContainerGenerator.ContainerFromIndex(0);
                        var runningLabel = Descendants<TextBlock>(runningRow).Single(text => text.Text == "Программа");
                        double runningOffset = runningLabel.TranslatePoint(new Point(), programs).Y
                            - runningTitle.TranslatePoint(new Point(), programs).Y;

                        Call(window, "SwitchSection", AppSection.Launch);
                        var launchList = (ListBox)programs.FindName("LaunchList");
                        launchList.ItemsSource = new[] { new { DisplayName = "Программа", DisplayDetails = "Путь" } };
                        window.UpdateLayout();
                        var launchTitle = (TextBlock)programs.FindName("ListTitle");
                        var launchRow = (ListBoxItem)launchList.ItemContainerGenerator.ContainerFromIndex(0);
                        var launchLabel = Descendants<TextBlock>(launchRow).Single(text => text.Text == "Программа");
                        var launchPath = Descendants<TextBlock>(launchRow).Single(text => text.Text == "Путь");
                        double launchOffset = launchLabel.TranslatePoint(new Point(), programs).Y
                            - launchTitle.TranslatePoint(new Point(), programs).Y;

                        Require(Math.Abs(runningOffset - launchOffset) < 0.5,
                            $"Running title offset is {runningOffset:0.##}; launch title offset is {launchOffset:0.##}");
                        Require(launchRow.ActualHeight > runningRow.ActualHeight,
                            $"Launch row height {launchRow.ActualHeight:0.##} did not exceed running row height {runningRow.ActualHeight:0.##}");
                        Require(launchPath.TranslatePoint(new Point(), launchRow).Y >= launchLabel.TranslatePoint(new Point(0, launchLabel.ActualHeight), launchRow).Y,
                            "Launch path does not extend below the program title");
                    });

                    Check("Launch edit command opens the selected entry in the editor", () =>
                    {
                        Call(window, "SwitchSection", AppSection.Control);
                        Call(window, "SwitchSection", AppSection.Launch);
                        var programs = (ProgramsView)window.FindName("Programs");
                        var entry = new LaunchEntry(Guid.NewGuid(), "Редактируемая программа", LaunchKind.Executable, Environment.ProcessPath!, "--before", "");
                        typeof(ProgramsView).GetField("catalog", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .SetValue(programs, new LaunchCatalogLoad(new LaunchCatalog(1, [entry]), true, null));
                        Call(programs, "ShowCatalog", new object?[] { null });
                        Call(window, "Execute", PadAction.Confirm);
                        window.UpdateLayout();

                        var edit = (Button)window.FindName("SecondaryCommand");
                        edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, edit));
                        window.UpdateLayout();

                        Require(programs.Editing && programs.Editor != null && programs.Editor.IsVisible,
                            "Edit command did not open the editor");
                        Require(((TextBox)programs.Editor!.FindName("EntryName")).Text == entry.Name,
                            "Editor did not receive the selected entry");
                        programs.Back();
                    });

                    Check("Delete confirmation uses the device-picker overlay pattern", () =>
                    {
                        Call(window, "SwitchSection", AppSection.Control);
                        Call(window, "SwitchSection", AppSection.Launch);
                        var programs = (ProgramsView)window.FindName("Programs");
                        var entry = new LaunchEntry(Guid.NewGuid(), "Очень длинное название программы для удаления", LaunchKind.Executable, Environment.ProcessPath!, "", "");
                        typeof(ProgramsView).GetField("catalog", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .SetValue(programs, new LaunchCatalogLoad(new LaunchCatalog(1, [entry]), true, null));
                        Call(programs, "ShowCatalog", new object?[] { null });
                        programs.SecondaryAsync().GetAwaiter().GetResult();
                        Require(programs.Editing, $"Editor did not open; section={programs.Navigation.Section}, active={programs.Navigation.SectionActive}, panel={programs.Navigation.Panel}");
                        programs.DeleteEditing();
                        window.UpdateLayout();

                        var picker = (Border)window.FindName("DevicePickerOverlay");
                        var confirmation = (Border)window.FindName("ConfirmationOverlay");
                        var choices = (ListBox)window.FindName("ConfirmationChoices");
                        Require(confirmation.IsVisible, $"Delete confirmation overlay is not visible; editing={programs.Editing}, panel={programs.Navigation.Panel}");
                        Require(Math.Abs(confirmation.ActualWidth - picker.Width) < 0.5,
                            $"Delete confirmation width {confirmation.ActualWidth:0.##} differs from picker width {picker.Width:0.##}");
                        Require(confirmation.Margin == picker.Margin
                            && confirmation.HorizontalAlignment == picker.HorizontalAlignment
                            && confirmation.VerticalAlignment == picker.VerticalAlignment,
                            "Delete confirmation geometry differs from the device picker");
                        Require(choices.SelectedIndex == 0
                            && choices.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem cancel
                            && cancel.IsKeyboardFocused,
                            "Delete confirmation does not focus Cancel by default");
                        var delete = (ListBoxItem)choices.ItemContainerGenerator.ContainerFromIndex(1);
                        var deleteText = Descendants<TextBlock>(delete).First(text => text.Text == "Удалить запись");
                        Require(ReferenceEquals(deleteText.Foreground, app.FindResource("Ps5ErrorText")),
                            "Delete action does not use the danger foreground");
                        var title = (TextBlock)window.FindName("ConfirmationTitle");
                        Require(title.Text.Contains(entry.Name) && title.TextWrapping == TextWrapping.Wrap,
                            "Delete confirmation does not show the full record identity");

                        Call(window, "Execute", PadAction.Close);
                        Require(!confirmation.IsVisible, "Back did not close the delete confirmation");
                    });

                    Check("Editor focus outline does not move field text", () =>
                    {
                        var editor = new LaunchEntryDialog(null, _ => Task.CompletedTask);
                        var host = new Window { Width = 900, Height = 500, Content = editor, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
                        host.Show();
                        host.UpdateLayout();
                        var name = (Border)editor.FindName("NameAttribute");
                        var target = (Border)editor.FindName("TargetAttribute");
                        var label = (Label)editor.FindName("NameLabel");
                        target.Focus(); host.UpdateLayout();
                        Point resting = label.TranslatePoint(new Point(), name);
                        name.Focus(); host.UpdateLayout();
                        Point focused = label.TranslatePoint(new Point(), name);
                        Require(Math.Abs(resting.X - focused.X) < 0.01 && Math.Abs(resting.Y - focused.Y) < 0.01,
                            $"Field text moved from {resting} to {focused}");
                        host.Close();
                    });

                    Check("External window-picker heading uses the shared drawer inset", () =>
                    {
                        Call(window, "SwitchSection", AppSection.Running);
                        var programs = (ProgramsView)window.FindName("Programs");
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
                        var overlay = (Border)window.FindName("WindowPickerOverlay");
                        var title = (TextBlock)window.FindName("WindowPickerTitle");
                        var headingGroup = (FrameworkElement)title.Parent;
                        var titleInset = new Point(
                            overlay.Padding.Left + headingGroup.Margin.Left,
                            overlay.Padding.Top + headingGroup.Margin.Top);
                        Require(titleInset == new Point(27, 28),
                            $"Window drawer title uses {titleInset} instead of the shared 27;28 inset");
                        Call(window, "Execute", PadAction.Close);
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
