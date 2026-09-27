namespace AudioSwitcher.Core;

public enum PadAction
{
    None, Up, Down, Left, Right, Confirm, Close,
    Secondary, CreateOrEdit, Settings, Details,
    ToggleList = Secondary
}

[Flags]
public enum GamepadButtons : ushort
{
    None = 0,
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Menu = 0x0010,
    View = 0x0020,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000
}

public enum GamepadControl
{
    None,
    DPadUp,
    DPadDown,
    DPadLeft,
    DPadRight,
    LeftStickUp,
    LeftStickDown,
    LeftStickLeft,
    LeftStickRight,
    Confirm,
    Close,
    Secondary,
    CreateOrEdit,
    Settings,
    Details
}

public readonly record struct GamepadSnapshot(
    int ControllerId,
    bool Connected,
    uint Packet,
    GamepadButtons Buttons,
    short LeftX,
    short LeftY);

public readonly record struct GamepadCommand(
    int ControllerId,
    GamepadControl Control,
    PadAction Action,
    long Timestamp,
    bool IsRepeat);

public enum GamepadCommandResult { Handled, ContextChanged, Blocked, Unhandled }

public enum GamepadDiagnosticResult
{
    Connected,
    NeutralBaseline,
    Generated,
    Suppressed,
    DeliveryDisabled,
    Disconnected,
    Handled,
    ContextChanged,
    Blocked,
    Unhandled
}

public readonly record struct GamepadDiagnosticEvent(
    long Timestamp,
    int ControllerId,
    GamepadControl Control,
    PadAction Action,
    bool IsRepeat,
    GamepadDiagnosticResult Result);
