using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AudioSwitcher.Core;
using AudioSwitcher.Platform;

namespace AudioSwitcher;

public partial class MainWindow : Window
{
    private enum OverlayMode { None, Devices, Settings, Error }

    private readonly AudioService audio = new();
    private readonly DisplayService display = new();
    private readonly InputGate gate = new();
    private readonly DispatcherTimer padTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly ApplicationSettingsStore settingsStore;
    private readonly InterfaceSettings interfaceSettings;
    private NavigationState navigation => Programs.Navigation;
    private bool busy, padArmed, pickingDisplay;
    private OverlayMode overlay;
    private string? errorDetails;
    private Point pointerStart;
    private bool dragAllowed, dragged;

    public MainWindow() : this(new ApplicationSettingsStore()) { }

    public MainWindow(ApplicationSettingsStore settingsStore)
    {
        this.settingsStore = settingsStore;
        InitializeComponent();
        Devices.Style = (Style)FindResource("TvList");
        SettingsChoices.Style = (Style)FindResource("TvList");
        foreach (var button in new[] { PrimaryCommand, SecondaryCommand, CreateCommand, SettingsCommand, BackCommand })
        { button.FontSize = 14; button.Padding = new Thickness(6, 7, 6, 7); }
        Programs.MoveBehavior = settingsStore.Load().MoveBehavior;
        Programs.TransferCompleted += Close;
        Programs.StatusChanged += SetStatus;
        Programs.ContextChanged += () => { gate.RequireRelease(); UpdateChrome(); };
        interfaceSettings = new InterfaceSettings(Application.Current, UpdateAppearance);
        SizeChanged += (_, _) => UpdateAppearance();
        Loaded += (_, _) =>
        {
            FitToWorkArea();
            navigation.Section = AppSection.Control;
            RefreshControl();
            UpdateChrome();
            AudioControlCard.Focus();
            Activate();
            padTimer.Start();
            refreshTimer.Start();
        };
        Closed += (_, _) => { padTimer.Stop(); refreshTimer.Stop(); Programs.Leave(); interfaceSettings.Dispose(); };
        padTimer.Tick += (_, _) => PollPad();
        refreshTimer.Tick += (_, _) => { if (!busy && overlay == OverlayMode.None) RefreshCurrent(); };
    }

    private void UpdateAppearance() => UpdateChrome();

    private void FitToWorkArea()
    {
        Width = 1220;
        Height = 760;
        WindowState = WindowState.Normal;
        WindowPlacement.Center(new WindowInteropHelper(this).Handle);
    }

    private void SetStatus(string text, string? details = null)
    {
        Status.Text = text;
        errorDetails = details;
        Status.SetResourceReference(TextBlock.ForegroundProperty, details == null ? "MutedText" : "ErrorText");
    }

    private void RefreshCurrent()
    {
        if (navigation.Section == AppSection.Control) RefreshControl(quiet: true);
        else if (navigation.Section == AppSection.Running) _ = Programs.RefreshAsync(quiet: true);
    }

    private void RefreshControl(bool quiet = false)
    {
        try
        {
            var audioItems = audio.GetDevices();
            var displayItems = display.GetDevices();
            AudioSummary.Text = audioItems.FirstOrDefault(x => x.IsDefault)?.DisplayName ?? audioItems.FirstOrDefault()?.DisplayName ?? "Нет доступных устройств";
            DisplaySummary.Text = displayItems.FirstOrDefault(x => x.IsDefault)?.DisplayName ?? displayItems.FirstOrDefault()?.DisplayName ?? "Нет доступных экранов";
            if (!quiet) SetStatus("Готово");
        }
        catch (Exception ex) { SetStatus("Не удалось обновить устройства", ex.Message); }
    }

