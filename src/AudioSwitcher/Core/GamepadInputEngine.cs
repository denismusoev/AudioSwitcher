namespace AudioSwitcher.Core;

public sealed class GamepadInputEngine
{
    private const int StickActivation = 16000;
    private const int StickRelease = 10000;
    private const long InitialRepeatDelay = 400;
    private const long RepeatInterval = 140;
    private const long OwnershipWindow = 2000;
    private const int DiagnosticCapacity = 256;

    private readonly Dictionary<int, ControllerState> controllers = [];
    private readonly HashSet<(int Controller, GamepadControl Control)> suppressed = [];
    private readonly Queue<GamepadDiagnosticEvent> diagnostics = new(DiagnosticCapacity);
    private int? activeController;
    private long ownerUntil;
    private bool engineDeliveryEnabled = true;

    public IReadOnlyList<GamepadCommand> Update(IReadOnlyList<GamepadSnapshot> snapshots, long timestamp, bool deliveryEnabled)
    {
        var candidates = new List<GamepadCommand>(snapshots.Count);
        foreach (var snapshot in snapshots)
            UpdateController(snapshot, timestamp, candidates);

        bool enabled = engineDeliveryEnabled && deliveryEnabled;
        if (!enabled)
        {
            foreach (var command in candidates)
                Record(command, GamepadDiagnosticResult.DeliveryDisabled);
            return [];
        }

        List<GamepadCommand>? commands = null;
        foreach (var candidate in candidates)
        {
            if (!CanDeliverFrom(candidate.ControllerId, timestamp)) continue;
            if (activeController != candidate.ControllerId)
                activeController = candidate.ControllerId;
            if (!candidate.IsRepeat) ownerUntil = timestamp + OwnershipWindow;
            if (IsSuppressed(candidate))
            {
                Record(candidate, GamepadDiagnosticResult.Suppressed);
                continue;
            }
            (commands ??= []).Add(candidate);
            Record(candidate, GamepadDiagnosticResult.Generated);
        }
        return commands ?? [];
    }

    public void SetDeliveryEnabled(bool enabled, long timestamp)
    {
        if (engineDeliveryEnabled == enabled) return;
        engineDeliveryEnabled = enabled;
        if (!enabled) return;

        foreach (var pair in controllers)
        {
            foreach (var control in ActiveButtonControls(pair.Value.Buttons))
                suppressed.Add((pair.Key, control));
            if (pair.Value.Direction != GamepadControl.None)
                suppressed.Add((pair.Key, pair.Value.Direction));
        }
    }

    public void Report(GamepadCommand command, GamepadCommandResult result)
    {
        if (result == GamepadCommandResult.ContextChanged)
            suppressed.Add((command.ControllerId, command.Control));
        Record(command, result switch
        {
            GamepadCommandResult.Handled => GamepadDiagnosticResult.Handled,
            GamepadCommandResult.ContextChanged => GamepadDiagnosticResult.ContextChanged,
            GamepadCommandResult.Blocked => GamepadDiagnosticResult.Blocked,
            _ => GamepadDiagnosticResult.Unhandled
        });
    }

    public IReadOnlyList<GamepadDiagnosticEvent> GetDiagnostics() => diagnostics.ToArray();

    private void UpdateController(GamepadSnapshot snapshot, long timestamp, List<GamepadCommand> candidates)
    {
        if (!snapshot.Connected)
        {
            if (controllers.Remove(snapshot.ControllerId))
                AddDiagnostic(new(timestamp, snapshot.ControllerId, GamepadControl.None, PadAction.None, false, GamepadDiagnosticResult.Disconnected));
            suppressed.RemoveWhere(item => item.Controller == snapshot.ControllerId);
            if (activeController == snapshot.ControllerId) activeController = null;
            return;
        }

        bool created;
        if (!controllers.TryGetValue(snapshot.ControllerId, out var state))
        {
            created = true;
            controllers[snapshot.ControllerId] = state = new ControllerState();
        }
        else created = false;

        var previousButtons = state.Buttons;
        var previousDirection = state.Direction;
        UpdateStickState(state, snapshot.LeftX, snapshot.LeftY);
        var direction = SelectDirection(state, snapshot.Buttons);
        var pressed = snapshot.Buttons & ~previousButtons;
        state.Buttons = snapshot.Buttons;
        UpdateDirectionState(snapshot.ControllerId, state, direction, timestamp, candidates);
        ClearReleasedSuppressions(snapshot.ControllerId, snapshot.Buttons, direction);

        bool neutral = IsNeutral(snapshot.Buttons, direction);
        if (created)
        {
            state.Baselined = neutral;
            state.Direction = direction;
            candidates.RemoveAll(command => command.ControllerId == snapshot.ControllerId);
            if (neutral) AddDiagnostic(new(timestamp, snapshot.ControllerId, GamepadControl.None, PadAction.None, false, GamepadDiagnosticResult.NeutralBaseline));
            return;
        }
        if (!state.Baselined)
        {
            if (neutral)
            {
                state.Baselined = true;
                AddDiagnostic(new(timestamp, snapshot.ControllerId, GamepadControl.None, PadAction.None, false, GamepadDiagnosticResult.NeutralBaseline));
            }
            candidates.RemoveAll(command => command.ControllerId == snapshot.ControllerId);
            return;
        }

        var buttonCommand = SelectButtonCommand(snapshot.ControllerId, pressed, timestamp);
        if (buttonCommand is GamepadCommand button)
        {
            candidates.RemoveAll(command => command.ControllerId == snapshot.ControllerId && IsDirection(command.Control));
            candidates.Add(button);
        }
        else if ((snapshot.Buttons & MappedButtons) != 0)
            candidates.RemoveAll(command => command.ControllerId == snapshot.ControllerId && IsDirection(command.Control));

        if (previousButtons != snapshot.Buttons || previousDirection != direction)
            AddDiagnostic(new(timestamp, snapshot.ControllerId, direction, DirectionAction(direction), false, enabledResult()));

        GamepadDiagnosticResult enabledResult() => engineDeliveryEnabled ? GamepadDiagnosticResult.Connected : GamepadDiagnosticResult.DeliveryDisabled;
    }

