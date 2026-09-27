namespace AudioSwitcher.Core;

public sealed class GamepadInputEngine
{
    private const int StickActivation = 16000;
    private const int StickRelease = 10000;
    private const long InitialRepeatDelay = 400;
    private const long RepeatInterval = 140;

    private readonly Dictionary<int, ControllerState> controllers = [];
    private readonly HashSet<(int Controller, GamepadControl Control)> suppressed = [];

    public IReadOnlyList<GamepadCommand> Update(IReadOnlyList<GamepadSnapshot> snapshots, long timestamp, bool deliveryEnabled)
    {
        List<GamepadCommand>? commands = null;
        foreach (var snapshot in snapshots)
        {
            if (!snapshot.Connected)
            {
                controllers.Remove(snapshot.ControllerId);
                suppressed.RemoveWhere(item => item.Controller == snapshot.ControllerId);
                continue;
            }

            if (!controllers.TryGetValue(snapshot.ControllerId, out var state))
                controllers[snapshot.ControllerId] = state = new ControllerState();

            var buttons = snapshot.Buttons;
            var pressed = buttons & ~state.Buttons;
            UpdateStickState(state, snapshot.LeftX, snapshot.LeftY);
            var direction = SelectDirection(state, buttons);
            ClearReleasedSuppressions(snapshot.ControllerId, buttons, direction);

            var buttonCommand = SelectButtonCommand(snapshot.ControllerId, pressed, timestamp);
            GamepadCommand? directionCommand = UpdateDirection(snapshot.ControllerId, state, direction, timestamp);
            state.Buttons = buttons;

            if (!deliveryEnabled) continue;
            GamepadCommand? selected = buttonCommand ?? ((buttons & MappedButtons) != 0 ? null : directionCommand);
            if (selected is not GamepadCommand command || IsSuppressed(command)) continue;
            (commands ??= []).Add(command);
        }
        return commands ?? [];
    }

    public void Report(GamepadCommand command, GamepadCommandResult result)
    {
        if (result == GamepadCommandResult.ContextChanged)
            suppressed.Add((command.ControllerId, command.Control));
    }

    private static readonly GamepadButtons MappedButtons = GamepadButtons.A | GamepadButtons.B | GamepadButtons.X |
        GamepadButtons.Y | GamepadButtons.Menu | GamepadButtons.View;

    private bool IsSuppressed(GamepadCommand command) => suppressed.Contains((command.ControllerId, command.Control));

    private void ClearReleasedSuppressions(int controller, GamepadButtons buttons, GamepadControl direction)
    {
        suppressed.RemoveWhere(item => item.Controller == controller && !IsActive(item.Control, buttons, direction));
    }

    private static bool IsActive(GamepadControl control, GamepadButtons buttons, GamepadControl direction) => control switch
    {
        GamepadControl.Confirm => buttons.HasFlag(GamepadButtons.A),
        GamepadControl.Close => buttons.HasFlag(GamepadButtons.B),
        GamepadControl.Secondary => buttons.HasFlag(GamepadButtons.X),
        GamepadControl.CreateOrEdit => buttons.HasFlag(GamepadButtons.Y),
        GamepadControl.Settings => buttons.HasFlag(GamepadButtons.Menu),
        GamepadControl.Details => buttons.HasFlag(GamepadButtons.View),
        _ => control == direction
    };

    private static GamepadCommand? SelectButtonCommand(int controller, GamepadButtons pressed, long timestamp)
    {
        foreach (var entry in ButtonPriority)
            if ((pressed & entry.Button) != 0)
                return new(controller, entry.Control, entry.Action, timestamp, false);
        return null;
    }

    private static readonly (GamepadButtons Button, GamepadControl Control, PadAction Action)[] ButtonPriority =
    [
        (GamepadButtons.B, GamepadControl.Close, PadAction.Close),
        (GamepadButtons.A, GamepadControl.Confirm, PadAction.Confirm),
        (GamepadButtons.X, GamepadControl.Secondary, PadAction.Secondary),
        (GamepadButtons.Y, GamepadControl.CreateOrEdit, PadAction.CreateOrEdit),
        (GamepadButtons.Menu, GamepadControl.Settings, PadAction.Settings),
        (GamepadButtons.View, GamepadControl.Details, PadAction.Details)
    ];

