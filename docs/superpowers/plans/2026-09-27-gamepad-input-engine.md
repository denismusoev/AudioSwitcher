# Gamepad Input Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace dispatcher-timer XInput polling with a dedicated input engine that reliably emits discrete controller commands without global release barriers.

**Architecture:** A pure, deterministic `GamepadInputEngine` converts per-controller snapshots into commands and owns transition, repeat, suppression, ownership, focus, and diagnostics state. A cancellable `GamepadInputService` polls `Gamepad` every 8 ms on a background task, retains a bounded command queue, and schedules one UI drain at a time; `MainWindow` handles commands and reports whether a context change requires source-specific suppression.

**Tech Stack:** C# 14, .NET 10, WPF, XInput 1.4, existing console check projects.

**Spec:** `docs/superpowers/specs/2026-09-27-gamepad-input-engine-design.md`

## Global Constraints

- Keep the existing controller mappings, navigation topology, keyboard behavior, mouse behavior, and UI layout unchanged.
- Poll XInput outside the WPF dispatcher at an 8 ms target interval; do not perform catch-up polls.
- Use 16000 as the stick activation threshold and 10000 as the release threshold.
- Repeat directions after 400 ms, then every 140 ms; never repeat face or menu buttons.
- Suppress only the physical control that caused a context change.
- Do not replay commands received while the UI is busy or inactive.
- Require a neutral baseline from a newly connected controller before it can take ownership.
- Retain at most 256 diagnostic events and avoid logging routine raw samples.
- Add no external package or runtime dependency.
- Run only affected checks and a Release build; do not run the full test suite.

## Review Focus

- A stick moving directly from horizontal to vertical without a neutral sample must emit the new dominant direction; Task 1 adds this test.
- Simultaneous button presses must preserve the existing priority (`Close`, `Confirm`, `Secondary`, `CreateOrEdit`, `Settings`, `Details`, directions); Task 1 adds this test.
- A controller reconnecting with a held or drifting control must not acquire ownership until neutral; Task 2 adds this test.
- Queue saturation must discard stale repeats before initial edges, especially `Close`; Task 3 adds this test.
- A direction held across `busy` or window deactivation must not fire when delivery resumes, while a fresh post-release press must fire; Tasks 2 and 4 add these tests.

---

### Task 1: Deterministic Single-Controller State Engine

**Files:**
- Create: `src/AudioSwitcher/Core/GamepadInputModels.cs`
- Create: `src/AudioSwitcher/Core/GamepadInputEngine.cs`
- Modify: `tests/AudioSwitcher.Checks/Program.cs`

**Interfaces:**
- Consumes: raw button flags, stick coordinates, controller slot, XInput packet number, and monotonic milliseconds.
- Produces: `GamepadSnapshot`, `GamepadControl`, `GamepadCommand`, `GamepadCommandResult`, and `GamepadInputEngine.Update(IReadOnlyList<GamepadSnapshot> snapshots, long timestamp, bool deliveryEnabled) -> IReadOnlyList<GamepadCommand>`.
- Produces: `GamepadInputEngine.Report(GamepadCommand command, GamepadCommandResult result)` for source-specific suppression.

- [ ] **Step 1: Write failing state-engine checks**

Add checks named `Button edges fire once`, `Directions repeat at 400 and 140 milliseconds`, `A changed direction does not require global neutral`, `Stick hysteresis rejects boundary noise`, `Context suppression affects only its source`, and `Simultaneous controls retain action priority`. Assert the exact timings, thresholds, physical control identities, and semantic actions from the spec.

- [ ] **Step 2: Run the affected check project and verify red**

Run: `dotnet run --project tests/AudioSwitcher.Checks/AudioSwitcher.Checks.csproj -c Release`

Expected: build or assertion failure because the new models and engine do not exist.

- [ ] **Step 3: Add the input models**

