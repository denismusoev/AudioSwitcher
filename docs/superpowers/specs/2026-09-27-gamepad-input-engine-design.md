# Gamepad Input Engine

## Goal

Replace the UI-thread gamepad polling and global release gate with a dedicated input engine that turns XInput state changes into reliable commands for the existing AudioSwitcher navigation model.

The engine must minimize missed deliberate presses, prevent accidental repeats after a context change, and preserve predictable behavior during busy operations, window activation changes, controller disconnects, and multi-controller use.

This change does not alter controller button assignments, navigation topology, screen layout, or application business operations.

## Current Failure Mode

The application currently polls XInput every 30 milliseconds with a `DispatcherTimer`. `InputGate` observes only the action selected for that sample and uses a global release barrier after navigation context changes.

This creates four loss paths:

- a release and a new press can both happen between UI-thread samples, so the release barrier rejects the new command;
- UI dispatcher work can delay sampling long enough to miss a short press;
- a command can be accepted by `InputGate` and then discarded by a later `busy` check;
- an inactive or drifting controller can interfere with the controller the user is actually operating.

The previously applied gate correction remains valid, but it addresses only unnecessary release barriers caused by visual context updates. It does not resolve these sampling and ownership problems.

## Selected Approach

Use a dedicated background XInput polling engine. It owns raw controller state, transition detection, direction repeat, suppression, and controller selection. The WPF UI receives discrete `GamepadCommand` instances through the dispatcher and remains responsible only for deciding what a command means in the current navigation context.

Alternatives rejected:

- reducing the existing dispatcher timer interval still leaves sampling coupled to UI load;
- modifying only the global release gate cannot distinguish physical controls or controllers;
- adopting GameInput or Raw Input expands compatibility and deployment risk without being necessary for this application.

## Architecture

```text
XInputGamepadSource (background poll, 8 ms target interval)
                         |
                         v
GamepadInputEngine (per-controller state machines)
                         |
                         v
bounded command delivery / Dispatcher.BeginInvoke
                         |
                         v
MainWindow command routing and navigation
```

### XInputGamepadSource

Responsibilities:

- read all four XInput slots;
- return controller identity, packet number, buttons, triggers, and stick coordinates;
- contain all P/Invoke details;
- perform no WPF work and contain no navigation policy.

The poll loop targets an 8 millisecond interval. It runs on one long-lived background task with cancellation tied to the window lifetime. A delayed iteration does not attempt to execute multiple catch-up polls.

### GamepadInputEngine

Responsibilities:

- maintain independent state for every connected controller;
- convert raw values into physical-control transitions;
- generate non-repeating button commands;
- generate initial and repeating directional commands;
- suppress only the physical control that caused a context change;
- maintain active-controller ownership;
- continue tracking physical state while command delivery is suspended;
- retain bounded diagnostic history.

The engine depends on an input source, monotonic clock, command sink, and cancellation token. The clock and input source are replaceable in tests.

### UI integration

`MainWindow` receives commands on its dispatcher and routes them through the existing action logic. It no longer polls XInput or owns button repeat timing.

Command routing reports one of these outcomes to the engine:

- `Handled`: the command completed without changing input context;
- `ContextChanged`: the command completed and further repeats from its physical source must wait for release;
- `Blocked`: the UI intentionally ignored the command while unavailable;
- `Unhandled`: the command has no meaning in the current context.

Only `ContextChanged` creates suppression, and suppression applies only to the source control carried by that command.

## Data Model

```csharp
internal readonly record struct GamepadCommand(
    int ControllerId,
    GamepadControl Control,
    PadAction Action,
    long Timestamp,
    bool IsRepeat);
```

`GamepadControl` identifies the physical origin, not merely the semantic action. It distinguishes face buttons, menu buttons, D-pad directions, and stick directions.

This distinction is required so that a held `Right` can be suppressed after entering a screen while `Down` or `Close` remains immediately available.

## Transition Rules

### Buttons

- A button-down edge emits one command.
- A held button emits no additional commands.
- A button-up edge rearms that button.
- Two different button controls do not require an intervening all-controller neutral sample.

### D-pad

Each D-pad direction is tracked as a physical button. Opposite directions cancel each other for navigation. If more than one perpendicular direction becomes active in the same sample, the most recently changed direction wins; if both changed together, vertical direction wins to preserve the existing menu-oriented behavior.

### Left stick

The stick uses hysteresis:

- activation threshold: absolute axis value at least 16000;
- release threshold: absolute axis value at most 10000.

Horizontal and vertical candidates are evaluated independently, then one navigation direction is selected. The axis with the larger normalized magnitude wins. A change from `Right` to `Down` emits a new command even when no sampled all-controls-neutral state exists between them.

The right stick and triggers remain unmapped.

### Direction repeat

- initial command: immediately on direction activation;
- initial repeat delay: 400 milliseconds;
- subsequent repeat interval: 140 milliseconds;
- a context-changing direction command suppresses further repeats from that physical direction until it is released;
- switching to a different direction emits a new initial command immediately.

Timing uses a monotonic clock rather than wall-clock time.

## Controller Ownership

The engine maintains one active controller for command delivery.

