using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AudioSwitcher.Core;
using AudioSwitcher.Platform;

namespace AudioSwitcher.Controls;

public partial class ProgramsView : UserControl
{
    private sealed record Choice(string Id, string DisplayName, string DisplayDetails = "");
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
    private ContentControl? modalHost;
    public NavigationState Navigation { get; } = new();
    public bool IsBusy => operating || editorOpen;
    public bool InPanel => Navigation.Panel != ProgramPanel.List;
    public string ActionHint => IsBusy ? "Подождите" : InPanel ? "Выбрать" : "Действия";
    public event Action<string, string?>? StatusChanged;
    public event Action? ContextChanged;

    public ProgramsView() : this(new LaunchCatalogStore()) { }
    public ProgramsView(LaunchCatalogStore store)
    {
        this.store = store; InitializeComponent();
        PanelSurface.SizeChanged += (_, e) => PanelCard.MaxHeight = Math.Max(0, e.NewSize.Height - 8);
    }
    public void SetModalHost(ContentControl host)
    {
        // Attach once: opening and closing a menu must not rebuild its visual tree.
        ViewRoot.Children.Remove(PanelSurface);
        modalHost = host; host.Content = PanelSurface;
    }
    private void CloseModal()
    {
        if (modalHost != null) modalHost.Visibility = Visibility.Collapsed;
    }
    public void Enter()
    {
        Leave(); lifetime = new();
        catalog = store.Load(); ShowCatalog(); UpdateLists();
        (Navigation.LaunchList ? LaunchList : RunningList).Focus();
        if (catalog.Error != null && Navigation.LaunchList) Notify("Каталог недоступен. Можно сохранить копию и сбросить", catalog.Error);
        _ = RefreshAsync();
    }
    public void Leave() { lifetime?.Cancel(); lifetime?.Dispose(); lifetime = null; }
    public async Task RefreshAsync(bool quiet = false)
    {
        if (Navigation.Section != AppSection.Programs || Navigation.LaunchList || InPanel || IsBusy || refreshing || lifetime == null) return;
        var active = lifetime; var token = active.Token; refreshing = true;
        if (!quiet) Notify("Загрузка приложений…");
        try
        {
            var snapshot = await windows.GetProgramsAsync(token);
            if (active != lifetime || Navigation.Section != AppSection.Programs || Navigation.LaunchList || InPanel) return;
            string signature = string.Join("|", snapshot.Select(p => $"{p.Identity}:{p.Name}:{string.Join(';', p.Windows.Select(w => $"{w.Handle}:{w.Title}:{w.Screen}:{w.NotResponding}"))}"));
            if (signature != snapshotSignature)
            {
                var selected = (RunningList.SelectedItem as RunningProgram)?.Identity;
                RunningList.ItemsSource = snapshot;
                RunningList.SelectedItem = snapshot.FirstOrDefault(p => p.Identity == selected) ?? snapshot.FirstOrDefault();
                snapshotSignature = signature;
                ScrollSelection(RunningList);
            }
            UpdateEmpty(); if (!quiet) Notify("Готово");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (active == lifetime) Notify("Не удалось загрузить приложения", ex.Message); }
        finally { refreshing = false; }
    }
    private void Notify(string text, string? details = null)
    {
        if (InPanel && operating)
        {
            PanelFeedback.Text = text; PanelFeedback.ToolTip = details;
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
        RunningList.Visibility = Navigation.LaunchList ? Visibility.Collapsed : Visibility.Visible;
        LaunchList.Visibility = Navigation.LaunchList ? Visibility.Visible : Visibility.Collapsed;
        CatalogTools.Visibility = Navigation.LaunchList ? Visibility.Visible : Visibility.Collapsed;
        RunningMode.Foreground = (System.Windows.Media.Brush)FindResource(Navigation.LaunchList ? "MutedText" : "Text");
        LaunchMode.Foreground = (System.Windows.Media.Brush)FindResource(Navigation.LaunchList ? "Text" : "MutedText");
        RunningMode.FontWeight = Navigation.LaunchList ? FontWeights.Normal : FontWeights.SemiBold;
        LaunchMode.FontWeight = Navigation.LaunchList ? FontWeights.SemiBold : FontWeights.Normal;
        System.Windows.Automation.AutomationProperties.SetHelpText(RunningMode, Navigation.LaunchList ? "Показать запущенные программы" : "Выбранный список");
        System.Windows.Automation.AutomationProperties.SetHelpText(LaunchMode, Navigation.LaunchList ? "Выбранный список" : "Показать программы для запуска");
        UpdateEmpty(); ContextChanged?.Invoke();
    }
    private void UpdateEmpty()
    {
        EmptyPrograms.Text = Navigation.LaunchList ? catalog.CanSave ? "Добавьте программы для запуска" : "Каталог не прочитан. Исходный файл сохранён" : "Нет запущенных приложений с окнами";
        EmptyPrograms.Visibility = (Navigation.LaunchList ? LaunchList : RunningList).Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    public void SelectMode(bool launch)
    {
        if (InPanel || IsBusy || Navigation.LaunchList == launch) return;
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
        var list = InPanel ? ProgramActions : Navigation.LaunchList ? LaunchList : RunningList;
        if (list.Items.Count == 0) return;
        list.SelectedIndex = Math.Clamp(list.SelectedIndex + direction, 0, list.Items.Count - 1); list.Focus(); ScrollSelection(list);
    }
    private static void ScrollSelection(ListBox list) { if (list.SelectedItem != null) list.ScrollIntoView(list.SelectedItem); }
    private static void FocusSelection(ListBox list)
    {
        list.UpdateLayout(); ScrollSelection(list);
        if (list.SelectedItem != null && list.ItemContainerGenerator.ContainerFromItem(list.SelectedItem) is ListBoxItem item) item.Focus();
        else list.Focus();
    }
    public bool Back()
    {
        if (IsBusy) return true;
        if (!Navigation.Back()) return false;
        if (Navigation.Panel == ProgramPanel.Actions) ShowActions();
        else
        {
            CloseModal(); ListsSurface.Visibility = Visibility.Visible; ListsSurface.IsEnabled = ListsSurface.IsHitTestVisible = true; PanelSurface.Visibility = Visibility.Collapsed;
            FocusSelection(Navigation.LaunchList ? LaunchList : RunningList); UpdateEmpty(); ContextChanged?.Invoke();
        }
        return true;
    }
    public async Task ConfirmAsync()
    {
        if (IsBusy) return;
        if (!InPanel)
        {
            if (Navigation.LaunchList && LaunchList.SelectedItem is CatalogRow row)
            { currentEntry = row.Entry; currentProgram = null; ShowActions(); }
            else if (!Navigation.LaunchList && RunningList.SelectedItem is RunningProgram program)
            { currentProgram = program; currentEntry = null; ShowActions(); }
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
            case "terminate":
                ShowPanel(ProgramPanel.ConfirmTermination, $"Завершить {currentProgram?.Name}?", "Несохранённые данные будут потеряны. Все окна выбранного процесса закроются.",
                    new[] { new Choice("cancel", "Отмена"), new Choice("kill", "Завершить принудительно") }); break;
            case "kill":
                if (currentProgram != null && await RunOperation("Завершение…", token => processes.TerminateAsync(currentProgram.Identity, token)))
                { ReturnToList(); await RefreshAsync(quiet: true); } break;
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
    private void ReturnToList()
    {
        CloseModal(); Navigation.Panel = ProgramPanel.List; ListsSurface.Visibility = Visibility.Visible; ListsSurface.IsEnabled = ListsSurface.IsHitTestVisible = true; PanelSurface.Visibility = Visibility.Collapsed;
        UpdateEmpty(); ContextChanged?.Invoke(); FocusSelection(Navigation.LaunchList ? LaunchList : RunningList);
    }
    private void ShowActions()
    {
        if (Navigation.LaunchList)
            ShowPanel(ProgramPanel.Actions, currentEntry?.Name ?? "Запись каталога", "Выберите действие", new[] { new Choice("launch", "Запустить"), new Choice("delete", "Удалить"), new Choice("edit", "Редактировать"), new Choice("cancel", "Отмена") });
        else
            ShowPanel(ProgramPanel.Actions, currentProgram?.Name ?? "Программа", "Выберите действие", new[] { new Choice("move", "На главный экран", "Переместить окно на текущий главный монитор"), new Choice("terminate", "Завершить принудительно", "Все окна этого экземпляра программы закроются") });
    }
    private void ShowPanel(ProgramPanel panel, string title, string description, System.Collections.IEnumerable choices)
    {
        Navigation.Panel = panel; PanelTitle.Text = title; PanelDescription.Text = description;
        PanelFeedback.Text = ""; PanelFeedback.ToolTip = null; PanelFeedback.Visibility = Visibility.Collapsed;
        PanelFeedback.Foreground = (System.Windows.Media.Brush)FindResource("MutedText");
        ProgramActions.ItemsSource = choices; ProgramActions.SelectedIndex = 0;
        if (modalHost != null) modalHost.Visibility = Visibility.Visible;
        ListsSurface.Visibility = Visibility.Visible;
        ListsSurface.IsEnabled = ListsSurface.IsHitTestVisible = false;
        PanelSurface.Visibility = Visibility.Visible;
        PanelSurface.IsHitTestVisible = true;
        FocusSelection(ProgramActions); ContextChanged?.Invoke();
    }
    private async Task MoveWindow(WindowTarget target)
    {
        if (!await RunOperation("Перемещение…", token => mover.MoveToPrimaryAsync(target, token))) return;
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
        PanelFeedback.Foreground = (System.Windows.Media.Brush)FindResource("MutedText");
        try
        {
            var result = await operation(lifetime.Token); Notify(result.Message, result.Details);
            PanelFeedback.Foreground = (System.Windows.Media.Brush)FindResource(result.Succeeded ? "Text" : "ErrorText");
            return result.Succeeded;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            string message = ex.Message.ReplaceLineEndings(" ");
            Notify(message.Length <= 140 ? message : message[..137] + "…", ex.Message);
            PanelFeedback.Foreground = (System.Windows.Media.Brush)FindResource("ErrorText");
            return false;
        }
        finally { operating = false; ListsSurface.IsEnabled = !InPanel; PanelSurface.IsHitTestVisible = true; ContextChanged?.Invoke(); }
    }
    private async Task EditEntry(LaunchEntry? entry)
    {
        if (IsBusy || !catalog.CanSave) return;
        editorOpen = true; ContextChanged?.Invoke();
        try
        {
            var dialog = new LaunchEntryDialog(entry, async nextEntry => {
                var entries = catalog.Catalog.Entries.Where(e => e.Id != nextEntry.Id).Append(nextEntry).ToArray();
                var next = new LaunchCatalog(1, entries);
                await Task.Run(() => store.Save(next)); catalog = new(next, true, null); currentEntry = nextEntry; ShowCatalog(nextEntry.Id); Notify("Программа сохранена");
            }) { Owner = Window.GetWindow(this) };
            dialog.ShowDialog();
        }
        finally { editorOpen = false; ContextChanged?.Invoke(); }
        if (InPanel) ShowActions(); UpdateEmpty();
        await Task.CompletedTask;
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
    private async void ListClick(object sender, MouseButtonEventArgs e)
    {
        // A double-click's second release must not execute a newly opened panel.
        if (IsBusy || e.ClickCount > 1 || sender is not ListBox list ||
            ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is not ListBoxItem item) return;
        list.SelectedItem = item.DataContext; e.Handled = true; await ConfirmAsync();
    }
}
