using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    public NavigationState Navigation { get; } = new();
    public bool IsBusy => operating || editorOpen;
    public bool InPanel => Navigation.Panel != ProgramPanel.List;
    public string ActionHint => InPanel ? "Выбрать" : Navigation.LaunchList ? "Открыть" : "Действия";
    public event Action<string, string?>? StatusChanged;
    public event Action? ContextChanged;

    public ProgramsView() : this(new LaunchCatalogStore()) { }
    public ProgramsView(LaunchCatalogStore store) { this.store = store; InitializeComponent(); }
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
    private void Notify(string text, string? details = null) => StatusChanged?.Invoke(text, details);
    private void ShowCatalog(Guid? preferred = null)
    {
        Guid? selected = preferred ?? (LaunchList.SelectedItem as CatalogRow)?.Entry.Id;
        var rows = catalog.Catalog.Entries.Select(e => new CatalogRow(e)).ToArray();
        LaunchList.ItemsSource = rows; LaunchList.SelectedItem = rows.FirstOrDefault(r => r.Entry.Id == selected) ?? rows.FirstOrDefault();
        AddProgram.IsEnabled = ConfigureProgram.IsEnabled = catalog.CanSave;
        ResetCatalog.Visibility = catalog.CanSave ? Visibility.Collapsed : Visibility.Visible;
    }
    private void UpdateLists()
    {
        RunningList.Visibility = Navigation.LaunchList ? Visibility.Collapsed : Visibility.Visible;
        LaunchList.Visibility = Navigation.LaunchList ? Visibility.Visible : Visibility.Collapsed;
        CatalogTools.Visibility = Navigation.LaunchList ? Visibility.Visible : Visibility.Collapsed;
        RunningMode.BorderBrush = Navigation.LaunchList ? System.Windows.Media.Brushes.Transparent : (System.Windows.Media.Brush)FindResource("Accent");
        LaunchMode.BorderBrush = Navigation.LaunchList ? (System.Windows.Media.Brush)FindResource("Accent") : System.Windows.Media.Brushes.Transparent;
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
    public bool Back()
    {
        if (IsBusy) return true;
        if (!Navigation.Back()) return false;
        if (Navigation.Panel == ProgramPanel.Actions) ShowActions();
        else
        {
            ListsSurface.Visibility = Visibility.Visible; PanelSurface.Visibility = Visibility.Collapsed;
            (Navigation.LaunchList ? LaunchList : RunningList).Focus(); UpdateEmpty(); ContextChanged?.Invoke();
        }
        return true;
    }
    public async Task ConfirmAsync()
    {
        if (IsBusy) return;
        if (!InPanel)
        {
            if (Navigation.LaunchList && LaunchList.SelectedItem is CatalogRow row)
                await RunOperation("Открытие…", async token => { await launcher.LaunchAsync(row.Entry, token); return new(ProgramResultCode.Success, "Команда запуска отправлена"); });
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
            case "move":
                if (currentProgram == null) return;
                if (currentProgram.Windows.Count == 1) await MoveWindow(currentProgram.Windows[0]);
                else ShowPanel(ProgramPanel.Windows, "Какое окно переместить?", currentProgram.Name, currentProgram.Windows);
                break;
            case "terminate":
                ShowPanel(ProgramPanel.ConfirmTermination, $"Завершить {currentProgram?.Name}?", "Несохранённые данные будут потеряны. Все окна выбранного процесса закроются.",
                    new[] { new Choice("cancel", "Отмена"), new Choice("kill", "Завершить принудительно") }); break;
            case "kill":
                if (currentProgram != null) await RunOperation("Завершение…", token => processes.TerminateAsync(currentProgram.Identity, token));
                ReturnToList(); await RefreshAsync(quiet: true); break;
            case "edit": if (currentEntry != null) await EditEntry(currentEntry); break;
            case "delete":
                ShowPanel(ProgramPanel.ConfirmDelete, $"Удалить запись «{currentEntry?.Name}»?", "Приложение и файл ярлыка останутся на компьютере.",
                    new[] { new Choice("cancel", "Отмена"), new Choice("delete-entry", "Удалить запись") }); break;
            case "delete-entry":
                if (currentEntry == null) return;
                await RunOperation("Сохранение…", async _ => {
                    var next = new LaunchCatalog(1, catalog.Catalog.Entries.Where(e => e.Id != currentEntry.Id).ToArray());
                    await Task.Run(() => store.Save(next)); catalog = new(next, true, null); ShowCatalog(); return new(ProgramResultCode.Success, "Запись удалена");
                }); ReturnToList(); break;
            case "reset":
                await RunOperation("Сохранение копии…", async _ => { await Task.Run(store.Reset); catalog = store.Load(); ShowCatalog(); return new(ProgramResultCode.Success, "Каталог сброшен. Копия исходного файла сохранена"); });
                ReturnToList(); break;
        }
    }
    private void ReturnToList()
    {
        Navigation.Panel = ProgramPanel.List; ListsSurface.Visibility = Visibility.Visible; PanelSurface.Visibility = Visibility.Collapsed;
        UpdateEmpty(); ContextChanged?.Invoke(); (Navigation.LaunchList ? LaunchList : RunningList).Focus();
    }
    private void ShowActions()
    {
        if (Navigation.LaunchList)
            ShowPanel(ProgramPanel.Actions, currentEntry?.Name ?? "Запись каталога", "Настройка записи для запуска", new[] { new Choice("edit", "Изменить запись"), new Choice("delete", "Удалить запись") });
        else
            ShowPanel(ProgramPanel.Actions, currentProgram?.Name ?? "Программа", "Выберите действие", new[] { new Choice("move", "На главный экран", "Переместить выбранное окно"), new Choice("terminate", "Завершить принудительно", "Закрыть выбранный процесс") });
    }
    private void ShowPanel(ProgramPanel panel, string title, string description, System.Collections.IEnumerable choices)
    {
        Navigation.Panel = panel; PanelTitle.Text = title; PanelDescription.Text = description;
        ProgramActions.ItemsSource = choices; ProgramActions.SelectedIndex = 0;
        ListsSurface.Visibility = Visibility.Collapsed; PanelSurface.Visibility = Visibility.Visible;
        ProgramActions.Focus(); ContextChanged?.Invoke();
    }
    private async Task MoveWindow(WindowTarget target)
    {
        await RunOperation("Перемещение…", token => mover.MoveToPrimaryAsync(target, token));
        ReturnToList(); await RefreshAsync(quiet: true);
    }
    private async Task RunOperation(string status, Func<CancellationToken, Task<ProgramResult>> operation)
    {
        if (IsBusy || lifetime == null) return;
        operating = true; ListsSurface.IsEnabled = PanelSurface.IsEnabled = false; Notify(status); ContextChanged?.Invoke();
        try { var result = await operation(lifetime.Token); Notify(result.Message, result.Details); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            string message = ex.Message.ReplaceLineEndings(" ");
            Notify(message.Length <= 140 ? message : message[..137] + "…", ex.Message);
        }
        finally { operating = false; ListsSurface.IsEnabled = PanelSurface.IsEnabled = true; ContextChanged?.Invoke(); }
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
    private void ConfigureClick(object sender, RoutedEventArgs e)
    {
        if (IsBusy || LaunchList.SelectedItem is not CatalogRow row) return;
        currentEntry = row.Entry; currentProgram = null; ShowActions();
    }
    private void ResetClick(object sender, RoutedEventArgs e)
    {
        if (IsBusy || catalog.CanSave) return;
        ShowPanel(ProgramPanel.ConfirmReset, "Сбросить каталог?", "Исходный файл будет сохранён рядом с каталогом как .bak. Список для запуска станет пустым.",
            new[] { new Choice("cancel", "Отмена"), new Choice("reset", "Сохранить копию и сбросить") });
    }
    private async void ListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox list && ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is ListBoxItem) await ConfirmAsync();
    }
    private async void ActionsDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ProgramActions, e.OriginalSource as DependencyObject) is ListBoxItem) await ConfirmAsync();
    }
}