    private void SwitchSection(AppSection section)
    {
        if (busy || Programs.IsBusy || Programs.InPanel || overlay != OverlayMode.None || navigation.Section == section) return;
        Programs.Leave();
        navigation.Section = section;
        navigation.LaunchList = section == AppSection.Launch;
        ControlSurface.Visibility = section == AppSection.Control ? Visibility.Visible : Visibility.Collapsed;
        Programs.Visibility = section == AppSection.Control ? Visibility.Collapsed : Visibility.Visible;
        gate.RequireRelease();
        if (section == AppSection.Control) { RefreshControl(); AudioControlCard.Focus(); }
        else { Programs.SelectMode(section == AppSection.Launch, force: true); Programs.Enter(); }
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        foreach (var (tab, selected) in new[]
        {
            (ControlTab, navigation.Section == AppSection.Control),
            (RunningTab, navigation.Section == AppSection.Running),
            (LaunchTab, navigation.Section == AppSection.Launch)
        })
        {
            tab.IsSelected = selected;
            tab.SetResourceReference(Control.ForegroundProperty, selected ? "Text" : "MutedText");
        }

        ControlTab.IsEnabled = RunningTab.IsEnabled = LaunchTab.IsEnabled = true;
        bool editing = Programs.Editing;
        bool panel = Programs.InPanel;
        PrimaryCommand.Content = editing ? "A  поле" : navigation.Section == AppSection.Running && !panel ? "A  на экран" : navigation.Section == AppSection.Launch && !panel ? "A  запустить" : "A  выбрать";
        SecondaryCommand.Content = editing ? "X  удалить" : navigation.Section == AppSection.Running ? "X  закрыть" : "X  изменить";
        CreateCommand.Content = editing ? "Y  сохранить" : "Y  добавить";
        SecondaryCommand.Visibility = overlay == OverlayMode.None && !panel && navigation.Section != AppSection.Control || editing ? Visibility.Visible : Visibility.Collapsed;
        CreateCommand.Visibility = overlay == OverlayMode.None && navigation.Section == AppSection.Launch ? Visibility.Visible : Visibility.Collapsed;
        SettingsCommand.Visibility = overlay == OverlayMode.None && !panel ? Visibility.Visible : Visibility.Collapsed;
        BackCommand.Content = overlay == OverlayMode.None && navigation.Section == AppSection.Control ? "B  закрыть" : "B  назад";
    }

    private void OpenDevicePicker(bool displays)
    {
        if (busy || overlay != OverlayMode.None) return;
        try
        {
            pickingDisplay = displays;
            var items = displays ? display.GetDevices() : audio.GetDevices();
            Devices.ItemsSource = items;
            Devices.SelectedItem = items.FirstOrDefault(x => x.IsDefault) ?? items.FirstOrDefault();
            Empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PickerTitle.Text = displays ? "Основной экран" : "Устройство звука";
            PickerSubtitle.Text = displays ? "Выберите экран, который станет главным" : "Выберите устройство вывода по умолчанию";
            ShowOverlay(OverlayMode.Devices);
            Devices.Focus();
            if (Devices.SelectedItem != null) Devices.ScrollIntoView(Devices.SelectedItem);
        }
        catch (Exception ex) { ShowError("Не удалось загрузить устройства", ex.Message); }
    }

    private void OpenSettings()
    {
        SettingsChoices.SelectedIndex = Programs.MoveBehavior == WindowMoveBehavior.KeepUtilityFocused ? 0 : 1;
        ShowOverlay(OverlayMode.Settings);
        SettingsChoices.Focus();
    }

    private void ShowError(string message, string details)
    {
        SetStatus(message, details);
        FullError.Text = details;
        ShowOverlay(OverlayMode.Error);
        ErrorScroll.Focus();
    }

    private void OpenDetails(string? panelDetails = null)
    {
        string? details = panelDetails ?? errorDetails;
        if (string.IsNullOrWhiteSpace(details)) return;
        FullError.Text = details;
        ShowOverlay(OverlayMode.Error);
        ErrorScroll.Focus();
    }