Move `PadAction` from `InputGate.cs` into `GamepadInputModels.cs`, preserving its existing values and aliases so navigation code remains source-compatible. Define the remaining exact types there: `[Flags] GamepadButtons`, `GamepadControl`, `GamepadSnapshot(int ControllerId, bool Connected, uint Packet, GamepadButtons Buttons, short LeftX, short LeftY)`, `GamepadCommand(int ControllerId, GamepadControl Control, PadAction Action, long Timestamp, bool IsRepeat)`, and `GamepadCommandResult { Handled, ContextChanged, Blocked, Unhandled }`.

- [ ] **Step 4: Implement the single-controller transition engine**

Implement `Update` and `Report` in `GamepadInputEngine`. Track button edges and a single selected navigation direction, use the specified stick hysteresis and dominant-axis rule, emit only the highest-priority initial action in a sample, and implement 400/140 ms direction repeat. `Report(..., ContextChanged)` suppresses that command's physical source until its release.

- [ ] **Step 5: Run the affected checks and verify green**

Run: `dotnet run --project tests/AudioSwitcher.Checks/AudioSwitcher.Checks.csproj -c Release`

Expected: all checks pass.

- [ ] **Step 6: Commit the state engine**

```powershell
git add src/AudioSwitcher/Core/GamepadInputModels.cs src/AudioSwitcher/Core/GamepadInputEngine.cs tests/AudioSwitcher.Checks/Program.cs
git commit -m "feat: add deterministic gamepad state engine"
```

### Task 2: Controller Ownership, Activation, and Diagnostics

**Files:**
- Modify: `src/AudioSwitcher/Core/GamepadInputEngine.cs`
- Modify: `src/AudioSwitcher/Core/GamepadInputModels.cs`
- Modify: `tests/AudioSwitcher.Checks/Program.cs`

**Interfaces:**
- Consumes: the Task 1 engine and models.
- Produces: multi-controller ownership inside `Update`, `GamepadInputEngine.SetDeliveryEnabled(bool enabled, long timestamp)`, and `GamepadInputEngine.GetDiagnostics() -> IReadOnlyList<GamepadDiagnosticEvent>`.

- [ ] **Step 1: Write failing ownership and lifecycle checks**

Add checks named `Only a neutral-baselined controller can acquire ownership`, `Owner disconnect releases ownership`, `Secondary drift cannot mask the active controller`, `Activation suppresses only controls already held`, `Busy-held direction does not fire after resume`, and `Diagnostics retain the newest 256 relevant events`.

- [ ] **Step 2: Run checks and verify red**

Run: `dotnet run --project tests/AudioSwitcher.Checks/AudioSwitcher.Checks.csproj -c Release`

Expected: the new ownership, activation, and diagnostic assertions fail.

- [ ] **Step 3: Implement controller ownership and delivery lifecycle**

Maintain independent controller states, neutral-baseline eligibility, one active owner, and a two-second neutral ownership window. `SetDeliveryEnabled(false, timestamp)` stops emission but continues state updates; reenabling suppresses only controls already held. Disconnect resets only that controller and immediately releases ownership when it was the owner.

- [ ] **Step 4: Implement bounded in-memory diagnostics**

Define `GamepadDiagnosticEvent` with timestamp, controller, control, action, repeat flag, and result. Record transitions, generated commands, suppression, ownership, activation, and disconnect events in a 256-entry ring without recording ordinary unchanged polls.

- [ ] **Step 5: Run checks and verify green**

Run: `dotnet run --project tests/AudioSwitcher.Checks/AudioSwitcher.Checks.csproj -c Release`

Expected: all checks pass.

- [ ] **Step 6: Commit ownership and diagnostics**

```powershell
git add src/AudioSwitcher/Core/GamepadInputModels.cs src/AudioSwitcher/Core/GamepadInputEngine.cs tests/AudioSwitcher.Checks/Program.cs
git commit -m "feat: handle gamepad ownership and lifecycle"
```

### Task 3: Background XInput Service and Bounded Delivery

