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
    private bool showingDisplays, busy, padArmed;
    private string signature = "";
    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Resources["MarkerTextVisibility"] = ActualWidth < 380 ? Visibility.Collapsed : Visibility.Visible;
        Loaded += (_, _) => { FitToWorkArea(); Refresh(); Activate(); Devices.Focus(); padTimer.Start(); refreshTimer.Start(); };
        Closed += (_, _) => { padTimer.Stop(); refreshTimer.Stop(); };
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
            AutomationProperties.SetName(Devices, showingDisplays ? "Мониторы" : "Устройства вывода");
            if (!quiet) { Status.Text = "Готово"; Status.ToolTip = null; }
            ScrollSelectionIntoView();
        }
        catch (Exception ex) { Status.Text = $"Не удалось загрузить устройства: {ex.Message}"; }
    }
    private void SwitchTab(bool displays)
    {
        if (busy || showingDisplays == displays) return;
        showingDisplays = displays; Devices.ItemsSource = null; signature = ""; Refresh(); Devices.Focus();
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
        if (action == PadAction.Close) { Close(); return; }
        if (busy) return;
        switch (action)
        {
            case PadAction.Left: SwitchTab(false); break;
            case PadAction.Right: SwitchTab(true); break;
            case PadAction.Up: Move(-1); break;
            case PadAction.Down: Move(1); break;
            case PadAction.Confirm: _ = ApplySelected(); break;
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
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        if (e.Key == Key.Enter && Keyboard.FocusedElement is ButtonBase) return;
        if (e.Key == Key.F5 && !busy) { Refresh(); e.Handled = true; return; }
        var action = e.Key switch { Key.Up => PadAction.Up, Key.Down => PadAction.Down, Key.Left => PadAction.Left, Key.Right => PadAction.Right, Key.Enter => PadAction.Confirm, _ => PadAction.None };
        if (action == PadAction.None || (e.IsRepeat && action == PadAction.Confirm)) return;
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
    private async void ApplyDoubleClick(object sender, MouseButtonEventArgs e) { if (ItemsControl.ContainerFromElement(Devices, e.OriginalSource as DependencyObject) is ListBoxItem) await ApplySelected(); }
}