    private void ShowOverlay(OverlayMode mode)
    {
        overlay = mode;
        OverlayShade.Visibility = Visibility.Visible;
        DevicePickerOverlay.Visibility = mode == OverlayMode.Devices ? Visibility.Visible : Visibility.Collapsed;
        SettingsSurface.Visibility = mode == OverlayMode.Settings ? Visibility.Visible : Visibility.Collapsed;
        DetailsSurface.Visibility = mode == OverlayMode.Error ? Visibility.Visible : Visibility.Collapsed;
        gate.RequireRelease();
        UpdateChrome();
    }

    private bool CloseDetails()
    {
        if (overlay == OverlayMode.None) return false;
        overlay = OverlayMode.None;
        OverlayShade.Visibility = Visibility.Collapsed;
        DevicePickerOverlay.Visibility = SettingsSurface.Visibility = DetailsSurface.Visibility = Visibility.Collapsed;
        RestoreFocus();
        gate.RequireRelease();
        UpdateChrome();
        return true;
    }

    private void RestoreFocus()
    {
        if (navigation.Section == AppSection.Control) AudioControlCard.Focus();
        else Programs.RestoreFocus();
    }

    private async Task ApplySelected()
    {
        if (busy || Devices.SelectedItem is not DeviceOption selected) return;
        busy = true;
        UpdateChrome();
        SetStatus(pickingDisplay ? "Смена основного экрана…" : "Переключение звука…");
        await Dispatcher.Yield(DispatcherPriority.Background);
        try
        {
            if (pickingDisplay) display.SetPrimaryDisplay(selected.Id); else audio.SetDefault(selected.Id);
            CloseDetails();
            RefreshControl(quiet: true);
            if (pickingDisplay)
            {
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                FitToWorkArea();
            }
            SetStatus($"Готово: {selected.DisplayName}");
        }
        catch (Exception ex) { ShowError("Не удалось переключить", ex.Message); }
        finally { busy = false; UpdateChrome(); }
    }

    private void ApplySetting()
    {
        if (SettingsChoices.SelectedIndex < 0) return;
        var behavior = SettingsChoices.SelectedIndex == 0 ? WindowMoveBehavior.KeepUtilityFocused : WindowMoveBehavior.ActivateAndClose;
        try
        {
            settingsStore.Save(new(behavior));
            Programs.MoveBehavior = behavior;
            SetStatus("Настройка сохранена");
            CloseDetails();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { ShowError("Не удалось сохранить настройку", ex.Message); }
    }

    private void PollPad()
    {
        bool connected = Gamepad.TryRead(out var action);
        if (!connected || !IsActive) { padArmed = false; gate.Accept(PadAction.None, Environment.TickCount64); return; }
        if (!padArmed) { if (action == PadAction.None) padArmed = true; return; }
        if (gate.Accept(action, Environment.TickCount64)) Execute(action);
    }

    private void Execute(PadAction action)
    {
        if (action == PadAction.Settings && overlay == OverlayMode.None && !Programs.InPanel) { OpenSettings(); return; }
        if (action == PadAction.Close)
        {
            if (CloseDetails()) return;
            if (navigation.Section != AppSection.Control && Programs.Back()) return;
            Close();
            return;
        }
        if (busy || Programs.IsBusy) return;
        if (overlay != OverlayMode.None)
        {
            if (overlay == OverlayMode.Error) { if (action == PadAction.Up) ErrorScroll.LineUp(); else if (action == PadAction.Down) ErrorScroll.LineDown(); else if (action == PadAction.Confirm) CloseDetails(); }
            else if (action == PadAction.Up) MoveOverlay(-1);
            else if (action == PadAction.Down) MoveOverlay(1);
            else if (action == PadAction.Confirm) { if (overlay == OverlayMode.Devices) _ = ApplySelected(); else ApplySetting(); }
            return;
        }
        switch (action)
        {
            case PadAction.PreviousSection: SwitchSection((AppSection)(((int)navigation.Section + 2) % 3)); break;
            case PadAction.NextSection: SwitchSection((AppSection)(((int)navigation.Section + 1) % 3)); break;
            case PadAction.Up: if (navigation.Section == AppSection.Control) MoveControl(-1); else Programs.Move(-1); break;
            case PadAction.Down: if (navigation.Section == AppSection.Control) MoveControl(1); else Programs.Move(1); break;
            case PadAction.Confirm:
                if (navigation.Section == AppSection.Control) OpenDevicePicker(Keyboard.FocusedElement == DisplayControlCard);
                else _ = Programs.ConfirmAsync();
                break;
            case PadAction.Secondary:
                if (Programs.Editing) Programs.DeleteEditing();
                else if (navigation.Section != AppSection.Control) _ = Programs.SecondaryAsync();
                break;
            case PadAction.CreateOrEdit:
                if (Programs.Editing) _ = Programs.CatalogAsync();
                else if (navigation.Section == AppSection.Launch) _ = Programs.CreateAsync();
                else OpenDetails();
                break;
            case PadAction.Details: OpenDetails(Programs.InPanel ? Programs.DetailsText : null); break;
        }
    }

    private void MoveControl(int direction)
    {
        if (direction > 0) DisplayControlCard.Focus(); else AudioControlCard.Focus();
    }

    private void MoveOverlay(int direction)
    {
        var list = overlay == OverlayMode.Devices ? Devices : SettingsChoices;
        if (list.Items.Count == 0) return;
        list.SelectedIndex = Math.Clamp(list.SelectedIndex + direction, 0, list.Items.Count - 1);
        list.Focus();
        list.ScrollIntoView(list.SelectedItem);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Programs.Editing) return;
        var action = e.Key switch
        {
            Key.Escape => PadAction.Close,
            Key.Up => PadAction.Up,
            Key.Down => PadAction.Down,
            Key.Enter => PadAction.Confirm,
            Key.X => PadAction.Secondary,
            Key.Y => PadAction.CreateOrEdit,
            Key.F10 or Key.M => PadAction.Settings,
            Key.F1 => PadAction.Details,
            Key.PageUp => PadAction.PreviousSection,
            Key.PageDown => PadAction.NextSection,
            _ => PadAction.None
        };
        if (action == PadAction.None || (e.IsRepeat && action is PadAction.Confirm or PadAction.Secondary or PadAction.CreateOrEdit or PadAction.Settings)) return;
        Execute(action);
        e.Handled = true;
    }