**Files:**
- Create: `src/AudioSwitcher/Core/GamepadInputService.cs`
- Modify: `src/AudioSwitcher/Platform/Gamepad.cs`
- Modify: `src/AudioSwitcher/App.xaml.cs`
- Modify: `tests/AudioSwitcher.Checks/Program.cs`

**Interfaces:**
- Consumes: Task 2 `GamepadInputEngine`, `GamepadSnapshot`, and `GamepadCommandResult`.
- Produces: `IGamepadSource.Read(Span<GamepadSnapshot> destination) -> int`, implemented by `Gamepad`.
- Produces: `GamepadInputService(IGamepadSource source, GamepadInputEngine engine, Action<Action> scheduleDrain, Func<GamepadCommand, GamepadCommandResult> handler, TimeProvider? timeProvider = null)` with `Start(CancellationToken)`, `SetDeliveryEnabled(bool)`, and `DisposeAsync()`.

- [ ] **Step 1: Write failing service checks**

Using a fake source, fake time provider, and captured scheduler, add checks named `Polling schedules one drain`, `Queue keeps initial edges ahead of repeats`, `Close survives saturated repeat queue`, `Cancellation stops polling`, and `A delayed poll does not catch up in a burst`.

- [ ] **Step 2: Run checks and verify red**

Run: `dotnet run --project tests/AudioSwitcher.Checks/AudioSwitcher.Checks.csproj -c Release`

Expected: build or assertions fail because the service and source contract do not exist.

- [ ] **Step 3: Convert Gamepad into the raw XInput source**

Convert `Gamepad` into an `IGamepadSource` implementation. Read all four fixed XInput slots into snapshots without applying semantic navigation priority. Preserve a small static mapping helper only where existing checks still need to verify button constants, expose a one-shot connection probe for startup diagnostics, and update `App.xaml.cs` to use that probe.

- [ ] **Step 4: Implement the cancellable polling service**

Poll every 8 ms on one background task. Feed snapshots to the engine, enqueue discrete commands in a bounded queue of 32 entries, schedule at most one drain, and discard the oldest repeat before dropping an initial edge. The UI handler's result is reported back to the engine. Catch `DllNotFoundException` and `EntryPointNotFoundException` once, terminate controller polling, and leave other input paths operational.

- [ ] **Step 5: Run checks and verify green**

Run: `dotnet run --project tests/AudioSwitcher.Checks/AudioSwitcher.Checks.csproj -c Release`

Expected: all checks pass.

- [ ] **Step 6: Commit the background service**

```powershell
git add src/AudioSwitcher/Core/GamepadInputService.cs src/AudioSwitcher/Platform/Gamepad.cs src/AudioSwitcher/App.xaml.cs tests/AudioSwitcher.Checks/Program.cs
git commit -m "feat: poll XInput outside the UI thread"
```

### Task 4: Main Window Integration and Global Gate Removal

**Files:**
- Modify: `src/AudioSwitcher/MainWindow.xaml.cs`
- Delete: `src/AudioSwitcher/Core/InputGate.cs`
- Modify: `tests/AudioSwitcher.Checks/Program.cs`
- Modify: `tests/AudioSwitcher.WindowChecks/ApprovedDesignChecks.cs`
- Modify: `tests/AudioSwitcher.WindowChecks/PageLayoutChecks.cs`
- Modify: `tests/AudioSwitcher.WindowChecks/Program.cs`
- Modify: `tests/AudioSwitcher.WindowChecks/SelectedFixChecks.cs`

**Interfaces:**
- Consumes: Task 3 `GamepadInputService` and `GamepadCommand`.
- Produces: `MainWindow.HandleGamepadCommand(GamepadCommand command) -> GamepadCommandResult` and context-aware command routing.

- [ ] **Step 1: Replace obsolete gate checks with failing integration checks**

Remove the old `InputGate` behavior checks. Add selected window checks named `Context-changing direction suppresses only its source`, `Close remains available after overlay entry`, `Busy blocks actions without replay`, `Window reactivation accepts a fresh press`, and `Main window no longer owns a gamepad dispatcher timer`.

