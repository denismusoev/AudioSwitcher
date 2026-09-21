using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AudioSwitcher.Core;
using AudioSwitcher.Platform;

namespace AudioSwitcher.Controls;

public partial class ProgramsView : UserControl
{
    private sealed record Choice(string Id, string DisplayName, string DisplayDetails = "", WindowTarget? Window = null);
    private sealed record CatalogRow(LaunchEntry Entry)
    {
        public string DisplayName => Entry.Name;
        public string DisplayDetails => (Entry.Kind == LaunchKind.Executable ? "Программа" : "Ярлык") + " · " + Entry.Target;
    }
    private readonly ProgramWindowService windows = new();
    private readonly ProgramWindowMover mover = new();
    private readonly ProgramProcessService processes = new();
    private readonly ProgramLaunchService launcher = new();
    private readonly LaunchCatalogStore store;
    private LaunchCatalogLoad catalog = new(new(1, []), true, null);
    private CancellationTokenSource? lifetime;
    private bool refreshing, operating, editorOpen;
    private string snapshotSignature = "";
    private RunningProgram? currentProgram;
    private LaunchEntry? currentEntry;
    public LaunchEntryDialog? Editor { get; private set; }
    public NavigationState Navigation { get; } = new();
    public bool IsBusy => operating || Editor?.IsSaving == true;
    public bool Editing => editorOpen;
    public bool CanDeleteEditing => Editing && currentEntry != null && Editor?.IsSaving != true;
    public string DetailsText => PanelTitle.Text + "\n\n" + PanelDescription.Text + (PanelFeedback.Visibility == Visibility.Visible ? "\n\n" + PanelFeedback.Text : "");
    public bool InPanel => editorOpen || Navigation.Panel != ProgramPanel.List;
    public string ActionHint => IsBusy ? "Подождите" : Editing ? "Сохранить" : InPanel ? "Выбрать" : "Действия";
    public event Action<string, string?>? StatusChanged;
    public event Action? ContextChanged;
    public event Action? TransferCompleted;
    public WindowMoveBehavior MoveBehavior { get; set; } = WindowMoveBehavior.KeepUtilityFocused;

