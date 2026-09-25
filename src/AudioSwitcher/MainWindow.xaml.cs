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
    private const double PreferredWindowWidth = 1360;
    private const double PreferredWindowHeight = 765;

    private enum OverlayMode { None, Devices, DeleteConfirmation, Settings, Error }
    private sealed record OverlayChoice(string Id, string DisplayName);

    private readonly AudioService audio = new();
    private readonly DisplayService display = new();
    private readonly InputGate gate = new();
    private readonly DispatcherTimer padTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly ApplicationSettingsStore settingsStore;
    private readonly InterfaceSettings interfaceSettings;
    private NavigationState navigation => Programs.Navigation;
    private bool busy, padArmed, pickingDisplay, controlRefreshing, closed;
    private OverlayMode overlay;
    private OverlayMode overlayReturnMode;
    private UIElement? overlayFocusOrigin;
    private UIElement? overlayReturnFocus;
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
        Programs.MoveBehavior = settingsStore.Load().MoveBehavior;
        Programs.TransferCompleted += Close;
        Programs.StatusChanged += SetStatus;
        Programs.DeleteConfirmationRequested += OpenDeleteConfirmation;
        Programs.ContextChanged += () => { gate.RequireRelease(); UpdateChrome(); };
        interfaceSettings = new InterfaceSettings(Application.Current, UpdateAppearance);
        SizeChanged += (_, _) => UpdateAppearance();
        SourceInitialized += (_, _) => ApplyPreferredSize();
        Loaded += (_, _) =>
        {
            navigation.Section = AppSection.Control;
            _ = RefreshControlAsync();
            UpdateChrome();
            FocusSectionTab();
            Activate();
            padTimer.Start();
            refreshTimer.Start();
        };
        Closed += (_, _) =>
        {
            closed = true;
            padTimer.Stop();
            refreshTimer.Stop();
            Programs.Leave();
            interfaceSettings.Dispose();
        };
        padTimer.Tick += (_, _) => PollPad();
        refreshTimer.Tick += async (_, _) => { if (!busy && overlay == OverlayMode.None) await RefreshCurrentAsync(); };
    }

    private void UpdateAppearance()
    {
        bool compact = ActualWidth > 0 && ActualWidth < 1200;
        WindowFrame.Padding = compact ? new Thickness(32, 32, 32, 76) : new Thickness(64, 48, 80, 68);
        SectionColumn.Width = new GridLength(compact ? 240 : 320);
        GapColumn.Width = new GridLength(compact ? 32 : 48);
        FooterSectionColumn.Width = new GridLength(compact ? 240 : 320);
        FooterGapColumn.Width = new GridLength(compact ? 32 : 48);
        SectionStack.Margin = compact ? new Thickness(0, 8, 0, 0) : new Thickness(32, 8, 0, 0);
        foreach (var tab in new[] { ControlTab, RunningTab, LaunchTab }) tab.Width = compact ? 240 : 280;
        FooterSurface.Margin = compact ? new Thickness(32, 0, 32, 16) : new Thickness(64, 0, 80, 20);
        SettingsSurface.Width = compact ? double.NaN : 840;
        SettingsSurface.HorizontalAlignment = compact ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        SettingsSurface.Margin = compact ? new Thickness(304, 96, 32, 76) : new Thickness(432, 123, 0, 86);
        DevicePickerOverlay.Width = Math.Min(450, Math.Max(280, ActualWidth - 64));
        DevicePickerOverlay.Margin = compact ? new Thickness(0, 32, 32, 76) : new Thickness(0, 40, 40, 86);
        DeleteConfirmationOverlay.Width = DevicePickerOverlay.Width;
        DeleteConfirmationOverlay.Margin = DevicePickerOverlay.Margin;
        UpdateChrome();
    }

    private void ApplyPreferredSize()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var size = WindowPlacement.PreferredSizeInDips(WindowPlacement.WorkAreaForWindow(handle), PreferredWindowWidth, PreferredWindowHeight);
        Width = size.Width;
        Height = size.Height;
    }

    private void SetStatus(string text, string? details = null)
    {
        Status.Text = text;
        errorDetails = details;
        Status.SetResourceReference(TextBlock.ForegroundProperty, details == null ? "MutedText" : "ErrorText");
    }

    private async Task RefreshCurrentAsync()
    {
        if (navigation.Section == AppSection.Control) await RefreshControlAsync(quiet: true);
        else if (navigation.Section == AppSection.Running) await Programs.RefreshAsync(quiet: true);
    }

    public Task SynchronizeGamesAsync(bool quiet = false) => Programs.SynchronizeGamesAsync(quiet);

    private async Task RefreshControlAsync(bool quiet = false)
    {
        if (controlRefreshing || closed) return;
        controlRefreshing = true;
        try
        {
            var snapshot = await Task.Run(() => (Audio: audio.GetDevices(), Displays: display.GetDevices()));
            if (closed || navigation.Section != AppSection.Control) return;
            var audioItems = snapshot.Audio;
            var displayItems = snapshot.Displays;
            var activeAudio = audioItems.FirstOrDefault(x => x.IsDefault) ?? audioItems.FirstOrDefault();
            var activeDisplay = displayItems.FirstOrDefault(x => x.IsDefault) ?? displayItems.FirstOrDefault();
            AudioSummary.Text = activeAudio?.DisplayName ?? "Нет доступных устройств";
            DisplaySummary.Text = activeDisplay?.DisplayName ?? "Нет доступных экранов";
            AutomationProperties.SetName(AudioControlCard, $"Устройство звука: {AudioSummary.Text}");
            AutomationProperties.SetName(DisplayControlCard, $"Основной экран: {DisplaySummary.Text}");
            if (!quiet) SetStatus("Готово");
        }
        catch (Exception ex) { SetStatus("Не удалось обновить устройства", ex.Message); }
        finally { controlRefreshing = false; }
    }

    private void SwitchSection(AppSection section)
    {
        if (busy || Programs.IsBusy || Programs.InPanel || overlay != OverlayMode.None || navigation.SectionActive) return;
        if (navigation.Section == section) { FocusSectionTab(); return; }
        Programs.Leave();
        navigation.Section = section;
        navigation.LaunchList = section == AppSection.Launch;
        SetPrimarySurfaceVisibility(true);
        gate.RequireRelease();
        if (section == AppSection.Control) _ = RefreshControlAsync();
        else
        {
            Programs.SelectMode(section == AppSection.Launch, force: true);
            Programs.Enter(focus: false);
        }
        FocusSectionTab();
        UpdateChrome();
    }

    private void ApplySectionChange()
    {
        Programs.Leave();
        SetPrimarySurfaceVisibility(true);
        gate.RequireRelease();
        if (navigation.Section == AppSection.Control) _ = RefreshControlAsync();
        else
        {
            Programs.SelectMode(navigation.Section == AppSection.Launch, force: true);
            Programs.Enter(focus: false);
        }
        FocusSectionTab();
        UpdateChrome();
    }

    private void ActivateSection(PadAction action = PadAction.Right)
    {
        if (navigation.Navigate(action) != NavigationTransition.EnteredSection) return;
        if (navigation.Section == AppSection.Control) AudioControlCard.Focus();
        else Programs.Enter();
        gate.RequireRelease();
        UpdateChrome();
    }

    private bool LeaveSection(PadAction action = PadAction.Left)
    {
        if (navigation.Navigate(action) != NavigationTransition.LeftSection) return false;
        Programs.ExitCurrentPage();
        FocusSectionTab();
        gate.RequireRelease();
        UpdateChrome();
        return true;
    }

    private void FocusSectionTab()
    {
        (navigation.Section switch { AppSection.Running => RunningTab, AppSection.Launch => LaunchTab, _ => ControlTab }).Focus();
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
            bool showSelection = overlay != OverlayMode.Settings && selected;
            tab.IsSelected = showSelection;
            tab.SetResourceReference(Control.ForegroundProperty, showSelection ? "Text" : "MutedText");
        }

        ControlTab.IsEnabled = RunningTab.IsEnabled = LaunchTab.IsEnabled = true;
        bool editing = Programs.Editing;
        bool panel = Programs.InPanel;
        bool sectionActive = navigation.SectionActive;
        PrimaryCommand.Content = overlay == OverlayMode.Settings ? "переключить" : overlay == OverlayMode.Error ? "закрыть" : !sectionActive ? "открыть" : editing ? "поле" : navigation.Section == AppSection.Running && !panel ? "на экран" : navigation.Section == AppSection.Launch && !panel ? "запустить" : "выбрать";
        SecondaryCommand.Content = editing ? "удалить" : navigation.Section == AppSection.Running ? "закрыть" : "изменить";
        CreateCommand.Content = editing ? "сохранить" : "добавить";
        SecondaryCommand.Visibility = overlay == OverlayMode.None && sectionActive && (editing ? Programs.CanDeleteEditing
            : !panel && (navigation.Section == AppSection.Running || navigation.Section == AppSection.Launch && Programs.CanEditSelectedLaunchEntry))
            ? Visibility.Visible : Visibility.Collapsed;
        CreateCommand.Visibility = overlay == OverlayMode.None && sectionActive && (editing || !panel && navigation.Section == AppSection.Launch) ? Visibility.Visible : Visibility.Collapsed;
        SettingsCommand.Visibility = overlay == OverlayMode.None && !panel ? Visibility.Visible : Visibility.Collapsed;
        BackCommand.Content = overlay == OverlayMode.None && !sectionActive ? "закрыть" : "назад";
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
            FocusSelection(Devices);
        }
        catch (Exception ex) { ShowError("Не удалось загрузить устройства", ex.Message); }
    }

    private void OpenDeleteConfirmation(string entryName)
    {
        DeleteConfirmationTitle.Text = $"Удалить запись «{entryName}»?";
        DeleteConfirmationChoices.ItemsSource = new[]
        {
            new OverlayChoice("cancel", "Отмена"),
            new OverlayChoice("delete", "Удалить запись")
        };
        DeleteConfirmationChoices.SelectedIndex = 0;
        ShowOverlay(OverlayMode.DeleteConfirmation);
        FocusSelection(DeleteConfirmationChoices);
    }

    private void OpenSettings()
    {
        SettingsChoices.SelectedIndex = Programs.MoveBehavior == WindowMoveBehavior.KeepUtilityFocused ? 0 : 1;
        SettingsToggle.IsChecked = SettingsChoices.SelectedIndex == 1;
        UpdateSettingsAutomationName();
        ShowOverlay(OverlayMode.Settings);
        SettingsToggle.Focus();
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
        if (overlay == OverlayMode.None) overlayFocusOrigin = Keyboard.FocusedElement as UIElement;
        else if (mode == OverlayMode.Error && overlay != OverlayMode.Error)
        {
            overlayReturnMode = overlay;
            overlayReturnFocus = Keyboard.FocusedElement as UIElement;
        }
        overlay = mode;
        OverlayShade.Visibility = Visibility.Visible;
        DevicePickerOverlay.Visibility = mode == OverlayMode.Devices ? Visibility.Visible : Visibility.Collapsed;
        DeleteConfirmationOverlay.Visibility = mode == OverlayMode.DeleteConfirmation ? Visibility.Visible : Visibility.Collapsed;
        SettingsSurface.Visibility = mode == OverlayMode.Settings ? Visibility.Visible : Visibility.Collapsed;
        DetailsSurface.Visibility = mode == OverlayMode.Error ? Visibility.Visible : Visibility.Collapsed;
        ModalBackdrop.Visibility = mode is OverlayMode.Devices or OverlayMode.DeleteConfirmation ? Visibility.Visible : Visibility.Collapsed;
        if (mode == OverlayMode.Settings) SetPrimarySurfaceVisibility(false);
        WindowFrame.IsEnabled = false;
        gate.RequireRelease();
        UpdateChrome();
    }

    private bool CloseDetails(bool cancelDeleteConfirmation = true)
    {
        if (overlay == OverlayMode.None) return false;
        if (overlay == OverlayMode.Error && overlayReturnMode != OverlayMode.None)
        {
            overlay = overlayReturnMode;
            overlayReturnMode = OverlayMode.None;
            DevicePickerOverlay.Visibility = overlay == OverlayMode.Devices ? Visibility.Visible : Visibility.Collapsed;
            DeleteConfirmationOverlay.Visibility = overlay == OverlayMode.DeleteConfirmation ? Visibility.Visible : Visibility.Collapsed;
            SettingsSurface.Visibility = overlay == OverlayMode.Settings ? Visibility.Visible : Visibility.Collapsed;
            DetailsSurface.Visibility = Visibility.Collapsed;
            ModalBackdrop.Visibility = overlay is OverlayMode.Devices or OverlayMode.DeleteConfirmation ? Visibility.Visible : Visibility.Collapsed;
            var returnFocus = overlayReturnFocus;
            overlayReturnFocus = null;
            if (returnFocus?.IsVisible == true && returnFocus.IsEnabled) returnFocus.Focus();
            else if (overlay == OverlayMode.Devices) FocusSelection(Devices);
            else SettingsToggle.Focus();
            gate.RequireRelease();
            UpdateChrome();
            return true;
        }
        overlay = OverlayMode.None;
        overlayReturnMode = OverlayMode.None;
        overlayReturnFocus = null;
        OverlayShade.Visibility = Visibility.Collapsed;
        ModalBackdrop.Visibility = Visibility.Collapsed;
        DevicePickerOverlay.Visibility = DeleteConfirmationOverlay.Visibility = SettingsSurface.Visibility = DetailsSurface.Visibility = Visibility.Collapsed;
        SetPrimarySurfaceVisibility(true);
        WindowFrame.IsEnabled = true;
        var focusOrigin = overlayFocusOrigin;
        overlayFocusOrigin = null;
        if (focusOrigin?.IsVisible == true && focusOrigin.IsEnabled) focusOrigin.Focus();
        else RestoreFocus();
        if (cancelDeleteConfirmation && Programs.Navigation.Panel == ProgramPanel.ConfirmDelete)
            Programs.CancelDeleteConfirmation();
        gate.RequireRelease();
        UpdateChrome();
        return true;
    }

    private void RestoreFocus()
    {
        if (!navigation.SectionActive) FocusSectionTab();
        else if (navigation.Section == AppSection.Control) AudioControlCard.Focus();
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
            await RefreshControlAsync(quiet: true);
            if (pickingDisplay)
            {
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                var handle = new WindowInteropHelper(this).Handle;
                WindowPlacement.Center(handle);
                await Dispatcher.Yield(DispatcherPriority.ContextIdle);
                ApplyPreferredSize();
                UpdateLayout();
                WindowPlacement.Center(handle);
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
            UpdateSettingsAutomationName();
            SettingsToggle.Focus();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { ShowError("Не удалось сохранить настройку", ex.Message); }
    }

    private void ToggleSetting()
    {
        SettingsToggle.IsChecked = SettingsToggle.IsChecked != true;
        SettingsChoices.SelectedIndex = SettingsToggle.IsChecked == true ? 1 : 0;
        ApplySetting();
    }

    private void SetPrimarySurfaceVisibility(bool visible)
    {
        ControlSurface.Visibility = visible && navigation.Section == AppSection.Control ? Visibility.Visible : Visibility.Collapsed;
        Programs.Visibility = visible && navigation.Section != AppSection.Control ? Visibility.Visible : Visibility.Collapsed;
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
            if (navigation.SectionActive && navigation.Section != AppSection.Control && Programs.Back()) return;
            if (LeaveSection(PadAction.Close)) return;
            Close();
            return;
        }
        if (busy || Programs.IsBusy) return;
        if (overlay != OverlayMode.None)
        {
            if (overlay == OverlayMode.Error) { if (action == PadAction.Up) ErrorScroll.LineUp(); else if (action == PadAction.Down) ErrorScroll.LineDown(); else if (action == PadAction.Confirm) CloseDetails(); }
            else if (overlay == OverlayMode.Devices && action == PadAction.Up) MoveOverlay(-1);
            else if (overlay == OverlayMode.Devices && action == PadAction.Down) MoveOverlay(1);
            else if (overlay == OverlayMode.DeleteConfirmation && action == PadAction.Up) MoveOverlaySelection(DeleteConfirmationChoices, -1);
            else if (overlay == OverlayMode.DeleteConfirmation && action == PadAction.Down) MoveOverlaySelection(DeleteConfirmationChoices, 1);
            else if (action == PadAction.Confirm)
            {
                if (overlay == OverlayMode.Devices) _ = ApplySelected();
                else if (overlay == OverlayMode.DeleteConfirmation) _ = ApplyDeleteConfirmation();
                else ToggleSetting();
            }
            return;
        }
        switch (action)
        {
            case PadAction.Up:
            case PadAction.Down:
                if (!navigation.SectionActive)
                {
                    if (navigation.Navigate(action) == NavigationTransition.SectionChanged) ApplySectionChange();
                }
                else if (navigation.Section == AppSection.Control) MoveControl(action == PadAction.Up ? -1 : 1);
                else Programs.Move(action == PadAction.Up ? -1 : 1);
                break;
            case PadAction.Left:
                if (Programs.Editing) Programs.MoveHorizontal(-1);
                else LeaveSection();
                break;
            case PadAction.Right:
                if (Programs.Editing) Programs.MoveHorizontal(1);
                else ActivateSection();
                break;
            case PadAction.Confirm:
                if (!navigation.SectionActive) { ActivateSection(PadAction.Confirm); break; }
                if (navigation.Section == AppSection.Control) OpenDevicePicker(Keyboard.FocusedElement == DisplayControlCard);
                else _ = Programs.ConfirmAsync();
                break;
            case PadAction.Secondary:
                if (!navigation.SectionActive) break;
                if (Programs.Editing) Programs.DeleteEditing();
                else if (navigation.Section != AppSection.Control) _ = Programs.SecondaryAsync();
                break;
            case PadAction.CreateOrEdit:
                if (!navigation.SectionActive) break;
                if (Programs.Editing) _ = Programs.CatalogAsync();
                else if (navigation.Section == AppSection.Launch) _ = Programs.CreateAsync();
                else OpenDetails();
                break;
            case PadAction.Details: if (navigation.SectionActive) OpenDetails(Programs.InPanel ? Programs.DetailsText : null); break;
        }
    }

    private void MoveControl(int direction)
    {
        if (direction > 0) DisplayControlCard.Focus(); else AudioControlCard.Focus();
    }

    private void MoveOverlay(int direction) => MoveOverlaySelection(Devices, direction);

    private void MoveOverlaySelection(ListBox list, int direction)
    {
        if (list.Items.Count == 0) return;
        list.SelectedIndex = Math.Clamp(list.SelectedIndex + direction, 0, list.Items.Count - 1);
        FocusSelection(list);
    }

    private async Task ApplyDeleteConfirmation()
    {
        if (DeleteConfirmationChoices.SelectedItem is not OverlayChoice choice) return;
        if (choice.Id == "cancel")
        {
            CloseDetails();
            return;
        }
        CloseDetails(cancelDeleteConfirmation: false);
        await Programs.ConfirmDeleteAsync();
    }

    private void FocusSelection(ListBox list)
    {
        list.UpdateLayout();
        if (list.SelectedItem != null) list.ScrollIntoView(list.SelectedItem);
        list.UpdateLayout();
        if (list.SelectedItem != null && list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) is ListBoxItem item)
            item.Focus();
        else
            list.Focus();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F5 && overlay == OverlayMode.None && !Programs.Editing)
        {
            _ = Programs.SynchronizeGamesAsync();
            e.Handled = true;
            return;
        }
        if (Programs.Editing) return;
        var action = e.Key switch
        {
            Key.Escape => PadAction.Close,
            Key.Up => PadAction.Up,
            Key.Down => PadAction.Down,
            Key.Left => PadAction.Left,
            Key.Right => PadAction.Right,
            Key.Enter => PadAction.Confirm,
            Key.X => PadAction.Secondary,
            Key.Y => PadAction.CreateOrEdit,
            Key.F10 or Key.M => PadAction.Settings,
            Key.F1 => PadAction.Details,
            _ => PadAction.None
        };
        if (action == PadAction.None || (e.IsRepeat && action is PadAction.Confirm or PadAction.Secondary or PadAction.CreateOrEdit or PadAction.Settings)) return;
        Execute(action);
        e.Handled = true;
    }

    private void SelectSectionByMouse(AppSection section)
    {
        if (busy || Programs.IsBusy || Programs.InPanel || overlay != OverlayMode.None) return;
        if (navigation.SectionActive)
        {
            if (navigation.Section == section) { RestoreFocus(); return; }
            if (!LeaveSection()) return;
        }
        SwitchSection(section);
        ActivateSection();
    }

    private void ControlClick(object sender, RoutedEventArgs e) => SelectSectionByMouse(AppSection.Control);
    private void RunningClick(object sender, RoutedEventArgs e) => SelectSectionByMouse(AppSection.Running);
    private void LaunchClick(object sender, RoutedEventArgs e) => SelectSectionByMouse(AppSection.Launch);
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
    private async void DeleteConfirmationMouseClick(object sender, MouseButtonEventArgs e)
    {
        if (!dragged && ItemsControl.ContainerFromElement(DeleteConfirmationChoices, e.OriginalSource as DependencyObject) is ListBoxItem item)
        { DeleteConfirmationChoices.SelectedItem = item.DataContext; e.Handled = true; await ApplyDeleteConfirmation(); }
    }
    private void SettingsMouseClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(SettingsChoices, e.OriginalSource as DependencyObject) is ListBoxItem item)
        { SettingsChoices.SelectedItem = item; e.Handled = true; ApplySetting(); }
    }
    private void SettingsToggleClick(object sender, RoutedEventArgs e)
    {
        SettingsChoices.SelectedIndex = SettingsToggle.IsChecked == true ? 1 : 0;
        UpdateSettingsAutomationName();
        ApplySetting();
    }

    private void UpdateSettingsAutomationName() => AutomationProperties.SetName(
        SettingsToggle,
        $"Переключаться на приложение: {(SettingsToggle.IsChecked == true ? "включено" : "выключено")}");
    private void RightPointerDown(object sender, MouseButtonEventArgs e) => e.Handled = true;
    private async void RightPointerUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var source = e.OriginalSource as DependencyObject;
        for (var node = source; node != null && node != this; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ListBoxItem item && ItemsControl.ItemsControlFromItemContainer(item) is ListBox list)
            {
                list.SelectedItem = item.DataContext;
                list.Focus();
                if (ReferenceEquals(list, Devices)) await ApplySelected();
                else if (ReferenceEquals(list, SettingsChoices)) ApplySetting();
                else if (Programs.IsAncestorOf(list)) await Programs.ConfirmAsync();
                return;
            }
            if (node is ToggleButton toggle)
            {
                toggle.IsChecked = toggle.IsChecked != true;
                toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, toggle));
                return;
            }
            if (node is ButtonBase button)
            {
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                return;
            }
        }
    }
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