    private bool CanDeliverFrom(int controller, long timestamp)
    {
        if (activeController is null || activeController == controller) return true;
        if (!controllers.TryGetValue(activeController.Value, out var owner))
        {
            activeController = null;
            return true;
        }
        if (!IsNeutral(owner.Buttons, owner.Direction) || timestamp < ownerUntil) return false;
        activeController = null;
        return true;
    }

    private static bool IsNeutral(GamepadButtons buttons, GamepadControl direction) =>
        (buttons & (MappedButtons | DPadButtons)) == 0 && direction == GamepadControl.None;

    private void UpdateDirectionState(int controller, ControllerState state, GamepadControl direction, long timestamp, List<GamepadCommand> candidates)
    {
        if (direction == GamepadControl.None)
        {
            state.Direction = GamepadControl.None;
            return;
        }
        if (direction != state.Direction)
        {
            state.Direction = direction;
            state.NextRepeat = timestamp + InitialRepeatDelay;
            candidates.Add(Command(controller, direction, timestamp, false));
            return;
        }
        if (timestamp < state.NextRepeat) return;
        state.NextRepeat = timestamp + RepeatInterval;
        candidates.Add(Command(controller, direction, timestamp, true));
    }

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

    private static IEnumerable<GamepadControl> ActiveButtonControls(GamepadButtons buttons)
    {
        foreach (var entry in ButtonPriority)
            if ((buttons & entry.Button) != 0) yield return entry.Control;
    }

    private static GamepadCommand? SelectButtonCommand(int controller, GamepadButtons pressed, long timestamp)
    {
        foreach (var entry in ButtonPriority)
            if ((pressed & entry.Button) != 0)
                return new(controller, entry.Control, entry.Action, timestamp, false);
        return null;
    }

    private static readonly GamepadButtons MappedButtons = GamepadButtons.A | GamepadButtons.B | GamepadButtons.X |
        GamepadButtons.Y | GamepadButtons.Menu | GamepadButtons.View;
    private static readonly GamepadButtons DPadButtons = GamepadButtons.DPadUp | GamepadButtons.DPadDown |
        GamepadButtons.DPadLeft | GamepadButtons.DPadRight;
    private static readonly (GamepadButtons Button, GamepadControl Control, PadAction Action)[] ButtonPriority =
    [
        (GamepadButtons.B, GamepadControl.Close, PadAction.Close),
        (GamepadButtons.A, GamepadControl.Confirm, PadAction.Confirm),
        (GamepadButtons.X, GamepadControl.Secondary, PadAction.Secondary),
        (GamepadButtons.Y, GamepadControl.CreateOrEdit, PadAction.CreateOrEdit),
        (GamepadButtons.Menu, GamepadControl.Settings, PadAction.Settings),
        (GamepadButtons.View, GamepadControl.Details, PadAction.Details)
    ];

    private static bool IsDirection(GamepadControl control) => control is >= GamepadControl.DPadUp and <= GamepadControl.LeftStickRight;

    private static GamepadCommand Command(int controller, GamepadControl control, long timestamp, bool repeat) =>
        new(controller, control, DirectionAction(control), timestamp, repeat);

    private static PadAction DirectionAction(GamepadControl control) => control switch
    {
        GamepadControl.DPadUp or GamepadControl.LeftStickUp => PadAction.Up,
        GamepadControl.DPadDown or GamepadControl.LeftStickDown => PadAction.Down,
        GamepadControl.DPadLeft or GamepadControl.LeftStickLeft => PadAction.Left,
        GamepadControl.DPadRight or GamepadControl.LeftStickRight => PadAction.Right,
        _ => PadAction.None
    };

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

    private void Record(GamepadCommand command, GamepadDiagnosticResult result) =>
        AddDiagnostic(new(command.Timestamp, command.ControllerId, command.Control, command.Action, command.IsRepeat, result));

    private void AddDiagnostic(GamepadDiagnosticEvent entry)
    {
        if (diagnostics.Count == DiagnosticCapacity) diagnostics.Dequeue();
        diagnostics.Enqueue(entry);
    }

    private sealed class ControllerState
    {
        public GamepadButtons Buttons;
        public GamepadControl Direction;
        public long NextRepeat;
        public short LeftX, LeftY;
        public bool Baselined;
        public bool StickUp, StickDown, StickLeft, StickRight;
    }
}
