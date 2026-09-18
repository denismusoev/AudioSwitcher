namespace AudioSwitcher.Core;
public enum PadAction { None, Up, Down, Left, Right, Confirm, Close }
public sealed class InputGate
{
    private PadAction previous;
    private long nextRepeat;
    public bool Accept(PadAction action, long milliseconds)
    {
        if (action == PadAction.None) { previous = action; return false; }
        if (action != previous) { previous = action; nextRepeat = milliseconds + 400; return true; }
        if (action is PadAction.Confirm or PadAction.Close || milliseconds < nextRepeat) return false;
        nextRepeat = milliseconds + 140;
        return true;
    }
}