    private static GamepadCommand? UpdateDirection(int controller, ControllerState state, GamepadControl direction, long timestamp)
    {
        if (direction == GamepadControl.None)
        {
            state.Direction = GamepadControl.None;
            return null;
        }
        if (direction != state.Direction)
        {
            state.Direction = direction;
            state.NextRepeat = timestamp + InitialRepeatDelay;
            return Command(controller, direction, timestamp, false);
        }
        if (timestamp < state.NextRepeat) return null;
        state.NextRepeat = timestamp + RepeatInterval;
        return Command(controller, direction, timestamp, true);
    }

    private static GamepadCommand Command(int controller, GamepadControl control, long timestamp, bool repeat) =>
        new(controller, control, control switch
        {
            GamepadControl.DPadUp or GamepadControl.LeftStickUp => PadAction.Up,
            GamepadControl.DPadDown or GamepadControl.LeftStickDown => PadAction.Down,
            GamepadControl.DPadLeft or GamepadControl.LeftStickLeft => PadAction.Left,
            GamepadControl.DPadRight or GamepadControl.LeftStickRight => PadAction.Right,
            _ => PadAction.None
        }, timestamp, repeat);

    private static GamepadControl SelectDirection(ControllerState state, GamepadButtons buttons)
    {
        var dpad = SelectDPad(state.Direction, buttons);
        if (dpad != GamepadControl.None) return dpad;

        int horizontal = state.StickRight ? 1 : state.StickLeft ? -1 : 0;
        int vertical = state.StickUp ? 1 : state.StickDown ? -1 : 0;
        if (horizontal == 0 && vertical == 0) return GamepadControl.None;
        if (vertical != 0 && (horizontal == 0 || Math.Abs(state.LeftY) >= Math.Abs(state.LeftX)))
            return vertical > 0 ? GamepadControl.LeftStickUp : GamepadControl.LeftStickDown;
        return horizontal > 0 ? GamepadControl.LeftStickRight : GamepadControl.LeftStickLeft;
    }

    private static GamepadControl SelectDPad(GamepadControl previous, GamepadButtons buttons)
    {
        bool up = buttons.HasFlag(GamepadButtons.DPadUp), down = buttons.HasFlag(GamepadButtons.DPadDown);
        bool left = buttons.HasFlag(GamepadButtons.DPadLeft), right = buttons.HasFlag(GamepadButtons.DPadRight);
        if (up && down) up = down = false;
        if (left && right) left = right = false;
        if (!up && !down && !left && !right) return GamepadControl.None;

        bool previousActive = previous switch
        {
            GamepadControl.DPadUp => up,
            GamepadControl.DPadDown => down,
            GamepadControl.DPadLeft => left,
            GamepadControl.DPadRight => right,
            _ => false
        };
        if (previousActive)
        {
            if (up && previous != GamepadControl.DPadUp) return GamepadControl.DPadUp;
            if (down && previous != GamepadControl.DPadDown) return GamepadControl.DPadDown;
            if (left && previous != GamepadControl.DPadLeft) return GamepadControl.DPadLeft;
            if (right && previous != GamepadControl.DPadRight) return GamepadControl.DPadRight;
            return previous;
        }
        if (up) return GamepadControl.DPadUp;
        if (down) return GamepadControl.DPadDown;
        if (left) return GamepadControl.DPadLeft;
        return GamepadControl.DPadRight;
    }

    private static void UpdateStickState(ControllerState state, short x, short y)
    {
        state.LeftX = x;
        state.LeftY = y;
        state.StickRight = UpdatePositive(state.StickRight, x);
        state.StickLeft = UpdateNegative(state.StickLeft, x);
        state.StickUp = UpdatePositive(state.StickUp, y);
        state.StickDown = UpdateNegative(state.StickDown, y);
    }

    private static bool UpdatePositive(bool active, short value) => active ? value > StickRelease : value >= StickActivation;
    private static bool UpdateNegative(bool active, short value) => active ? value < -StickRelease : value <= -StickActivation;

    private sealed class ControllerState
    {
        public GamepadButtons Buttons;
        public GamepadControl Direction;
        public long NextRepeat;
        public short LeftX, LeftY;
        public bool StickUp, StickDown, StickLeft, StickRight;
    }
}
