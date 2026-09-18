namespace AudioSwitcher.Core;

public enum AppSection { Audio, Displays, Programs }
public enum ProgramPanel { List, Actions, Windows, ConfirmTermination, ConfirmDelete, ConfirmReset }
public sealed class NavigationState
{
    public AppSection Section { get; set; }
    public bool LaunchList { get; set; }
    public ProgramPanel Panel { get; set; }
    public bool ChangeSection(int direction)
    {
        if (Panel != ProgramPanel.List) return false;
        var next = (AppSection)Math.Clamp((int)Section + direction, 0, 2);
        if (next == Section) return false;
        Section = next; return true;
    }
    public bool ToggleList()
    {
        if (Section != AppSection.Programs || Panel != ProgramPanel.List) return false;
        LaunchList = !LaunchList; return true;
    }
    public bool Back()
    {
        if (Panel == ProgramPanel.List) return false;
        Panel = Panel is ProgramPanel.Actions or ProgramPanel.ConfirmReset ? ProgramPanel.List : ProgramPanel.Actions;
        return true;
    }
}