    public ProgramsView() : this(new LaunchCatalogStore()) { }
    public ProgramsView(LaunchCatalogStore store)
    {
        this.store = store; InitializeComponent();

    }
    private void ClosePanel()
    {
        PanelSurface.Visibility = Visibility.Collapsed;
    }
    public void Enter(bool focus = true)
    {
        if (lifetime == null)
        {
            lifetime = new();
            catalog = store.Load(); ShowCatalog(); UpdateLists();
            if (catalog.Error != null && Navigation.LaunchList) Notify("Каталог недоступен. Можно сохранить копию и сбросить", catalog.Error);
            _ = RefreshAsync();
        }
        if (focus) RestoreFocus();
    }
    public void Leave() { lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null; }
    public async Task RefreshAsync(bool quiet = false)
    {
        if (Navigation.Section != AppSection.Programs || Navigation.LaunchList || Editing || (InPanel && currentProgram == null) || IsBusy || refreshing || lifetime == null) return;
        var active = lifetime; var token = active.Token; refreshing = true;
        if (!quiet) Notify("Загрузка приложений…");
        try
        {
            var snapshot = await windows.GetProgramsAsync(token);
            if (active != lifetime || Navigation.Section != AppSection.Programs || Navigation.LaunchList || Editing || IsBusy) return;
            ApplyRunningSnapshot(snapshot, quiet);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (active == lifetime) Notify("Не удалось загрузить приложения", ex.Message); }
        finally { refreshing = false; }
    }
    private void ApplyRunningSnapshot(IReadOnlyList<RunningProgram> snapshot, bool quiet)
    {
        string signature = string.Join("|", snapshot.Select(p => $"{p.Key}:{p.Name}:{string.Join(';', p.Windows.Select(w => $"{w.Handle}:{w.Title}:{w.Screen}:{w.NotResponding}"))}"));
        if (signature != snapshotSignature)
        {
            var selected = (RunningList.SelectedItem as RunningProgram)?.Key;
            RunningList.ItemsSource = snapshot;
            RunningList.SelectedItem = snapshot.FirstOrDefault(p => p.Key == selected) ?? snapshot.FirstOrDefault();
            snapshotSignature = signature;
            if (!InPanel) ScrollSelection(RunningList);
        }
        if (InPanel && currentProgram != null)
        {
            var latest = snapshot.FirstOrDefault(p => p.Key == currentProgram.Key);
            if (latest == null)
            {
                currentProgram = null; ReturnToList(focus: Window.GetWindow(this)?.IsActive == true); Notify("Программа уже закрыта"); return;
            }
            currentProgram = latest;
            if (Navigation.Panel == ProgramPanel.Actions)
            {
                PanelTitle.Text = latest.Name;
                PanelDescription.Text = string.Join(", ", latest.Windows.Select(w => w.Screen).Distinct());
            }
            else if (Navigation.Panel == ProgramPanel.Windows)
            {
                var selected = (ProgramActions.SelectedItem as WindowTarget)?.Handle;
                var oldWindows = ProgramActions.ItemsSource?.Cast<WindowTarget>().ToArray() ?? [];
                if (!oldWindows.SequenceEqual(latest.Windows))
                {
                    ProgramActions.ItemsSource = latest.Windows;
                    ProgramActions.SelectedItem = latest.Windows.FirstOrDefault(w => w.Handle == selected) ?? latest.Windows.FirstOrDefault();
                    ScrollSelection(ProgramActions);
                }
                PanelDescription.Text = latest.Name;
            }
            else if (Navigation.Panel == ProgramPanel.CloseWindows) ShowCloseChoices(latest);
        }
        UpdateEmpty(); if (!quiet) Notify("Готово");
    }
    private void Notify(string text, string? details = null)
    {
        if (InPanel && operating)
        {
            PanelFeedback.Text = details == null || details == text ? text : $"{text}\n{details}";
            PanelFeedback.Visibility = Visibility.Visible;
        }
        StatusChanged?.Invoke(text, details);
    }
    private void ShowCatalog(Guid? preferred = null)
    {
        Guid? selected = preferred ?? (LaunchList.SelectedItem as CatalogRow)?.Entry.Id;
        var rows = catalog.Catalog.Entries.Select(e => new CatalogRow(e)).ToArray();
        LaunchList.ItemsSource = rows; LaunchList.SelectedItem = rows.FirstOrDefault(r => r.Entry.Id == selected) ?? rows.FirstOrDefault();
        AddProgram.IsEnabled = catalog.CanSave;
        ResetCatalog.Visibility = catalog.CanSave ? Visibility.Collapsed : Visibility.Visible;
    }
    private void UpdateLists()
    {
        ListTitle.Text = Navigation.LaunchList ? "Для запуска" : "Запущенные приложения";
        ListSubtitle.Text = Navigation.LaunchList ? "Добавленные программы и ярлыки" : "";
        ListSubtitle.Visibility = Navigation.LaunchList ? Visibility.Visible : Visibility.Collapsed;
        RunningList.Visibility = Navigation.LaunchList ? Visibility.Collapsed : Visibility.Visible;
        LaunchList.Visibility = Navigation.LaunchList ? Visibility.Visible : Visibility.Collapsed;
        CatalogTools.Visibility = Visibility.Collapsed;
        RunningMode.SetResourceReference(Control.ForegroundProperty, Navigation.LaunchList ? "MutedText" : "Text");
        LaunchMode.SetResourceReference(Control.ForegroundProperty, Navigation.LaunchList ? "Text" : "MutedText");
        RunningMode.IsSelected = !Navigation.LaunchList; LaunchMode.IsSelected = Navigation.LaunchList;
        RunningMode.SetResourceReference(Control.BorderBrushProperty, Navigation.LaunchList ? "WindowSurface" : "Accent");
        LaunchMode.SetResourceReference(Control.BorderBrushProperty, Navigation.LaunchList ? "Accent" : "WindowSurface");
        RunningMode.FontWeight = Navigation.LaunchList ? FontWeights.Normal : FontWeights.Normal;
        LaunchMode.FontWeight = Navigation.LaunchList ? FontWeights.Normal : FontWeights.Normal;
        System.Windows.Automation.AutomationProperties.SetHelpText(RunningMode, Navigation.LaunchList ? "Показать запущенные программы" : "Выбранный список");
        System.Windows.Automation.AutomationProperties.SetHelpText(LaunchMode, Navigation.LaunchList ? "Выбранный список" : "Показать программы для запуска");
        UpdateEmpty(); ContextChanged?.Invoke();
    }
    private void UpdateEmpty()
    {
        EmptyPrograms.Text = Navigation.LaunchList ? catalog.CanSave ? "" : "Каталог не прочитан. Исходный файл сохранён" : "Нет запущенных приложений с окнами";
        bool showEmpty = (Navigation.LaunchList ? LaunchList : RunningList).Items.Count == 0 && (!Navigation.LaunchList || !catalog.CanSave);
        EmptyPrograms.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
    }
    public void SelectMode(bool launch, bool force = false)
    {
        if (InPanel || IsBusy || (!force && Navigation.LaunchList == launch)) return;
        Navigation.LaunchList = launch; UpdateLists();
        if (launch)
        {
            catalog = store.Load(); ShowCatalog(); UpdateEmpty(); LaunchList.Focus();
            Notify(catalog.Error == null ? "Готово" : "Каталог недоступен. Можно сохранить копию и сбросить", catalog.Error);
        }
        else { RunningList.Focus(); _ = RefreshAsync(); }
    }
    public void Move(int direction)
    {
        if (IsBusy) return;
        if (Editing)
        {
            Editor?.MoveField(direction < 0 ? PadAction.Up : PadAction.Down);
            return;
        }
        var list = InPanel ? ProgramActions : Navigation.LaunchList ? LaunchList : RunningList;
        if (list.Items.Count == 0) return;
        list.SelectedIndex = Math.Clamp(list.SelectedIndex + direction, 0, list.Items.Count - 1);
        FocusSelection(list);
    }
    public void MoveHorizontal(int direction)
    {
        if (Editing && !IsBusy) Editor?.MoveField(direction < 0 ? PadAction.Left : PadAction.Right);
    }
    private static void ScrollSelection(ListBox list) { if (list.SelectedItem != null) list.ScrollIntoView(list.SelectedItem); }
    private static void FocusSelection(ListBox list)
    {
        list.UpdateLayout(); ScrollSelection(list);
        if (list.SelectedItem != null && list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) is ListBoxItem item) item.Focus();
        else list.Focus();
    }
    public void RestoreFocus() => FocusSelection(InPanel ? ProgramActions : Navigation.LaunchList ? LaunchList : RunningList);
    public bool Back()
    {
        if (IsBusy) return true;
        if (Editing) { if (Editor?.EndFieldInput() != true) CloseEditor(); return true; }
        if (!Navigation.Back()) return false;
        if (Navigation.Panel == ProgramPanel.Actions) ShowActions();
        else
        {
            ClosePanel(); ListsSurface.Visibility = Visibility.Visible; ListsSurface.IsEnabled = ListsSurface.IsHitTestVisible = true; PanelSurface.Visibility = Visibility.Collapsed;
            FocusSelection(Navigation.LaunchList ? LaunchList : RunningList); UpdateEmpty(); ContextChanged?.Invoke();
        }
        return true;
    }
    public async Task ConfirmAsync()
    {
        if (IsBusy) return;
        if (Editing) { Editor?.ConfirmField(); return; }
        if (!InPanel)
        {
            if (Navigation.LaunchList && LaunchList.SelectedItem is CatalogRow row)
            { currentEntry = row.Entry; currentProgram = null;
                if (await RunOperation("Открытие…", async token => { await launcher.LaunchAsync(currentEntry, token); return new(ProgramResultCode.Success, "Команда запуска отправлена"); })) ReturnToList(); }
            else if (!Navigation.LaunchList && RunningList.SelectedItem is RunningProgram program)
            {
                currentProgram = program; currentEntry = null;
                if (program.Windows.Count == 1) await MoveWindow(program.Windows[0]);
                else ShowPanel(ProgramPanel.Windows, "Какое окно переместить?", program.Name, program.Windows);
            }
            return;
        }
        if (Navigation.Panel == ProgramPanel.Windows && ProgramActions.SelectedItem is WindowTarget target)
        { await MoveWindow(target); return; }
        if (ProgramActions.SelectedItem is not Choice choice) return;
        switch (choice.Id)
        {
            case "cancel": Back(); break;
            case "launch":
                if (currentEntry == null) return;
                if (await RunOperation("Открытие…", async token => { await launcher.LaunchAsync(currentEntry, token); return new(ProgramResultCode.Success, $"Команда запуска отправлена: {currentEntry.Name}"); })) ReturnToList();
                break;
            case "move":
                if (currentProgram == null) return;
                if (currentProgram.Windows.Count == 1) await MoveWindow(currentProgram.Windows[0]);
                else ShowPanel(ProgramPanel.Windows, "Какое окно переместить?", currentProgram.Name, currentProgram.Windows);
                break;
            case "close": if (currentProgram != null) ShowCloseChoices(currentProgram); break;
            case "close-window":
                if (choice.Window != null && await RunOperation("Закрытие…", token => processes.CloseAsync(choice.Window, token)))
                { ReturnToList(); await RefreshAsync(quiet: true); } break;
            case "close-all":
                if (currentProgram != null && await RunOperation("Закрытие…", token => processes.CloseAllAsync(currentProgram.Windows, token)))
                { ReturnToList(); await RefreshAsync(quiet: true); } break;
            case "add": await EditEntry(null); break;
            case "edit": if (currentEntry != null) await EditEntry(currentEntry); break;
            case "delete":
                ShowPanel(ProgramPanel.ConfirmDelete, $"Удалить запись «{currentEntry?.Name}»?", "Приложение и файл ярлыка останутся на компьютере.",
                    new[] { new Choice("cancel", "Отмена"), new Choice("delete-entry", "Удалить запись") }); break;
            case "delete-entry":
                if (currentEntry == null) return;
                if (await RunOperation("Сохранение…", async _ => {
                    var next = new LaunchCatalog(1, catalog.Catalog.Entries.Where(e => e.Id != currentEntry.Id).ToArray());
                    await Task.Run(() => store.Save(next)); catalog = new(next, true, null); ShowCatalog(); return new(ProgramResultCode.Success, "Запись удалена");
                })) ReturnToList(); break;
            case "reset":
                if (await RunOperation("Сохранение копии…", async _ => { await Task.Run(store.Reset); catalog = store.Load(); ShowCatalog(); return new(ProgramResultCode.Success, "Каталог сброшен. Копия исходного файла сохранена"); })) ReturnToList(); break;
        }
    }
    private void ReturnToList(bool focus = true)
    {
        ClosePanel(); Navigation.Panel = ProgramPanel.List; ListsSurface.Visibility = Visibility.Visible; ListsSurface.IsEnabled = ListsSurface.IsHitTestVisible = true; PanelSurface.Visibility = Visibility.Collapsed;
        UpdateEmpty(); ContextChanged?.Invoke(); if (focus) FocusSelection(Navigation.LaunchList ? LaunchList : RunningList);
    }
    public async Task CatalogAsync()
    {
        if (IsBusy) return;
        if (Editing) { if (Editor != null) await Editor.SaveAsync(); return; }
        if (InPanel) return;
        if (!Navigation.LaunchList) { await ConfirmAsync(); return; }
        currentEntry = (LaunchList.SelectedItem as CatalogRow)?.Entry; currentProgram = null;
        if (!catalog.CanSave) { ResetClick(this, new RoutedEventArgs()); return; }
        if (currentEntry == null) ShowPanel(ProgramPanel.Actions, "Каталог запуска", "Каталог пуст", new[] { new Choice("add", "Добавить программу") });
        else ShowActions();
    }
    private void ShowActions()
    {
        if (Navigation.LaunchList && currentEntry == null)
        {
            ShowPanel(ProgramPanel.Actions, "Каталог запуска", "Каталог пуст", new[] { new Choice("add", "Добавить программу") });
            return;
        }
        if (Navigation.LaunchList)
            ShowPanel(ProgramPanel.Actions, "Каталог запуска", currentEntry?.Name ?? "Каталог пуст", new[] { new Choice("edit", "Редактировать"), new Choice("delete", "Удалить запись"), new Choice("add", "Добавить программу") });
        else
            ShowPanel(ProgramPanel.Actions, currentProgram?.Name ?? "Программа", string.Join(", ", currentProgram?.Windows.Select(w => w.Screen).Distinct() ?? []), new[] { new Choice("move", "На главный экран"), new Choice("close", "Закрыть окна") });
    }
    private void ShowCloseChoices(RunningProgram program)
    {
        var selected = ProgramActions.SelectedItem as Choice;
        bool restoreKeyboardFocus = ProgramActions.IsKeyboardFocusWithin;
        currentProgram = program;
        var choices = program.Windows.Select(window => new Choice("close-window", window.DisplayName, window.DisplayDetails, window))
            .Append(new Choice("close-all", "Закрыть все окна", $"Окна: {program.Windows.Count}"))
            .ToArray();
        if (Navigation.Panel == ProgramPanel.CloseWindows)
        {
            PanelTitle.Text = "Какое окно закрыть?";
            PanelDescription.Text = program.Name;
            ProgramActions.ItemsSource = choices;
            ProgramActions.SelectedItem = selected?.Window == null
                ? choices.FirstOrDefault(choice => choice.Id == selected?.Id)
                : choices.FirstOrDefault(choice => choice.Window?.Handle == selected.Window.Handle);
            ProgramActions.SelectedItem ??= choices.FirstOrDefault();
            if (restoreKeyboardFocus) FocusSelection(ProgramActions);
            else ScrollSelection(ProgramActions);
            return;
        }
        ShowPanel(ProgramPanel.CloseWindows, "Какое окно закрыть?", program.Name, choices);
    }
    private void ShowPanel(ProgramPanel panel, string title, string description, System.Collections.IEnumerable choices)
    {
        Navigation.Panel = panel; PanelTitle.Text = title; PanelDescription.Text = description;
        bool confirmation = panel is ProgramPanel.ConfirmDelete or ProgramPanel.ConfirmReset;
        PanelSurface.Background = confirmation ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(185, 8, 10, 13)) : System.Windows.Media.Brushes.Transparent;
        PanelContainer.Width = confirmation ? 680 : double.NaN;
        PanelContainer.MaxHeight = confirmation ? 350 : double.PositiveInfinity;
        PanelContainer.HorizontalAlignment = confirmation ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        PanelContainer.VerticalAlignment = confirmation ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        PanelContainer.Background = confirmation ? (System.Windows.Media.Brush)FindResource("DialogSurface") : System.Windows.Media.Brushes.Transparent;
        PanelContainer.BorderBrush = confirmation ? (System.Windows.Media.Brush)FindResource("Line") : System.Windows.Media.Brushes.Transparent;
        PanelContainer.BorderThickness = confirmation ? new Thickness(1) : new Thickness(0);
        PanelContainer.Padding = confirmation ? new Thickness(24) : new Thickness(0);
        PanelDescription.Text = confirmation ? Navigation.LaunchList ? "Каталог запуска" : "Запущенные программы" : description;
        PanelWarning.Text = "";
        PanelWarning.Visibility = Visibility.Collapsed;
        PanelFeedback.Text = ""; PanelFeedback.ToolTip = null; PanelFeedback.Visibility = Visibility.Collapsed;
        PanelFeedback.SetResourceReference(TextBlock.ForegroundProperty, "MutedText");
        ProgramActions.ItemsSource = choices; ProgramActions.SelectedIndex = 0;
        ListsSurface.Visibility = Visibility.Collapsed;
        ListsSurface.IsEnabled = ListsSurface.IsHitTestVisible = false;
        PanelSurface.Visibility = Visibility.Visible;
        PanelSurface.IsHitTestVisible = true;
        FocusSelection(ProgramActions); ContextChanged?.Invoke();
    }
    private async Task MoveWindow(WindowTarget target)
    {
        bool activate = MoveBehavior == WindowMoveBehavior.ActivateAndClose;
        if (!await RunOperation("Перемещение…", token => mover.MoveToPrimaryAsync(target, token, activate))) return;
        if (activate) { TransferCompleted?.Invoke(); return; }
        ReturnToList();
        // Let WPF commit the panel transition before replacing the list source.
        // Updating ItemsSource in the same layout pass can leave stale selected-row
        // pixels behind after the native window has moved between monitors.
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        await RefreshAsync(quiet: true);
    }
    private async Task<bool> RunOperation(string status, Func<CancellationToken, Task<ProgramResult>> operation)
    {
        if (IsBusy || lifetime == null) return false;
        operating = true; ListsSurface.IsEnabled = false; PanelSurface.IsHitTestVisible = false; Notify(status); ContextChanged?.Invoke();
        PanelFeedback.SetResourceReference(TextBlock.ForegroundProperty, "MutedText");
        try
        {
            var result = await operation(lifetime.Token); Notify(result.Message, result.Details);
            PanelFeedback.SetResourceReference(TextBlock.ForegroundProperty, result.Succeeded ? "Text" : "ErrorText");
            return result.Succeeded;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            string message = ex.Message.ReplaceLineEndings(" ");
            Notify(message.Length <= 140 ? message : message[..137] + "…", ex.Message);
            PanelFeedback.SetResourceReference(TextBlock.ForegroundProperty, "ErrorText");
            return false;
        }
        finally { operating = false; ListsSurface.IsEnabled = !InPanel; PanelSurface.IsHitTestVisible = true; ContextChanged?.Invoke(); }
    }
    private async Task EditEntry(LaunchEntry? entry)
    {
        if (IsBusy || !catalog.CanSave) return;
        editorOpen = true;
        ListsSurface.Visibility = PanelSurface.Visibility = Visibility.Collapsed;
        Editor = new LaunchEntryDialog(entry, async nextEntry => {
                var entries = catalog.Catalog.Entries.Any(e => e.Id == nextEntry.Id)
                    ? catalog.Catalog.Entries.Select(e => e.Id == nextEntry.Id ? nextEntry : e).ToArray()
                    : catalog.Catalog.Entries.Append(nextEntry).ToArray();
                var next = new LaunchCatalog(1, entries);
                await Task.Run(() => store.Save(next)); catalog = new(next, true, null); currentEntry = nextEntry; ShowCatalog(nextEntry.Id); Notify("Программа сохранена");
            });
        Editor.Completed += CloseEditor;
        Editor.BusyChanged += () => ContextChanged?.Invoke();
        EditorHost.Content = Editor; EditorHost.Visibility = Visibility.Visible;
        ContextChanged?.Invoke();
        await Task.CompletedTask;
    }
    private void CloseEditor()
    {
        if (Editor?.IsSaving == true) return;
        editorOpen = false; EditorHost.Visibility = Visibility.Collapsed; EditorHost.Content = null; Editor = null;
        if (Navigation.Panel != ProgramPanel.List) ShowActions();
        else ReturnToList();
        UpdateEmpty(); ContextChanged?.Invoke();
    }
    private void RunningClick(object sender, RoutedEventArgs e) => SelectMode(false);
    private void LaunchClick(object sender, RoutedEventArgs e) => SelectMode(true);
    private async void AddClick(object sender, RoutedEventArgs e) => await EditEntry(null);
    private void ResetClick(object sender, RoutedEventArgs e)
    {
        if (IsBusy || catalog.CanSave) return;
        ShowPanel(ProgramPanel.ConfirmReset, "Сбросить каталог?", "Исходный файл будет сохранён рядом с каталогом как .bak. Список для запуска станет пустым.",
            new[] { new Choice("cancel", "Отмена"), new Choice("reset", "Сохранить копию и сбросить") });
    }
    private ListBoxItem? pressedItem;
    private void ListPress(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is not ListBoxItem item) return;
        pressedItem = item;
        list.SelectedItem = item.DataContext; list.Focus(); e.Handled = true;
    }
    public async Task SecondaryAsync()
    {
        if (IsBusy || InPanel) return;
        if (Navigation.LaunchList)
        {
            if (LaunchList.SelectedItem is CatalogRow row) { currentEntry = row.Entry; currentProgram = null; await EditEntry(row.Entry); }
            return;
        }
        if (RunningList.SelectedItem is not RunningProgram program) return;
        currentProgram = program; currentEntry = null;
        ShowCloseChoices(program);
    }
    public async Task CreateAsync()
    {
        if (IsBusy || InPanel || !Navigation.LaunchList) return;
        currentEntry = null; currentProgram = null;
        await EditEntry(null);
    }
    public void DeleteEditing()
    {
        if (!Editing || currentEntry == null || Editor?.IsSaving == true) return;
        editorOpen = false;
        EditorHost.Visibility = Visibility.Collapsed;
        EditorHost.Content = null;
        Editor = null;
        ShowPanel(ProgramPanel.ConfirmDelete, $"Удалить запись «{currentEntry.Name}»?", "Приложение и файл останутся на компьютере.",
            new[] { new Choice("cancel", "Отмена"), new Choice("delete-entry", "Удалить запись") });
    }
    private async void ListClick(object sender, MouseButtonEventArgs e)
    {
        if (IsBusy || sender is not ListBox list ||
            ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is not ListBoxItem item) return;
        e.Handled = true;
        if (!ReferenceEquals(item, pressedItem)) return;
        list.SelectedItem = item.DataContext; pressedItem = null; await ConfirmAsync();
    }
}

