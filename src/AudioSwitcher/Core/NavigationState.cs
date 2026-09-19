namespace AudioSwitcher.Core;

public enum AppSection
{
    Control = 0,
    Running = 1,
    Launch = 2,
    Audio = Control,
    Programs = Running,
    Displays = Launch,
    Settings = Launch
}
public enum ProgramPanel { List, Actions, Windows, CloseWindows, ConfirmDelete, ConfirmReset }
public sealed class NavigationState
{
    public AppSection Section { get; set; }
    public bool LaunchList { get; set; }
    public ProgramPanel Panel { get; set; }
    public bool ChangeSection(int direction)
    {
        if (Panel != ProgramPanel.List) return false;
        const int count = 3;
        int next = ((int)Section + Math.Sign(direction) + count) % count;
        Section = (AppSection)next;
        LaunchList = Section == AppSection.Launch;
        return true;
    }
    public bool ToggleList()
    {
        if (Section != AppSection.Programs || Panel != ProgramPanel.List) return false;
        LaunchList = !LaunchList; return true;
    }
    public bool Back()
    {
        if (Panel == ProgramPanel.List) return false;
        Panel = ProgramPanel.List;
        return true;
    }
}
