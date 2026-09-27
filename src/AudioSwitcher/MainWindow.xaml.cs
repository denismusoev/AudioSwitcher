using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AudioSwitcher.Controls;
using AudioSwitcher.Core;
using AudioSwitcher.Platform;

namespace AudioSwitcher;

public partial class MainWindow : Window
{
    private const double PreferredWindowWidth = 1200;
    private const double PreferredWindowHeight = 765;

    private enum OverlayMode { None, Devices, WindowSelection, Confirmation, Settings, Error }
    private sealed record OverlayChoice(string Id, string DisplayName, bool IsDestructive = false);
    private sealed record WindowOverlayChoice(string Id, string DisplayName, string DisplayDetails, WindowTarget? Window = null, bool IsDestructive = false);

    private readonly AudioService audio = new();
    private readonly DisplayService display = new();
    private readonly GamepadInputService gamepadInput;
    private readonly RefreshBackoff controlRefreshBackoff = new();
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly SemaphoreSlim deviceOperations = new(1, 1);
    private TimeSpan deviceLoadTimeout = TimeSpan.FromSeconds(8);
    private readonly ApplicationSettingsStore settingsStore;
    private readonly InterfaceSettings interfaceSettings;
    private NavigationState navigation => Programs.Navigation;
    private bool busy, loadingDevices, pickingDisplay, pickerLoadFailed, controlRefreshing, closed;
    private int deviceLoadGeneration;
    private WindowSelectionAction windowSelectionAction;
    private RunningProgram? windowSelectionProgram;
    private OverlayMode overlay;
    private OverlayMode overlayReturnMode;
    private UIElement? overlayFocusOrigin;
    private UIElement? overlayReturnFocus;
    private Func<Task>? confirmationAction;
    private string? errorDetails;
    private Point pointerStart;
    private bool dragAllowed, dragged;
    private ListBoxItem? pointerPressedItem;
    private bool validListClick;

    public MainWindow() : this(new ApplicationSettingsStore()) { }