- [ ] **Step 2: Run focused checks and verify red**

Run: `dotnet run --project tests/AudioSwitcher.WindowChecks/AudioSwitcher.WindowChecks.csproj -c Release -- --selected-fixes`

Expected: new integration assertions fail against timer polling and the global gate.

- [ ] **Step 3: Replace timer polling with the service lifecycle**

Construct the input service after WPF initialization, pass `Dispatcher.BeginInvoke` at input priority as the scheduler, start it from `Loaded`, update delivery state from `Activated` and `Deactivated`, and dispose it during `Closed`. Remove `padTimer`, `padArmed`, `PollPad`, and every `gate.RequireRelease()` call.

- [ ] **Step 4: Return explicit routing outcomes**

Change controller execution to `HandleGamepadCommand(GamepadCommand)`. Return `Blocked` for non-close commands rejected by `busy` or `Programs.IsBusy`, `ContextChanged` for synchronous section, overlay, or back transitions, `Handled` for completed in-context actions, and `Unhandled` where no action applies. Preserve current action priority and UI methods.

- [ ] **Step 5: Delete InputGate and update static expectations**

Remove `InputGate.cs`, its old console checks, and reflection that expects `padTimer` or `gate` across all window-check entry points. Where a window check previously stopped `padTimer`, disable the injected service through its reflected field instead. Keep keyboard and mouse paths unchanged.

- [ ] **Step 6: Run focused core and window checks**

Run: `dotnet run --project tests/AudioSwitcher.Checks/AudioSwitcher.Checks.csproj -c Release`

Run: `dotnet run --project tests/AudioSwitcher.WindowChecks/AudioSwitcher.WindowChecks.csproj -c Release -- --selected-fixes`

Expected: both commands pass with zero failed checks.

- [ ] **Step 7: Commit UI integration**

```powershell
git add src/AudioSwitcher/MainWindow.xaml.cs src/AudioSwitcher/Core/InputGate.cs tests/AudioSwitcher.Checks/Program.cs tests/AudioSwitcher.WindowChecks/ApprovedDesignChecks.cs tests/AudioSwitcher.WindowChecks/PageLayoutChecks.cs tests/AudioSwitcher.WindowChecks/Program.cs tests/AudioSwitcher.WindowChecks/SelectedFixChecks.cs
git commit -m "feat: route UI navigation through gamepad engine"
```

### Task 5: Focused Regression Verification and Release Artifact

**Files:**
- Modify only if a focused check exposes a defect in the changed input path.

**Interfaces:**
- Consumes: the completed input engine, service, and window integration.
- Produces: verified Release executable and a concise list of any hardware-only checks still requiring user confirmation.

- [ ] **Step 1: Run the affected static design checks**

Run: `dotnet run --project tests/AudioSwitcher.WindowChecks/AudioSwitcher.WindowChecks.csproj -c Release -- --fixed-design`

Expected: exit code 0.

- [ ] **Step 2: Build the Release application**

Run: `dotnet build src/AudioSwitcher/AudioSwitcher.csproj -c Release`

Expected: build succeeds with 0 warnings and 0 errors; executable exists at `src/AudioSwitcher/bin/Release/net10.0-windows/AudioSwitcher.exe`.

- [ ] **Step 3: Inspect the final diff and input-specific status**

Run: `git diff --check HEAD~4..HEAD`

Run: `git status --short`

Expected: no whitespace errors and no unintended files.

- [ ] **Step 4: Perform or hand off the physical-controller smoke pass**

Verify rapid `Right -> Down`, `Confirm -> Back`, held direction across a screen transition, busy-operation release/fresh press, focus loss/restoration, disconnect/reconnect, and a second controller if available. Automated checks remain authoritative for deterministic state transitions; explicitly report any unavailable hardware scenario rather than claiming it passed.

- [ ] **Step 5: Commit any verification-only correction, if one was required**

Create no empty commit. If a focused regression required a correction, stage only that correction and its test, then commit with a message describing the specific defect.