- A controller becomes active when it produces a valid new edge after having reached a neutral baseline.
- Ownership continues while any of its controls are active and for two seconds after its last accepted edge.
- Another controller may take ownership after the current owner is neutral and its ownership window has expired.
- Disconnecting the owner releases ownership immediately.
- A newly connected controller must first report neutral input. This prevents an already-deflected or drifting stick from taking ownership.

Raw states for all controllers continue to be updated even when they do not own command delivery.

## Focus and Lifecycle

When the window is inactive, the engine continues reading and updating state but does not deliver commands. On activation, only controls already held are suppressed until their individual release. A control pressed after activation is delivered normally without requiring the entire controller to become neutral.

Window shutdown cancels the poll loop and prevents further dispatcher posts. Shutdown does not block waiting indefinitely for XInput.

Disconnect and reconnect reset the affected controller state and repeat schedule. They do not reset other controllers.

## Busy-State Policy

Commands are not queued for later replay during a non-cancellable busy operation.

- The engine continues tracking press and release transitions.
- The UI returns `Blocked` for unavailable actions.
- A blocked command does not suppress unrelated controls and is not replayed when the operation completes.
- A command that remains physically held when busy ends does not fire as a new press.
- A fresh press after release is delivered immediately.
- `Close` remains routable before the general busy guard wherever the existing UI already treats it as safe.

This prevents both silent corruption of the gate state and surprising delayed actions.

## Delivery and Backpressure

The background engine posts only discrete commands, not raw samples, to the WPF dispatcher. At most one dispatcher drain operation may be pending.

The internal queue is bounded. Initial button and direction edges are retained ahead of repeat events. When pressure occurs, stale direction-repeat events are discarded first. Commands are checked against the window lifetime and activation state again when drained.

No WPF object is read or mutated from the polling thread.

## Diagnostics

The engine keeps an in-memory ring buffer containing the latest 256 relevant events. Each entry records:

- monotonic timestamp;
- controller and physical control;
- transition or generated action;
- repeat flag;
- ownership state;
- delivery result such as handled, context changed, blocked, inactive, suppressed, or disconnected.

Routine polling samples are not logged. On an input-related error or explicit diagnostic request, the buffer can be written through the existing diagnostic logger. No device identifiers beyond the XInput slot are stored.

## Error Handling

- An unavailable XInput DLL disables controller input without crashing keyboard or mouse operation.
- Failure reading one slot is treated as that controller being disconnected.
- Exceptions in the polling loop are reported once and stop the loop rather than creating a tight retry cycle.
- Exceptions raised while the UI handles a command use the existing UI task and diagnostic paths; they do not terminate the polling engine.

## Code Organization

Planned production changes:

```text
src/AudioSwitcher/
├── Core/
│   ├── GamepadCommand.cs
│   └── GamepadInputEngine.cs
├── Platform/
│   └── Gamepad.cs
└── MainWindow.xaml.cs
```

`Gamepad.cs` becomes the raw XInput source. The semantic mapping, repeat policy, suppression, ownership, and diagnostics move into `GamepadInputEngine`.

`InputGate` is removed if it has no remaining keyboard-independent consumer after migration. No compatibility wrapper is retained solely for the old polling design.

## Verification Strategy

Use deterministic unit checks with a fake input source and fake monotonic clock for the state engine. Cover:

- button down, hold, release, and second press;
- `Right` followed by `Down` without a sampled global neutral state;
- confirm followed immediately by close after opening an overlay;
- direction repeat at 400 and 140 milliseconds;
- context suppression affecting only the originating control;
- stick activation and release hysteresis;
- perpendicular and opposite direction resolution;
- commands blocked during busy state followed by a valid fresh press;
- inactive-window held controls and a new post-activation press;
- disconnect and reconnect;
- controller ownership and drift from another controller;
- queue pressure discarding repeats before initial edges;
- cancellation and shutdown.

Add focused window checks for command-outcome routing, busy behavior, focus activation, and context-change suppression. Do not require physical controller hardware for automated verification.

Run only the affected unit checks, selected window checks, and a Release build. Perform one manual smoke pass with a physical controller for rapid direction changes, overlay open/back, held-direction transitions, and window focus restoration.

## Acceptance Criteria

The implementation is complete when:

- `Right` followed by `Down` is delivered even without a UI-observed neutral sample;
- confirm followed immediately by close works after an overlay transition;
- buttons fire once per physical press;
- held directions repeat at the defined timing;
- a context change suppresses only the originating held control;
- UI load does not delay XInput sampling because polling is not performed by the dispatcher;
- busy-state commands are neither replayed later nor allowed to corrupt the next fresh press;
- the first new press after window activation is accepted;
- an idle or drifting secondary controller cannot mask the active controller;
- controller disconnect, reconnect, and application shutdown are clean;
- existing keyboard and mouse input behavior is unchanged;
- affected automated checks and the Release build pass.

## Out of Scope

- remappable controller bindings;
- controller-specific UI prompts;
- vibration;
- right-stick or trigger navigation;
- Bluetooth or HID handling outside XInput;
- persisting the active controller between application launches;
- replaying user input after a busy operation.
