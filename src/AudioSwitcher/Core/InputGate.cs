namespace AudioSwitcher.Core;
public sealed class InputGate
{
    private PadAction previous;
    private long nextRepeat;
    private bool requireRelease;
    public void RequireRelease()
    {
        if (previous != PadAction.None) requireRelease = true;
    }
    public bool Accept(PadAction action, long milliseconds)
    {
        if (requireRelease)
        {
            if (action == PadAction.None) { requireRelease = false; previous = PadAction.None; }
            return false;
        }
        if (action == PadAction.None) { previous = action; return false; }
        if (action != previous) { previous = action; nextRepeat = milliseconds + 400; return true; }
        if (action is PadAction.Confirm or PadAction.Close or PadAction.Secondary or PadAction.CreateOrEdit or PadAction.Settings or PadAction.Details || milliseconds < nextRepeat) return false;
        nextRepeat = milliseconds + 140;
        return true;
    }
}