    public MainWindow(ApplicationSettingsStore settingsStore)
    {
        lifetimeToken = lifetime.Token;
        this.settingsStore = settingsStore;
        InitializeComponent();
        gamepadInput = new GamepadInputService(
            new Gamepad(),
            new GamepadInputEngine(),
            action =>
            {
                if (!closed && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                    _ = Dispatcher.InvokeAsync(action, DispatcherPriority.Input);
            },
            HandleGamepadCommand);
        Programs.MoveBehavior = settingsStore.Load().MoveBehavior;
        Programs.TransferCompleted += Close;
        Programs.StatusChanged += SetStatus;
        Programs.DeleteConfirmationRequested += OpenDeleteConfirmation;
        Programs.ResetConfirmationRequested += OpenResetConfirmation;
        Programs.WindowSelectionRequested += OpenWindowPicker;
        Programs.ContextChanged += UpdateChrome;
        interfaceSettings = new InterfaceSettings(Application.Current, UpdateAppearance);
        SizeChanged += (_, _) => UpdateAppearance();
        SourceInitialized += (_, _) => ApplyPreferredSize();
        Loaded += OnLoaded;
        Activated += (_, _) => gamepadInput.SetDeliveryEnabled(true);
        Deactivated += (_, _) => gamepadInput.SetDeliveryEnabled(false);
        Closed += (_, _) =>
        {
            closed = true;
            lifetime.Cancel();
            refreshTimer.Stop();
            _ = gamepadInput.DisposeAsync();
            Programs.Leave();
            interfaceSettings.Dispose();
            lifetime.Dispose();
        };
        refreshTimer.Tick += async (_, _) => { if (!busy && overlay == OverlayMode.None) await RefreshCurrentAsync(); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        navigation.Section = AppSection.Control;
        UpdateChrome();
        FocusSectionTab();
        Activate();
        gamepadInput.SetDeliveryEnabled(IsActive);
        gamepadInput.Start(lifetimeToken);
        refreshTimer.Start();
        await RefreshControlAsync();
        if (!closed) await SynchronizeGamesAsync(quiet: true);
    }

    private void UpdateAppearance()
    {
        bool compact = ActualWidth > 0 && ActualWidth < 1180;
        WindowFrame.Padding = compact ? new Thickness(32, 32, 32, 76) : new Thickness(64, 48, 80, 68);
        SectionColumn.Width = new GridLength(compact ? 240 : 320);
        GapColumn.Width = new GridLength(compact ? 32 : 48);
        FooterSectionColumn.Width = new GridLength(compact ? 240 : 320);
        FooterGapColumn.Width = new GridLength(compact ? 32 : 48);
        SectionStack.Margin = compact ? new Thickness(0, 8, 0, 0) : new Thickness(32, 8, 0, 0);
        foreach (var tab in new[] { ControlTab, RunningTab, LaunchTab }) tab.Width = compact ? 240 : 280;
        FooterSurface.Margin = compact ? new Thickness(32, 0, 32, 16) : new Thickness(64, 0, 80, 20);
        SettingsSurface.Width = double.NaN;
        SettingsSurface.HorizontalAlignment = HorizontalAlignment.Stretch;
        SettingsSurface.Margin = compact ? new Thickness(304, 76, 32, 76) : new Thickness(432, 92, 80, 86);
        DevicePickerOverlay.Width = Math.Min(450, Math.Max(280, ActualWidth - 64));
        DevicePickerOverlay.Margin = compact ? new Thickness(0, 32, 32, 76) : new Thickness(0, 40, 40, 86);
        ConfirmationOverlay.Width = DevicePickerOverlay.Width;
        ConfirmationOverlay.Margin = DevicePickerOverlay.Margin;
        WindowPickerOverlay.Width = DevicePickerOverlay.Width;
        WindowPickerOverlay.Margin = DevicePickerOverlay.Margin;
        UpdateChrome();
    }

    private void ApplyPreferredSize()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        ApplyPreferredSize(() =>
        {
            var size = WindowPlacement.PreferredSizeInDips(WindowPlacement.WorkAreaForWindow(handle), PreferredWindowWidth, PreferredWindowHeight);
            return (size.Width, size.Height);
        });
    }

    private void ApplyPreferredSize(Func<(double Width, double Height)> getPreferredSize)
    {
        try
        {
            var size = getPreferredSize();
            Width = size.Width;
            Height = size.Height;
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("Window.ApplyPreferredSize", error);
        }
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

    public Task SynchronizeGamesAsync(bool quiet = false) => closed
        ? Task.CompletedTask
        : Programs.SynchronizeGamesAsync(quiet, lifetimeToken);

    private async Task<T> RunDeviceOperationAsync<T>(Func<T> operation, TimeSpan? timeout = null)
    {
        var token = lifetimeToken;
        var elapsed = Stopwatch.StartNew();
        if (timeout is TimeSpan limit)
        {
            if (!await deviceOperations.WaitAsync(limit, token))
                throw new TimeoutException("Windows не ответила на запрос устройств вовремя.");
        }
        else await deviceOperations.WaitAsync(token);

        bool releaseHere = true;
        try
        {
            TimeSpan? remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException("Windows не ответила на запрос устройств вовремя.");
            var task = Task.Run(operation, CancellationToken.None);
            try
            {
                return remaining is TimeSpan wait ? await task.WaitAsync(wait, token) : await task;
            }
            catch (Exception error) when (error is TimeoutException or OperationCanceledException && !task.IsCompleted)
            {
                releaseHere = false;
                _ = ReleaseDeviceOperationWhenCompleteAsync(task);
                throw;
            }
        }
        finally
        {
            if (releaseHere) deviceOperations.Release();
        }
    }

    private async Task ReleaseDeviceOperationWhenCompleteAsync(Task operation)
    {
        try { await operation; }
        catch { }
        finally { deviceOperations.Release(); }
    }

    private async Task RefreshControlAsync(bool quiet = false)
    {
        if (controlRefreshing || closed) return;
        controlRefreshing = true;
        try
        {
            var snapshot = await RunDeviceOperationAsync(() => (Audio: audio.GetDevices(), Displays: display.GetDevices()));
            if (closed) return;
            controlRefreshBackoff.RecordSuccess();
            refreshTimer.Interval = controlRefreshBackoff.CurrentDelay;
            if (navigation.Section != AppSection.Control) return;
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
        catch (OperationCanceledException) when (closed) { }
        catch (Exception ex)
        {
            DiagnosticLog.Error("control.refresh", ex);
            if (!closed)
            {
                controlRefreshBackoff.RecordFailure();
                refreshTimer.Interval = controlRefreshBackoff.CurrentDelay;
                SetStatus("Не удалось обновить устройства", ex.Message);
            }
        }
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
        if (section == AppSection.Control) ObserveUiTask(RefreshControlAsync(), "Не удалось обновить устройства");
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
        if (navigation.Section == AppSection.Control) ObserveUiTask(RefreshControlAsync(), "Не удалось обновить устройства");
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
        UpdateChrome();
    }

    private bool LeaveSection(PadAction action = PadAction.Left)
    {
        if (navigation.Navigate(action) != NavigationTransition.LeftSection) return false;
        Programs.ExitCurrentPage();
        FocusSectionTab();
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
        PrimaryCommand.Content = overlay == OverlayMode.Settings ? "переключить" : overlay == OverlayMode.Error ? "закрыть" : overlay == OverlayMode.Devices && pickerLoadFailed ? "повторить" : overlay != OverlayMode.None ? "выбрать" : !sectionActive ? "открыть" : editing ? "поле" : navigation.Section == AppSection.Running && !panel ? "на экран" : navigation.Section == AppSection.Launch && !panel ? "запустить" : "выбрать";
        SecondaryCommand.Content = editing ? "удалить" : navigation.Section == AppSection.Running ? "закрыть" : "изменить";
        CreateCommand.Content = editing ? "сохранить" : "добавить";
        SecondaryCommand.Visibility = overlay == OverlayMode.None && sectionActive && (editing ? Programs.CanDeleteEditing
            : !panel && (navigation.Section == AppSection.Running || navigation.Section == AppSection.Launch && Programs.CanEditSelectedLaunchEntry))
            ? Visibility.Visible : Visibility.Collapsed;
        CreateCommand.Visibility = overlay == OverlayMode.None && sectionActive && (editing || !panel && navigation.Section == AppSection.Launch) ? Visibility.Visible : Visibility.Collapsed;
        SettingsCommand.Visibility = overlay == OverlayMode.None && !panel ? Visibility.Visible : Visibility.Collapsed;
        BackCommand.Content = overlay == OverlayMode.None && !sectionActive ? "закрыть" : "назад";
    }

    private void OpenDevicePicker(bool displays) => ObserveUiTask(OpenDevicePickerAsync(displays), "Не удалось загрузить устройства");

    private async Task OpenDevicePickerAsync(bool displays)
    {
        if (closed || busy || overlay != OverlayMode.None && overlay != OverlayMode.Devices) return;
        busy = true;
        loadingDevices = true;
        pickingDisplay = displays;
        pickerLoadFailed = false;
        int generation = ++deviceLoadGeneration;
        PickerTitle.Text = displays ? "Основной экран" : "Устройство звука";
        PickerSubtitle.Text = displays ? "Выберите экран, который станет главным" : "Выберите устройство вывода по умолчанию";
        Devices.ItemsSource = null;
        Devices.SelectedItem = null;
        Empty.Visibility = Visibility.Collapsed;
        PickerError.Visibility = Visibility.Collapsed;
        PickerLoading.Visibility = Visibility.Visible;
        if (overlay == OverlayMode.None) ShowOverlay(OverlayMode.Devices);
        UpdateChrome();
        try
        {
            var items = await RunDeviceOperationAsync(() => displays ? display.GetDevices() : audio.GetDevices(), deviceLoadTimeout);
            if (closed || generation != deviceLoadGeneration || overlay != OverlayMode.Devices) return;
            Devices.ItemsSource = items;
            Devices.SelectedItem = items.FirstOrDefault(x => x.IsDefault) ?? items.FirstOrDefault();
            Empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PickerLoading.Visibility = Visibility.Collapsed;
            SetStatus("Готово");
            FocusSelection(Devices);
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception ex)
        {
            DiagnosticLog.Error("device_picker.load", ex, new Dictionary<string, object?> { ["kind"] = displays ? "display" : "audio" });
            if (!closed && generation == deviceLoadGeneration && overlay == OverlayMode.Devices)
            {
                PickerLoading.Visibility = Visibility.Collapsed;
                PickerErrorText.Text = ex is TimeoutException ? "Windows не ответила на запрос устройств вовремя" : "Не удалось загрузить устройства";
                PickerError.Visibility = Visibility.Visible;
                pickerLoadFailed = true;
                SetStatus("Не удалось загрузить устройства", ex.Message);
                Devices.Focus();
            }
        }
        finally
        {
            loadingDevices = false;
            busy = false;
            if (!closed) UpdateChrome();
        }
    }

    private void OpenWindowPicker(WindowSelectionAction action, RunningProgram program)
    {
        if (busy || overlay != OverlayMode.None || program.Windows.Count == 0) return;
        windowSelectionAction = action;
        windowSelectionProgram = program;
        WindowPickerTitle.Text = action == WindowSelectionAction.Move ? "Какое окно переместить?" : "Какое окно закрыть?";
        WindowPickerSubtitle.Text = program.Name;
        var choices = program.Windows
            .Select(window => new WindowOverlayChoice("window", window.DisplayName, window.DisplayDetails, window));
        WindowChoices.ItemsSource = action == WindowSelectionAction.Close
            ? choices.Append(new WindowOverlayChoice("close-all", "Закрыть все окна", $"Окна: {program.Windows.Count}", IsDestructive: true)).ToArray()
            : choices.ToArray();
        WindowChoices.SelectedIndex = 0;
        ShowOverlay(OverlayMode.WindowSelection);
        FocusSelection(WindowChoices);
    }

    private void OpenDeleteConfirmation(string entryName)
    {
        OpenConfirmation(
            $"Удалить запись «{entryName}»?",
            "Запись исчезнет из AudioSwitcher. Программа и её файлы останутся на компьютере.",
            "Удалить запись",
            Programs.ConfirmDeleteAsync);
    }

    private void OpenResetConfirmation()
    {
        OpenConfirmation(
            "Сбросить каталог?",
            "Исходный файл будет сохранён рядом с каталогом как .bak. Список для запуска станет пустым.",
            "Сохранить копию и сбросить",
            Programs.ConfirmResetAsync);
    }

    private void OpenConfirmation(string title, string description, string actionLabel, Func<Task> action)
    {
        ConfirmationTitle.Text = title;
        ConfirmationDescription.Text = description;
        confirmationAction = action;
        ConfirmationChoices.ItemsSource = new[]
        {
            new OverlayChoice("cancel", "Отмена"),
            new OverlayChoice("confirm", actionLabel, IsDestructive: true)
        };
        ConfirmationChoices.SelectedIndex = 0;
        ShowOverlay(OverlayMode.Confirmation);
        FocusSelection(ConfirmationChoices);
    }

    private void OpenSettings()
    {
        SettingsToggle.IsChecked = Programs.MoveBehavior == WindowMoveBehavior.ActivateAndClose;
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
        WindowPickerOverlay.Visibility = mode == OverlayMode.WindowSelection ? Visibility.Visible : Visibility.Collapsed;
        ConfirmationOverlay.Visibility = mode == OverlayMode.Confirmation ? Visibility.Visible : Visibility.Collapsed;
        SettingsSurface.Visibility = mode == OverlayMode.Settings ? Visibility.Visible : Visibility.Collapsed;
        DetailsSurface.Visibility = mode == OverlayMode.Error ? Visibility.Visible : Visibility.Collapsed;
        ModalBackdrop.Visibility = mode is OverlayMode.Devices or OverlayMode.WindowSelection or OverlayMode.Confirmation ? Visibility.Visible : Visibility.Collapsed;
        if (mode == OverlayMode.Settings) SetPrimarySurfaceVisibility(false);
        WindowFrame.IsEnabled = false;
        UpdateChrome();
    }

    private bool CloseDetails(bool cancelConfirmation = true)
    {
        if (overlay == OverlayMode.None) return false;
        if (overlay == OverlayMode.Error && overlayReturnMode != OverlayMode.None)
        {
            overlay = overlayReturnMode;
            overlayReturnMode = OverlayMode.None;
            DevicePickerOverlay.Visibility = overlay == OverlayMode.Devices ? Visibility.Visible : Visibility.Collapsed;
            WindowPickerOverlay.Visibility = overlay == OverlayMode.WindowSelection ? Visibility.Visible : Visibility.Collapsed;
            ConfirmationOverlay.Visibility = overlay == OverlayMode.Confirmation ? Visibility.Visible : Visibility.Collapsed;
            SettingsSurface.Visibility = overlay == OverlayMode.Settings ? Visibility.Visible : Visibility.Collapsed;
            DetailsSurface.Visibility = Visibility.Collapsed;
            ModalBackdrop.Visibility = overlay is OverlayMode.Devices or OverlayMode.WindowSelection or OverlayMode.Confirmation ? Visibility.Visible : Visibility.Collapsed;
            var returnFocus = overlayReturnFocus;
            overlayReturnFocus = null;
            if (returnFocus?.IsVisible == true && returnFocus.IsEnabled) returnFocus.Focus();
            else if (overlay == OverlayMode.Devices) FocusSelection(Devices);
            else if (overlay == OverlayMode.WindowSelection) FocusSelection(WindowChoices);
            else SettingsToggle.Focus();
            UpdateChrome();
            return true;
        }
        if (overlay == OverlayMode.Devices)
        {
            deviceLoadGeneration++;
            if (loadingDevices) { loadingDevices = false; busy = false; }
        }
        overlay = OverlayMode.None;
        overlayReturnMode = OverlayMode.None;
        overlayReturnFocus = null;
        OverlayShade.Visibility = Visibility.Collapsed;
        ModalBackdrop.Visibility = Visibility.Collapsed;
        DevicePickerOverlay.Visibility = WindowPickerOverlay.Visibility = ConfirmationOverlay.Visibility = SettingsSurface.Visibility = DetailsSurface.Visibility = Visibility.Collapsed;
        SetPrimarySurfaceVisibility(true);
        WindowFrame.IsEnabled = true;
        var focusOrigin = overlayFocusOrigin;
        overlayFocusOrigin = null;
        if (focusOrigin?.IsVisible == true && focusOrigin.IsEnabled) focusOrigin.Focus();
        else RestoreFocus();
        if (cancelConfirmation)
            Programs.CancelConfirmation();
        confirmationAction = null;
        WindowChoices.ItemsSource = null;
        windowSelectionProgram = null;
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
        if (closed || busy || Devices.SelectedItem is not DeviceOption selected) return;
        busy = true;
        UpdateChrome();
        SetStatus(pickingDisplay ? "Смена основного экрана…" : "Переключение звука…");
        await Dispatcher.Yield(DispatcherPriority.Background);
        try
        {
            await RunDeviceOperationAsync(() =>
            {
                if (pickingDisplay) display.SetPrimaryDisplay(selected.Id); else audio.SetDefault(selected.Id);
                return true;
            });
            if (closed) return;
            CloseDetails();
            await RefreshControlAsync(quiet: true);
            if (closed) return;
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
        catch (OperationCanceledException) when (closed) { }
        catch (Exception ex)
        {
            DiagnosticLog.Error("device.apply", ex, new Dictionary<string, object?>
            {
                ["deviceId"] = selected.Id,
                ["kind"] = pickingDisplay ? "display" : "audio"
            });
            if (!closed) ShowError("Не удалось переключить", ex.Message);
        }
        finally { busy = false; if (!closed) UpdateChrome(); }
    }

    private void ObserveUiTask(Task task, string message)
    {
        TaskObserver.Observe(task, error =>
        {
            if (closed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (!closed) ShowError(message, error.Message);
                });
            }
            catch (InvalidOperationException) when (closed || Dispatcher.HasShutdownStarted) { }
        });
    }

    private void ApplySetting()
    {
        var behavior = SettingsToggle.IsChecked == true ? WindowMoveBehavior.ActivateAndClose : WindowMoveBehavior.KeepUtilityFocused;
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
        ApplySetting();
    }

    private void SetPrimarySurfaceVisibility(bool visible)
    {
        ControlSurface.Visibility = visible && navigation.Section == AppSection.Control ? Visibility.Visible : Visibility.Collapsed;
        Programs.Visibility = visible && navigation.Section != AppSection.Control ? Visibility.Visible : Visibility.Collapsed;
    }

    private GamepadCommandResult HandleGamepadCommand(GamepadCommand command)
    {
        if (closed) return GamepadCommandResult.Blocked;
        return ExecuteAction(command.Action);
    }

    private void Execute(PadAction action) => _ = ExecuteAction(action);

    private GamepadCommandResult ExecuteAction(PadAction action)
    {
        if (action == PadAction.Settings && overlay == OverlayMode.None && !Programs.InPanel)
        {
            OpenSettings();
            return GamepadCommandResult.ContextChanged;
        }
        if (action == PadAction.Close)
        {
            if (CloseDetails()) return GamepadCommandResult.ContextChanged;
            if (navigation.SectionActive && navigation.Section != AppSection.Control && Programs.Back()) return GamepadCommandResult.ContextChanged;
            if (LeaveSection(PadAction.Close)) return GamepadCommandResult.ContextChanged;
            Close();
            return GamepadCommandResult.Handled;
        }
        if (busy || Programs.IsBusy) return GamepadCommandResult.Blocked;
        if (overlay != OverlayMode.None)
        {
            if (overlay == OverlayMode.Error)
            {
                if (action == PadAction.Up) ErrorScroll.LineUp();
                else if (action == PadAction.Down) ErrorScroll.LineDown();
                else if (action == PadAction.Confirm) { CloseDetails(); return GamepadCommandResult.ContextChanged; }
                else return GamepadCommandResult.Unhandled;
            }
            else if (overlay == OverlayMode.Devices && action == PadAction.Up) MoveOverlay(-1);
            else if (overlay == OverlayMode.Devices && action == PadAction.Down) MoveOverlay(1);
            else if (overlay == OverlayMode.WindowSelection && action == PadAction.Up) MoveOverlaySelection(WindowChoices, -1);
            else if (overlay == OverlayMode.WindowSelection && action == PadAction.Down) MoveOverlaySelection(WindowChoices, 1);
            else if (overlay == OverlayMode.Confirmation && action == PadAction.Up) MoveOverlaySelection(ConfirmationChoices, -1);
            else if (overlay == OverlayMode.Confirmation && action == PadAction.Down) MoveOverlaySelection(ConfirmationChoices, 1);
            else if (action == PadAction.Confirm)
            {
                if (overlay == OverlayMode.Devices && pickerLoadFailed) OpenDevicePicker(pickingDisplay);
                else if (overlay == OverlayMode.Devices) ObserveUiTask(ApplySelected(), "Не удалось переключить");
                else if (overlay == OverlayMode.WindowSelection) ObserveUiTask(ApplyWindowSelection(), "Не удалось выполнить действие");
                else if (overlay == OverlayMode.Confirmation) ObserveUiTask(ApplyConfirmation(), "Не удалось выполнить действие");
                else ToggleSetting();
            }
            else return GamepadCommandResult.Unhandled;
            return GamepadCommandResult.Handled;
        }
        switch (action)
        {
            case PadAction.Up:
            case PadAction.Down:
                if (!navigation.SectionActive)
                {
                    if (navigation.Navigate(action) == NavigationTransition.SectionChanged)
                    {
                        ApplySectionChange();
                        return GamepadCommandResult.ContextChanged;
                    }
                    return GamepadCommandResult.Unhandled;
                }
                if (navigation.Section == AppSection.Control) MoveControl(action == PadAction.Up ? -1 : 1);
                else Programs.Move(action == PadAction.Up ? -1 : 1);
                return GamepadCommandResult.Handled;
            case PadAction.Left:
                if (Programs.Editing) { Programs.MoveHorizontal(-1); return GamepadCommandResult.Handled; }
                return LeaveSection() ? GamepadCommandResult.ContextChanged : GamepadCommandResult.Unhandled;
            case PadAction.Right:
                if (Programs.Editing) { Programs.MoveHorizontal(1); return GamepadCommandResult.Handled; }
                bool wasActive = navigation.SectionActive;
                ActivateSection();
                return !wasActive && navigation.SectionActive ? GamepadCommandResult.ContextChanged : GamepadCommandResult.Unhandled;
            case PadAction.Confirm:
                if (!navigation.SectionActive)
                {
                    ActivateSection(PadAction.Confirm);
                    return GamepadCommandResult.ContextChanged;
                }
                if (navigation.Section == AppSection.Control)
                {
                    OpenDevicePicker(Keyboard.FocusedElement == DisplayControlCard);
                    return GamepadCommandResult.ContextChanged;
                }
                ObserveUiTask(Programs.ConfirmAsync(), "Не удалось выполнить действие");
                return GamepadCommandResult.Handled;
            case PadAction.Secondary:
                if (!navigation.SectionActive) return GamepadCommandResult.Unhandled;
                if (Programs.Editing) Programs.DeleteEditing();
                else if (navigation.Section != AppSection.Control) ObserveUiTask(Programs.SecondaryAsync(), "Не удалось выполнить действие");
                else return GamepadCommandResult.Unhandled;
                return GamepadCommandResult.Handled;
            case PadAction.CreateOrEdit:
                if (!navigation.SectionActive) return GamepadCommandResult.Unhandled;
                if (Programs.Editing) ObserveUiTask(Programs.CatalogAsync(), "Не удалось сохранить запись");
                else if (navigation.Section == AppSection.Launch) ObserveUiTask(Programs.CreateAsync(), "Не удалось создать запись");
                else OpenDetails();
                return GamepadCommandResult.Handled;
            case PadAction.Details:
                if (!navigation.SectionActive) return GamepadCommandResult.Unhandled;
                OpenDetails(Programs.InPanel ? Programs.DetailsText : null);
                return GamepadCommandResult.ContextChanged;
            default:
                return GamepadCommandResult.Unhandled;
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

    private async Task ApplyConfirmation()
    {
        if (ConfirmationChoices.SelectedItem is not OverlayChoice choice) return;
        if (choice.Id == "cancel")
        {
            CloseDetails();
            return;
        }
        var action = confirmationAction;
        CloseDetails(cancelConfirmation: false);
        confirmationAction = null;
        if (action != null) await action();
    }

    private async Task ApplyWindowSelection()
    {
        if (WindowChoices.SelectedItem is not WindowOverlayChoice choice || windowSelectionProgram == null) return;
        var program = windowSelectionProgram;
        var action = windowSelectionAction;
        CloseDetails();
        await Programs.ApplyWindowSelectionAsync(action, program, choice.Window);
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
            ObserveUiTask(SynchronizeGamesAsync(), "Не удалось обновить каталог игр");
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
        if (TryConsumeListClick(Devices, e.OriginalSource as DependencyObject, out var item))
        { Devices.SelectedItem = item.DataContext; e.Handled = true; await ApplySelected(); }
    }
    private async void ConfirmationMouseClick(object sender, MouseButtonEventArgs e)
    {
        if (TryConsumeListClick(ConfirmationChoices, e.OriginalSource as DependencyObject, out var item))
        { ConfirmationChoices.SelectedItem = item.DataContext; e.Handled = true; await ApplyConfirmation(); }
    }
    private async void WindowChoiceMouseClick(object sender, MouseButtonEventArgs e)
    {
        if (TryConsumeListClick(WindowChoices, e.OriginalSource as DependencyObject, out var item))
        { WindowChoices.SelectedItem = item.DataContext; e.Handled = true; await ApplyWindowSelection(); }
    }
    private void SettingsToggleClick(object sender, RoutedEventArgs e)
    {
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
                else if (ReferenceEquals(list, WindowChoices)) await ApplyWindowSelection();
                else if (ReferenceEquals(list, ConfirmationChoices)) await ApplyConfirmation();
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
        pointerStart = e.GetPosition(this); dragged = false; dragAllowed = true; validListClick = false; pointerPressedItem = null;
        for (var node = e.OriginalSource as DependencyObject; node != null && node != this; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ListBoxItem item) { pointerPressedItem = item; dragAllowed = false; break; }
            if (node is TextBoxBase or ButtonBase or ListBox or ScrollBar or Thumb) { dragAllowed = false; break; }
        }
    }
    private void PointerMove(object sender, MouseEventArgs e)
    {
        if (!dragAllowed || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - pointerStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - pointerStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        dragAllowed = false; dragged = true; e.Handled = true; DragMove();
    }
    private void PointerUp(object sender, MouseButtonEventArgs e)
    {
        var releasedItem = e.OriginalSource is DependencyObject source
            ? FindAncestor<ListBoxItem>(source)
            : null;
        validListClick = !dragged && pointerPressedItem != null && ReferenceEquals(pointerPressedItem, releasedItem);
        pointerPressedItem = null;
        dragAllowed = false;
        if (dragged) e.Handled = true;
    }

    private bool TryConsumeListClick(ListBox list, DependencyObject? source, out ListBoxItem item)
    {
        item = source == null ? null! : ItemsControl.ContainerFromElement(list, source) as ListBoxItem ?? null!;
        bool activate = validListClick && item != null;
        validListClick = false;
        return activate;
    }

    private static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        for (DependencyObject? node = source; node != null; node = VisualTreeHelper.GetParent(node))
            if (node is T match) return match;
        return null;
    }
}
