using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using AudioSwitcher.Core;
using AudioSwitcher.Platform;

namespace AudioSwitcher.Controls;
public partial class LaunchEntryDialog : Window
{
    private readonly Guid id;
    private readonly Func<LaunchEntry, Task> save;
    private bool saving;
    public LaunchEntryDialog(LaunchEntry? entry, Func<LaunchEntry, Task> save)
    {
        this.save = save; id = entry?.Id ?? Guid.NewGuid();
        InitializeComponent();
        if (entry != null)
        {
            Title = "Редактировать программу"; EntryName.Text = entry.Name; EntryTarget.Text = entry.Target;
            EntryArguments.Text = entry.Arguments; EntryDirectory.Text = entry.WorkingDirectory;
        }
        Loaded += (_, _) => EntryName.Focus();
        Closing += (_, e) => { if (saving) e.Cancel = true; };
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
        if (picker.ShowDialog(this) != true) return;
        EntryTarget.Text = picker.FileName;
        if (string.IsNullOrWhiteSpace(EntryName.Text)) EntryName.Text = Path.GetFileNameWithoutExtension(picker.FileName);
    }
    private async void SaveClick(object sender, RoutedEventArgs e)
    {
        if (saving) return;
        saving = true; SaveEntry.IsEnabled = false; EditorError.Text = "";
        try
        {
            var kind = Kind;
            var entry = new LaunchEntry(id, EntryName.Text.Trim(), kind, EntryTarget.Text.Trim(),
                kind == LaunchKind.Executable ? EntryArguments.Text : "", kind == LaunchKind.Executable ? EntryDirectory.Text.Trim() : "");
            await Task.Run(() => new ProgramLaunchService().Validate(entry));
            await save(entry); saving = false; DialogResult = true;
        }
        catch (Exception ex) { EditorError.Text = ex.Message; }
        finally { saving = false; SaveEntry.IsEnabled = true; }
    }
    private void DialogKeyDown(object sender, KeyEventArgs e)
    {
        if (saving || (e.Key == Key.Enter && e.IsRepeat)) e.Handled = true;
    }
}
