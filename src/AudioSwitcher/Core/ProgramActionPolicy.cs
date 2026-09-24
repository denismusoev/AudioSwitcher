namespace AudioSwitcher.Core;

public static class ProgramActionPolicy
{
    public static WindowTarget? DirectCloseTarget(RunningProgram program) =>
        program.Windows.Count == 1 ? program.Windows[0] : null;
}
