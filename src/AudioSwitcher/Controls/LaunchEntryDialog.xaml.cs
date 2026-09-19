using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using AudioSwitcher.Core;
using AudioSwitcher.Platform;

namespace AudioSwitcher.Controls;
public partial class LaunchEntryDialog : System.Windows.Controls.UserControl
{
    private readonly Guid id;
    private readonly Func<LaunchEntry, Task> save;
    private bool saving;
    public bool IsSaving => saving;
    public event Action? Completed;
    public event Action? BusyChanged;
    public LaunchEntryDialog(LaunchEntry? entry, Func<LaunchEntry, Task> save)
    {
        this.save = save; id = entry?.Id ?? Guid.NewGuid();
        InitializeComponent();
        if (entry != null)
        {
            EditorTitle.Text = "Редактировать программу"; EntryName.Text = entry.Name; EntryTarget.Text = entry.Target;
            EntryArguments.Text = entry.Arguments; EntryDirectory.Text = entry.WorkingDirectory;
        }
        Loaded += (_, _) => SelectField(0);
    }
    private LaunchKind Kind => Path.GetExtension(EntryTarget.Text.Trim()).ToLowerInvariant() switch { ".lnk" => LaunchKind.Shortcut, ".url" => LaunchKind.InternetShortcut, _ => LaunchKind.Executable };
    private void TargetChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (ExecutableOptions == null) return;
        bool executable = Kind == LaunchKind.Executable;
        ExecutableOptions.Visibility = executable ? Visibility.Visible : Visibility.Collapsed;
        ShortcutHint.Visibility = executable ? Visibility.Collapsed : Visibility.Visible;
    }
    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Программы и ярлыки|*.exe;*.lnk;*.url|Программы|*.exe|Ярлыки|*.lnk;*.url", CheckFileExists = true, DereferenceLinks = false };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;
        EntryTarget.Text = picker.FileName;
        if (string.IsNullOrWhiteSpace(EntryName.Text)) EntryName.Text = Path.GetFileNameWithoutExtension(picker.FileName);
    }
    private async void SaveClick(object sender, RoutedEventArgs e)
        => await SaveAsync();
    public async Task SaveAsync()
    {
        if (saving) return;
        saving = true; IsEnabled = false; EditorError.Text = ""; BusyChanged?.Invoke();
        try
        {
            var kind = Kind;
            var entry = new LaunchEntry(id, EntryName.Text.Trim(), kind, EntryTarget.Text.Trim(),
                kind == LaunchKind.Executable ? EntryArguments.Text : "", kind == LaunchKind.Executable ? EntryDirectory.Text.Trim() : "");
            await Task.Run(() => new ProgramLaunchService().Validate(entry));
            await save(entry); saving = false; Close();
        }
        catch (Exception ex) { EditorError.Text = ex.Message; }
        finally
        {
            saving = false; IsEnabled = true;
            if (IsVisible)
            {
                if (IsTextEditing) Inputs[field].Focus();
                else Attributes[field].Focus();
            }
            BusyChanged?.Invoke();
        }
    }
    private int field;
    public bool IsTextEditing { get; private set; }
    private System.Windows.Controls.Border[] Attributes => [NameAttribute, TargetAttribute, ArgumentsAttribute, DirectoryAttribute];
    private System.Windows.Controls.TextBox[] Inputs => [EntryName, EntryTarget, EntryArguments, EntryDirectory];
    private void SelectField(int index)
    {
        field = index; IsTextEditing = false;
        Attributes[field].Focus(); Attributes[field].BringIntoView();
    }
    public void MoveField(PadAction direction)
    {
        if (saving || IsTextEditing) return;
        int delta = direction switch { PadAction.Up => -1, PadAction.Down => 1, PadAction.Left => -2, PadAction.Right => 2, _ => 0 };
        int next = field + delta;
        if (delta != 0 && next >= 0 && next < Attributes.Length && Attributes[next].IsVisible) SelectField(next);
    }
    public void ConfirmField()
    {
        if (saving) return;
        if (IsTextEditing) return;
        IsTextEditing = true; Inputs[field].Focus();
    }
    public bool EndFieldInput()
    {
        if (!IsTextEditing) return false;
        SelectField(field); return true;
    }
    private void AttributeMouseDown(object sender, MouseButtonEventArgs e)
    {
        field = Array.IndexOf(Attributes, sender);
        IsTextEditing = true; Inputs[field].Focus();
    }
    private void InputFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        field = Array.IndexOf(Inputs, sender); IsTextEditing = true;
    }
    public void Close() { if (!saving) { Visibility = Visibility.Collapsed; Completed?.Invoke(); } }
    private void CancelClick(object sender, RoutedEventArgs e) => Close();
    private void DialogKeyDown(object sender, KeyEventArgs e)
    {
        if (saving || (e.Key == Key.Enter && e.IsRepeat)) e.Handled = true;
        else if (e.Key == Key.Escape) { if (!EndFieldInput()) Close(); e.Handled = true; }
        else if (e.Key == Key.F2) { _ = SaveAsync(); e.Handled = true; }
        else if (e.Key == Key.Enter) { ConfirmField(); e.Handled = true; }
        else if (!IsTextEditing && e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
        {
            MoveField(e.Key switch { Key.Up => PadAction.Up, Key.Down => PadAction.Down, Key.Left => PadAction.Left, _ => PadAction.Right });
            e.Handled = true;
        }
    }
}