    private void ControlClick(object sender, RoutedEventArgs e) => SwitchSection(AppSection.Control);
    private void RunningClick(object sender, RoutedEventArgs e) => SwitchSection(AppSection.Running);
    private void LaunchClick(object sender, RoutedEventArgs e) => SwitchSection(AppSection.Launch);
    private void PrimaryCommandClick(object sender, RoutedEventArgs e) => Execute(PadAction.Confirm);
    private void SecondaryCommandClick(object sender, RoutedEventArgs e) => Execute(PadAction.Secondary);
    private void CreateCommandClick(object sender, RoutedEventArgs e) => Execute(PadAction.CreateOrEdit);
    private void SettingsCommandClick(object sender, RoutedEventArgs e) => Execute(PadAction.Settings);
    private void BackCommandClick(object sender, RoutedEventArgs e) => Execute(PadAction.Close);
    private void AudioCardClick(object sender, RoutedEventArgs e) => OpenDevicePicker(false);
    private void DisplayCardClick(object sender, RoutedEventArgs e) => OpenDevicePicker(true);
    private async void ApplyMouseClick(object sender, MouseButtonEventArgs e)
    {
        if (!dragged && ItemsControl.ContainerFromElement(Devices, e.OriginalSource as DependencyObject) is ListBoxItem item)
        { Devices.SelectedItem = item.DataContext; e.Handled = true; await ApplySelected(); }
    }
    private void SettingsMouseClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(SettingsChoices, e.OriginalSource as DependencyObject) is ListBoxItem item)
        { SettingsChoices.SelectedItem = item; e.Handled = true; ApplySetting(); }
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
        if (Math.Abs(position.X - pointerStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - pointerStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragAllowed = false; dragged = true; e.Handled = true; DragMove();
    }
    private void PointerUp(object sender, MouseButtonEventArgs e) { dragAllowed = false; if (dragged) e.Handled = true; }
}
