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
    private bool showingSettings => navigation.Section == AppSection.Settings;
    private readonly ApplicationSettingsStore settingsStore;
    private bool updatingMovePreference = true;
    private bool busy, padArmed;
    private Point pointerStart;
    private bool dragAllowed, dragged;
    private readonly InterfaceSettings interfaceSettings;
    private string? errorDetails;
    private bool detailsOpen;
    private string signature = "";
    public MainWindow() : this(new ApplicationSettingsStore()) { }
    public MainWindow(ApplicationSettingsStore settingsStore)
    {
        this.settingsStore = settingsStore;
        InitializeComponent();
        Programs.MoveBehavior = settingsStore.Load().MoveBehavior;
        UpdateMovePreference();
        Programs.TransferCompleted += Close;
        interfaceSettings = new InterfaceSettings(Application.Current, UpdateAppearance);
        SizeChanged += (_, _) => UpdateAppearance();
        Programs.StatusChanged += SetStatus;
        Programs.ContextChanged += () => { gate.RequireRelease(); UpdateHints(); };
        Loaded += (_, _) => { FitToWorkArea(); Refresh(); UpdateHints(); Activate(); Devices.Focus(); padTimer.Start(); refreshTimer.Start(); };
        Closed += (_, _) => { padTimer.Stop(); refreshTimer.Stop(); Programs.Leave(); interfaceSettings.Dispose(); };
        padTimer.Tick += (_, _) => PollPad();
        refreshTimer.Tick += (_, _) => { if (!busy) Refresh(quiet: true); };
    }
    private void UpdateAppearance()
    {
        AudioTab.FontSize = DisplayTab.FontSize = ProgramsTab.FontSize = SettingsTab.FontSize = 24;
        WindowFrame.Padding = new Thickness(36, 28, 36, 24);
        UpdateHints();
    }
    private void SetStatus(string text, string? details = null)
    {
        Status.Text = text; Status.ToolTip = null; errorDetails = details;
        Status.SetResourceReference(TextBlock.ForegroundProperty, details == null ? "Text" : "ErrorText");
        UpdateHints();
    }
    private void OpenDetails(string? panelDetails = null)
    {
        if ((panelDetails == null && (errorDetails == null || Programs.InPanel)) || busy || Programs.IsBusy) return;
        detailsOpen = true; FullError.Text = panelDetails ?? errorDetails;
        DeviceSurface.Visibility = Programs.Visibility = Visibility.Collapsed;
        SettingsSurface.Visibility = Visibility.Collapsed;
        DetailsSurface.Visibility = Visibility.Visible; ErrorScroll.ScrollToTop(); ErrorScroll.Focus(); UpdateHints();
    }
    private bool CloseDetails()
    {
        if (!detailsOpen) return false;
        detailsOpen = false; DetailsSurface.Visibility = Visibility.Collapsed;
        DeviceSurface.Visibility = showingPrograms || showingSettings ? Visibility.Collapsed : Visibility.Visible;
        SettingsSurface.Visibility = showingSettings ? Visibility.Visible : Visibility.Collapsed;
        Programs.Visibility = showingPrograms ? Visibility.Visible : Visibility.Collapsed;
        if (showingSettings) MoveBehaviorToggle.Focus(); else if (!showingPrograms) Devices.Focus(); else Programs.RestoreFocus();
        UpdateHints(); return true;
    }
    private void FitToWorkArea()
    {
        Width = 640; Height = 700;
        WindowState = WindowState.Normal;
        WindowPlacement.Center(new WindowInteropHelper(this).Handle);
    }
    private void Refresh(bool quiet = false, string? preferred = null)
    {
        if (showingSettings) return;
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
            if (!quiet) SetStatus("Готово");
            ScrollSelectionIntoView();
        }
        catch (Exception ex) { SetStatus("Не удалось загрузить устройства", ex.Message); }
    }
    private void SwitchTab(bool displays)
    {
        SwitchSection(displays ? AppSection.Displays : AppSection.Audio);
    }
    private void SwitchSection(AppSection section)
    {
        if (busy || Programs.IsBusy || Programs.InPanel || detailsOpen || navigation.Section == section) return;
        Programs.Leave(); navigation.Section = section;
        Devices.ItemsSource = null; signature = "";
        DeviceSurface.Visibility = showingPrograms || showingSettings ? Visibility.Collapsed : Visibility.Visible;
        SettingsSurface.Visibility = showingSettings ? Visibility.Visible : Visibility.Collapsed;
        Programs.Visibility = showingPrograms ? Visibility.Visible : Visibility.Collapsed;
        gate.RequireRelease(); UpdateHints();
        if (showingSettings) { UpdateMovePreference(); SetStatus("Готово"); MoveBehaviorToggle.Focus(); }
        else if (showingPrograms)
        {
            AudioTab.Foreground = DisplayTab.Foreground = (Brush)FindResource("MutedText");
            ProgramsTab.Foreground = (Brush)FindResource("Text"); Programs.Enter();
        }
        else { Refresh(); Devices.Focus(); }
    }
    private void UpdateHints()
    {
        bool available = !busy && !Programs.IsBusy && !Programs.InPanel && !detailsOpen;
        AudioTab.IsEnabled = DisplayTab.IsEnabled = ProgramsTab.IsEnabled = SettingsTab.IsEnabled = available;
        StatusSurface.Visibility = Visibility.Visible;
        CatalogCommand.Visibility = showingPrograms && navigation.LaunchList && !Programs.InPanel ? Visibility.Visible : Visibility.Collapsed;
        SaveCommand.Visibility = Programs.Editing ? Visibility.Visible : Visibility.Collapsed;
        CatalogCommand.IsEnabled = SaveCommand.IsEnabled = !busy && !Programs.IsBusy;
        BackCommand.Content = detailsOpen || Programs.InPanel ? "Назад" : "Закрыть";
        foreach (var (tab, selected) in new[] { (AudioTab, navigation.Section == AppSection.Audio), (DisplayTab, navigation.Section == AppSection.Displays), (ProgramsTab, showingPrograms), (SettingsTab, showingSettings) })
        {
            tab.SetResourceReference(Control.ForegroundProperty, selected ? "Text" : "MutedText");
            tab.IsSelected = selected;
        }
    }
    private async Task ApplySelected()
    {
        if (busy || Devices.SelectedItem is not DeviceOption selected) return;
        busy = true;
        UpdateHints();
        bool changeDisplay = showingDisplays;
        SetStatus(changeDisplay ? "Сохранение…" : "Переключение…");
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
            SetStatus($"Готово: {selected.DisplayName}");
        }
        catch (Exception ex) { Refresh(quiet: true); SetStatus("Не удалось переключить — Y / F1: подробности", ex.Message); }
        finally { busy = false; UpdateHints(); }
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
        if (action == PadAction.Close) { if (CloseDetails()) return; if (!showingPrograms || !Programs.Back()) Close(); return; }
        if (busy || Programs.IsBusy) return;
        if (detailsOpen)
        {
            if (action == PadAction.Up) ErrorScroll.LineUp();
            else if (action == PadAction.Down) ErrorScroll.LineDown();
            else if (action == PadAction.Confirm) CloseDetails();
            return;
        }
        switch (action)
        {
            case PadAction.Left:
            case PadAction.PreviousSection: SwitchSection((AppSection)Math.Max(0, (int)navigation.Section - 1)); break;
            case PadAction.Right:
            case PadAction.NextSection: SwitchSection((AppSection)Math.Min((int)AppSection.Settings, (int)navigation.Section + 1)); break;
            case PadAction.ToggleList:
                if (showingPrograms && !Programs.InPanel) Programs.SelectMode(!navigation.LaunchList);
                break;
            case PadAction.Details:
                if (showingPrograms) _ = Programs.CatalogAsync();
                else OpenDetails();
                break;
            case PadAction.Up: if (showingSettings) MoveSetting(-1); else if (showingPrograms) Programs.Move(-1); else Move(-1); break;
            case PadAction.Down: if (showingSettings) MoveSetting(1); else if (showingPrograms) Programs.Move(1); else Move(1); break;
            case PadAction.Confirm: if (showingSettings) ApplySetting(); else if (showingPrograms) _ = Programs.ConfirmAsync(); else _ = ApplySelected(); break;
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
        if (Programs.Editing) return;
        if (e.Key == Key.Enter && Keyboard.FocusedElement == MoveBehaviorToggle) { if (!e.IsRepeat) ApplySetting(); e.Handled = true; return; }
        if (e.Key == Key.Enter && Keyboard.FocusedElement is ButtonBase) return;
        if (e.Key == Key.F5 && !busy) { Refresh(); e.Handled = true; return; }
        var action = e.Key switch { Key.Up => PadAction.Up, Key.Down => PadAction.Down, Key.Left => PadAction.Left, Key.Right => PadAction.Right, Key.Enter => PadAction.Confirm, Key.X => PadAction.ToggleList, Key.Y => PadAction.Details, Key.F1 => PadAction.Details, _ => PadAction.None };
        if (action == PadAction.None || (e.IsRepeat && action is PadAction.Confirm or PadAction.ToggleList or PadAction.Details)) return;
        Execute(action); e.Handled = true;
    }
    private void AudioClick(object sender, RoutedEventArgs e) => SwitchTab(false);
    private async void CatalogCommandClick(object sender, RoutedEventArgs e) => await Programs.CatalogAsync();
    private async void SaveCommandClick(object sender, RoutedEventArgs e) => await Programs.CatalogAsync();
    private void BackCommandClick(object sender, RoutedEventArgs e) { Execute(PadAction.Close); }
    private void DisplayClick(object sender, RoutedEventArgs e) => SwitchTab(true);
    private void ProgramsClick(object sender, RoutedEventArgs e) => SwitchSection(AppSection.Programs);
    private void SettingsClick(object sender, RoutedEventArgs e) => SwitchSection(AppSection.Settings);
    private void MoveSetting(int direction) => MoveBehaviorToggle.Focus();
    private void ApplySetting() => MoveBehaviorToggle.IsChecked = Programs.MoveBehavior != WindowMoveBehavior.ActivateAndClose;
    private void UpdateMovePreference()
    {
        updatingMovePreference = true;
        try
        {
            bool enabled = Programs.MoveBehavior == WindowMoveBehavior.ActivateAndClose;
            MoveBehaviorToggle.IsChecked = enabled;
            MoveBehaviorValue.Text = enabled ? "Вкл." : "Выкл.";
            MoveBehaviorDescription.Text = enabled
                ? "Приложение получит фокус. AudioSwitcher закроется после успешного переноса."
                : "Окно переместится без передачи фокуса. AudioSwitcher останется открытым и активным.";
        }
        finally { updatingMovePreference = false; }
    }
    private void MovePreferenceChanged(object sender, RoutedEventArgs e)
    {
        if (updatingMovePreference) return;
        var behavior = MoveBehaviorToggle.IsChecked == true ? WindowMoveBehavior.ActivateAndClose : WindowMoveBehavior.KeepUtilityFocused;
        try { settingsStore.Save(new(behavior)); Programs.MoveBehavior = behavior; UpdateMovePreference(); SetStatus("Настройка сохранена"); }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException) { UpdateMovePreference(); SetStatus("Не удалось сохранить настройку", error.Message); }
    }
    private async void ApplyMouseClick(object sender, MouseButtonEventArgs e)
    {
        if (!dragged && ItemsControl.ContainerFromElement(Devices, e.OriginalSource as DependencyObject) is ListBoxItem item)
        { Devices.SelectedItem = item.DataContext; e.Handled = true; await ApplySelected(); }
    }
    private void IgnoreRightButton(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private void PointerDown(object sender, MouseButtonEventArgs e)
    {
        pointerStart = e.GetPosition(this); dragged = false; dragAllowed = true;
        for (var node = e.OriginalSource as DependencyObject; node != null && node != this; node = VisualTreeHelper.GetParent(node))
            if (node is TextBoxBase or ButtonBase or ScrollBar or Thumb) { dragAllowed = false; break; }
    }
    private void PointerMove(object sender, MouseEventArgs e)
    {
        if (!dragAllowed || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - pointerStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - pointerStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragAllowed = false; dragged = true; e.Handled = true; DragMove();
    }
    private void PointerUp(object sender, MouseButtonEventArgs e) { dragAllowed = false; if (dragged) e.Handled = true; }
}

