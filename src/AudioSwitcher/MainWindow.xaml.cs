using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Interop;
using AudioSwitcher.Core;
using AudioSwitcher.Platform;

namespace AudioSwitcher;
public partial class MainWindow : Window
{
    private readonly AudioService audio = new();
    private readonly DisplayService display = new();
    private readonly InputGate gate = new();
    private readonly DispatcherTimer padTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private NavigationState navigation => Programs.Navigation;
    private bool showingDisplays => navigation.Section == AppSection.Displays;
    private bool showingPrograms => navigation.Section == AppSection.Programs;
    private bool busy, padArmed;
    private string signature = "";
    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => {
            Resources["MarkerTextVisibility"] = ActualWidth < 380 ? Visibility.Collapsed : Visibility.Visible;
            AudioTab.FontSize = DisplayTab.FontSize = ProgramsTab.FontSize = ActualWidth < 420 ? 16 : 22;
        };
        Programs.StatusChanged += (text, details) => { Status.Text = text; Status.ToolTip = details; };
        Programs.ContextChanged += () => { gate.RequireRelease(); UpdateHints(); };
        Loaded += (_, _) => { FitToWorkArea(); Refresh(); Activate(); Devices.Focus(); padTimer.Start(); refreshTimer.Start(); };
        Closed += (_, _) => { padTimer.Stop(); refreshTimer.Stop(); Programs.Leave(); };
        padTimer.Tick += (_, _) => PollPad();
        refreshTimer.Tick += (_, _) => { if (!busy) Refresh(quiet: true); };
    }
    private void FitToWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        // Move first so GetDpiForWindow uses the target monitor's current scaling.
        WindowPlacement.Center(handle);
        var area = WindowPlacement.PrimaryWorkArea();
        double scale = WindowPlacement.Scale(handle);
        MinWidth = Math.Min(340, area.Width / scale);
        MinHeight = Math.Min(400, area.Height / scale);
        Width = Math.Min(Width, area.Width / scale);
        Height = Math.Min(Height, area.Height / scale);
        UpdateLayout();
        WindowPlacement.Center(handle);
    }
    private void Refresh(bool quiet = false, string? preferred = null)
    {
        if (showingPrograms) { _ = Programs.RefreshAsync(quiet); return; }
        try
        {
            var items = showingDisplays ? display.GetDevices() : audio.GetDevices();
            var newSignature = string.Join("|", items.Select(d => $"{d.Id}:{d.Name}:{d.Details}:{d.IsDefault}"));
            if (quiet && signature == newSignature) return;
            string? selected = preferred ?? (Devices.SelectedItem as DeviceOption)?.Id;
            Devices.ItemsSource = items;
            Devices.SelectedItem = items.FirstOrDefault(d => d.Id == selected) ?? items.FirstOrDefault(d => d.IsDefault) ?? items.FirstOrDefault();
            signature = newSignature;
            Empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AudioTab.Foreground = (Brush)FindResource(showingDisplays ? "MutedText" : "Text");
            DisplayTab.Foreground = (Brush)FindResource(showingDisplays ? "Text" : "MutedText");
            ProgramsTab.Foreground = (Brush)FindResource("MutedText");
            AutomationProperties.SetName(Devices, showingDisplays ? "Мониторы" : "Устройства вывода");
            if (!quiet) { Status.Text = "Готово"; Status.ToolTip = null; }
            ScrollSelectionIntoView();
        }
        catch (Exception ex) { Status.Text = $"Не удалось загрузить устройства: {ex.Message}"; }
    }
    private void SwitchTab(bool displays)
    {
        SwitchSection(displays ? AppSection.Displays : AppSection.Audio);
    }
    private void SwitchSection(AppSection section)
    {
        if (busy || Programs.IsBusy || Programs.InPanel || navigation.Section == section) return;
        Programs.Leave(); navigation.Section = section;
        Devices.ItemsSource = null; signature = "";
        DeviceSurface.Visibility = showingPrograms ? Visibility.Collapsed : Visibility.Visible;
        Programs.Visibility = showingPrograms ? Visibility.Visible : Visibility.Collapsed;
        gate.RequireRelease(); UpdateHints();
        if (showingPrograms)
        {
            AudioTab.Foreground = DisplayTab.Foreground = (Brush)FindResource("MutedText");
            ProgramsTab.Foreground = (Brush)FindResource("Text"); Programs.Enter();
        }
        else { Refresh(); Devices.Focus(); }
    }
    private void UpdateHints()
    {
        string label = showingPrograms ? Programs.ActionHint : "Применить";
        ApplyHint.Text = $" / Enter · {label}";
        AutomationProperties.SetName(ApplyHint, $"A / Enter · {label}");
        BackHint.Text = showingPrograms && Programs.InPanel ? " / Esc · Назад" : " / Esc · Закрыть";
        ListHint.Visibility = showingPrograms && !Programs.InPanel ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task ApplySelected()
    {
        if (busy || Devices.SelectedItem is not DeviceOption selected) return;
        busy = true;
        bool changeDisplay = showingDisplays;
        Status.Text = changeDisplay ? "Сохранение…" : "Переключение…";
        Status.ToolTip = null;
        // Let WPF paint the status before entering the synchronous Windows API calls.
        await Dispatcher.Yield(DispatcherPriority.Background);
        try
        {
            if (changeDisplay) display.SetPrimaryDisplay(selected.Id); else audio.SetDefault(selected.Id);
            Refresh(quiet: true, preferred: selected.Id);
            if (changeDisplay)
            {
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                FitToWorkArea();
                // Finish any DPI/layout response to moving between monitors, then
                // center using the final native window size rather than stale DIPs.
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                FitToWorkArea();
            }
            Status.Text = $"Готово: {selected.DisplayName}";
        }
        catch (Exception ex) { Refresh(quiet: true); Status.Text = "Не удалось переключить"; Status.ToolTip = ex.Message; }
        finally { busy = false; }
    }
    private void PollPad()
    {
        bool connected = Gamepad.TryRead(out var action);
        // Ignore a button held while launching the app until all controls are released.
        if (!connected || !IsActive) { padArmed = false; gate.Accept(PadAction.None, Environment.TickCount64); return; }
        if (!padArmed) { if (action == PadAction.None) padArmed = true; return; }
        if (gate.Accept(action, Environment.TickCount64)) Execute(action);
    }
    private void Execute(PadAction action)
    {
        if (action == PadAction.Close) { if (!showingPrograms || !Programs.Back()) Close(); return; }
        if (busy || Programs.IsBusy) return;
        switch (action)
        {
            case PadAction.Left:
            case PadAction.PreviousSection: SwitchSection((AppSection)Math.Max(0, (int)navigation.Section - 1)); break;
            case PadAction.Right:
            case PadAction.NextSection: SwitchSection((AppSection)Math.Min(2, (int)navigation.Section + 1)); break;
            case PadAction.ToggleList: if (showingPrograms) Programs.SelectMode(!navigation.LaunchList); break;
            case PadAction.Up: if (showingPrograms) Programs.Move(-1); else Move(-1); break;
            case PadAction.Down: if (showingPrograms) Programs.Move(1); else Move(1); break;
            case PadAction.Confirm: if (showingPrograms) _ = Programs.ConfirmAsync(); else _ = ApplySelected(); break;
        }
    }
    private void Move(int direction)
    {
        if (Devices.Items.Count == 0) return;
        Devices.SelectedIndex = Math.Clamp(Devices.SelectedIndex + direction, 0, Devices.Items.Count - 1);
        Devices.Focus(); ScrollSelectionIntoView();
    }
    private void ScrollSelectionIntoView()
    {
        if (Devices.SelectedItem == null) return;
        // MakeVisible excludes the row's trailing margin. Pin boundary selections
        // to the same physical limits as mouse-wheel scrolling, including that margin.
        var scroll = Devices.Template.FindName("DeviceScroll", Devices) as ScrollViewer;
        if (scroll != null && Devices.SelectedIndex == 0) scroll.ScrollToTop();
        else if (scroll != null && Devices.SelectedIndex == Devices.Items.Count - 1) scroll.ScrollToBottom();
        else Devices.ScrollIntoView(Devices.SelectedItem);
    }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { if (!e.IsRepeat) Execute(PadAction.Close); e.Handled = true; return; }
        if (e.Key == Key.Enter && Keyboard.FocusedElement is ButtonBase) return;
        if (e.Key == Key.F5 && !busy) { Refresh(); e.Handled = true; return; }
        var action = e.Key switch { Key.Up => PadAction.Up, Key.Down => PadAction.Down, Key.Left => PadAction.Left, Key.Right => PadAction.Right, Key.Enter => PadAction.Confirm, Key.X => PadAction.ToggleList, _ => PadAction.None };
        if (action == PadAction.None || (e.IsRepeat && action is PadAction.Confirm or PadAction.ToggleList)) return;
        Execute(action); e.Handled = true;
    }
    private void DragWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        for (var source = e.OriginalSource as DependencyObject; source != null && source != sender; source = VisualTreeHelper.GetParent(source))
            if (source is ButtonBase) return;
        DragMove();
    }
    private void AudioClick(object sender, RoutedEventArgs e) => SwitchTab(false);
    private void DisplayClick(object sender, RoutedEventArgs e) => SwitchTab(true);
    private void ProgramsClick(object sender, RoutedEventArgs e) => SwitchSection(AppSection.Programs);
    private async void ApplyDoubleClick(object sender, MouseButtonEventArgs e) { if (ItemsControl.ContainerFromElement(Devices, e.OriginalSource as DependencyObject) is ListBoxItem) await ApplySelected(); }
}
