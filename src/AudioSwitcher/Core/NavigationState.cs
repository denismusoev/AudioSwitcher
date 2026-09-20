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
public enum NavigationTransition { None, SectionChanged, EnteredSection, LeftSection }
public sealed class NavigationState
{
    public AppSection Section { get; set; }
    public bool LaunchList { get; set; }
    public ProgramPanel Panel { get; set; }
    public bool SectionActive { get; private set; }
    public bool ChangeSection(int direction)
    {
        if (Panel != ProgramPanel.List || SectionActive) return false;
        const int count = 3;
        int next = ((int)Section + Math.Sign(direction) + count) % count;
        Section = (AppSection)next;
        LaunchList = Section == AppSection.Launch;
        return true;
    }
    public bool EnterSection()
    {
        if (SectionActive || Panel != ProgramPanel.List) return false;
        SectionActive = true;
        return true;
    }
    public bool LeaveSection()
    {
        if (!SectionActive || Panel != ProgramPanel.List) return false;
        SectionActive = false;
        return true;
    }
    public NavigationTransition Navigate(PadAction action)
    {
        if (Panel != ProgramPanel.List) return NavigationTransition.None;
        if (!SectionActive && action is PadAction.Up or PadAction.Down)
            return ChangeSection(action == PadAction.Up ? -1 : 1) ? NavigationTransition.SectionChanged : NavigationTransition.None;
        if (!SectionActive && action is PadAction.Right or PadAction.Confirm)
            return EnterSection() ? NavigationTransition.EnteredSection : NavigationTransition.None;
        if (SectionActive && action is PadAction.Left or PadAction.Close)
            return LeaveSection() ? NavigationTransition.LeftSection : NavigationTransition.None;
        return NavigationTransition.None;
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
