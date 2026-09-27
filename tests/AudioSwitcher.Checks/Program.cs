using AudioSwitcher.Core;
using AudioSwitcher.Platform;

int passed = 0, failed = 0;
void Check(string name, Action test) { try { test(); Console.WriteLine($"PASS {name}"); passed++; } catch (Exception e) { Console.WriteLine($"FAIL {name}: {e.Message}"); failed++; } }
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
Check("Refresh failures use bounded backoff and success resets it", () => {
    var backoff = new RefreshBackoff();
    Equal(TimeSpan.FromSeconds(3), backoff.CurrentDelay);
    foreach (int seconds in new[] { 6, 12, 24, 30, 30 })
    {
        backoff.RecordFailure();
        Equal(TimeSpan.FromSeconds(seconds), backoff.CurrentDelay);
    }
    backoff.RecordSuccess();
    Equal(TimeSpan.FromSeconds(3), backoff.CurrentDelay);
});
Check("New primary moves to origin and preserves spacing", () => {
    var result = DisplayLayout.Rebase([new("a", 0, 0), new("b", -1920, 240), new("c", 2560, -100)], "b");
    Equal(new DisplayPosition("b", 0, 0), result[1]);
    Equal(new DisplayPosition("a", 1920, -240), result[0]);
    Equal(new DisplayPosition("c", 4480, -340), result[2]);
});
Check("Missing display rejects operation", () => {
    try { DisplayLayout.Rebase([new("a", 0, 0)], "missing"); } catch (ArgumentException) { return; }
    throw new Exception("Missing display was accepted");
});
Check("Confirm fires once until release", () => {
    var gate = new InputGate();
    Equal(true, gate.Accept(PadAction.Confirm, 0));
    Equal(false, gate.Accept(PadAction.Confirm, 1000));
    gate.Accept(PadAction.None, 1010);
    Equal(true, gate.Accept(PadAction.Confirm, 1020));
});
Check("Navigation repeats after initial delay", () => {
    var gate = new InputGate();
    Equal(true, gate.Accept(PadAction.Down, 0));
    Equal(false, gate.Accept(PadAction.Down, 200));
    Equal(true, gate.Accept(PadAction.Down, 400));
    Equal(false, gate.Accept(PadAction.Down, 450));
    Equal(true, gate.Accept(PadAction.Down, 540));
});
Check("Only user application windows are included, including minimized and unnamed", () => {
    var facts = new ProgramWindowFacts(true, false, false, false, false, true, false, "Game", "C:\\Games\\game.exe");
    Equal(true, ProgramFilter.Include(facts));
    Equal(false, ProgramFilter.Include(facts with { Visible = false }));
    Equal(false, ProgramFilter.Include(facts with { Cloaked = true }));
    Equal(false, ProgramFilter.Include(facts with { ToolWindow = true }));
    Equal(false, ProgramFilter.Include(facts with { HasOwner = true }));
    Equal(true, ProgramFilter.Include(facts with { HasOwner = true, AppWindow = true }));
    Equal(false, ProgramFilter.Include(facts with { SameUserSession = false }));
    Equal(false, ProgramFilter.Include(facts with { Self = true }));
});
Check("Explorer document windows remain but desktop and Windows shell hosts are excluded", () => {
    var facts = new ProgramWindowFacts(true, false, false, false, false, true, false, "CabinetWClass", "C:\\Windows\\explorer.exe");
    Equal(true, ProgramFilter.Include(facts));
    Equal(false, ProgramFilter.Include(facts with { ClassName = "Shell_TrayWnd" }));
    Equal(false, ProgramFilter.Include(facts with { ClassName = "Progman" }));
    Equal(false, ProgramFilter.Include(facts with { ImagePath = "C:\\Windows\\SystemApps\\Microsoft.Windows.StartMenuExperienceHost_x\\StartMenuExperienceHost.exe", ClassName = "Windows.UI.Core.CoreWindow" }));
    Equal(true, ProgramFilter.Include(facts with { ImagePath = "C:\\Games\\SearchHost.exe", ClassName = "Game" }));
});
Check("Window layout centers in actual work area and supports negative coordinates", () => {
    Equal(new WindowBounds(-1500, 340, 1000, 600), ProgramWindowLayout.Calculate(new(50, 50, 1000, 600), new(-2000, 40, 2000, 1200), 96, 96, false, new(-2000, 0, 2000, 1240)));
});
Check("Window layout scales for target DPI and clamps oversized windows", () => {
    Equal(new WindowBounds(210, 100, 1500, 900), ProgramWindowLayout.Calculate(new(-1920, 0, 1000, 600), new(0, 40, 1920, 1020), 96, 144, false, new(0, 0, 1920, 1080)));
    Equal(new WindowBounds(0, 40, 1920, 1020), ProgramWindowLayout.Calculate(new(0, 0, 4000, 2000), new(0, 40, 1920, 1020), 96, 96, false, new(0, 0, 1920, 1080)));
});
Check("Borderless full monitor window uses whole TV bounds", () => {
    Equal(new WindowBounds(0, 0, 3840, 2160), ProgramWindowLayout.Calculate(new(-1920, 0, 1920, 1080), new(0, 0, 3840, 2120), 96, 144, true, new(0, 0, 3840, 2160)));
});
Check("Window placement accepts a mostly contained frame and rejects a sliver", () => {
    var area = new WindowBounds(0, 0, 1000, 800);
    if (!ProgramWindowLayout.IsSufficientlyOnScreen(new(700, 100, 500, 500), area, 3)) throw new Exception("Mostly visible frame was rejected");
    if (ProgramWindowLayout.IsSufficientlyOnScreen(new(980, 100, 500, 500), area, 3)) throw new Exception("Mostly off-screen frame was accepted");
});
Check("Program panels cancel before closing the application", () => {
    var navigation = new NavigationState { Section = AppSection.Running, Panel = ProgramPanel.CloseWindows };
    Equal(true, navigation.Back()); Equal(ProgramPanel.List, navigation.Panel);
    Equal(false, navigation.Back());
    navigation.Panel = ProgramPanel.Windows;
    Equal(false, navigation.ChangeSection(-1)); Equal(AppSection.Running, navigation.Section);
    Equal(true, navigation.Back()); Equal(ProgramPanel.List, navigation.Panel);
});
Check("A single program window closes directly while multiple windows require a picker", () => {
    var first = new WindowTarget(new(10, 100), (nint)1, "First", "Экран 1", false);
    var second = new WindowTarget(new(10, 100), (nint)2, "Second", "Экран 2", false);
    Equal(first, ProgramActionPolicy.DirectCloseTarget(new(first.Process, "Game", [first])));
    Equal<WindowTarget?>(null, ProgramActionPolicy.DirectCloseTarget(new(first.Process, "Game", [first, second])));
});
Check("Only manual launch entries can be edited", () =>
{
    var manualEntry = new LaunchEntry(Guid.NewGuid(), "Manual", LaunchKind.Executable, @"C:\manual.exe");
    var gameEntry = manualEntry with { Source = LaunchEntrySource.GameManifest, SourceKey = @"E:\Games\Game" };
    Equal(true, ProgramActionPolicy.CanEditLaunchEntry(manualEntry));
    Equal(false, ProgramActionPolicy.CanEditLaunchEntry(gameEntry));
});
Check("Up and down wrap through the three main sections", () => {
    var navigation = new NavigationState();
    Equal(NavigationTransition.SectionChanged, navigation.Navigate(PadAction.Down)); Equal(AppSection.Running, navigation.Section);
    Equal(NavigationTransition.SectionChanged, navigation.Navigate(PadAction.Down)); Equal(AppSection.Launch, navigation.Section);
    Equal(NavigationTransition.SectionChanged, navigation.Navigate(PadAction.Down)); Equal(AppSection.Control, navigation.Section);
    Equal(NavigationTransition.SectionChanged, navigation.Navigate(PadAction.Up)); Equal(AppSection.Launch, navigation.Section);
    navigation.Panel = ProgramPanel.Actions; Equal(NavigationTransition.None, navigation.Navigate(PadAction.Up));
});
Check("Right or confirm enters a section and left or close returns", () => {
    var navigation = new NavigationState();
    Equal(NavigationTransition.EnteredSection, navigation.Navigate(PadAction.Confirm));
    Equal(NavigationTransition.None, navigation.Navigate(PadAction.Down));
    Equal(AppSection.Control, navigation.Section);
    Equal(NavigationTransition.LeftSection, navigation.Navigate(PadAction.Close));
    Equal(NavigationTransition.None, navigation.Navigate(PadAction.Left));
    Equal(NavigationTransition.EnteredSection, navigation.Navigate(PadAction.Right));
    Equal(NavigationTransition.LeftSection, navigation.Navigate(PadAction.Left));
});
Check("Shoulder buttons have no navigation action", () => {
    Equal(PadAction.None, Gamepad.Map(new Gamepad.State { Buttons = 0x0100 }));
    Equal(PadAction.None, Gamepad.Map(new Gamepad.State { Buttons = 0x0200 }));
});
Check("Confirmation is rearmed only after all gamepad controls are released", () => {
    var gate = new InputGate();
    Equal(true, gate.Accept(PadAction.Confirm, 0));
    gate.RequireRelease();
    Equal(false, gate.Accept(PadAction.Confirm, 1000));
    Equal(false, gate.Accept(PadAction.Down, 1200));
    Equal(false, gate.Accept(PadAction.None, 1300));
    Equal(true, gate.Accept(PadAction.Confirm, 1400));
    Equal(false, gate.Accept(PadAction.Confirm, 2000));
});
Check("A release barrier does not swallow a new action after neutral input", () => {
    var gate = new InputGate();
    Equal(false, gate.Accept(PadAction.None, 0));
    gate.RequireRelease();
    Equal(true, gate.Accept(PadAction.Close, 10));
});
Check("TV shortcut actions do not repeat while held", () => {
    var gate = new InputGate();
    Equal(true, gate.Accept(PadAction.Secondary, 0));
    Equal(false, gate.Accept(PadAction.Secondary, 900));
    gate.Accept(PadAction.None, 910);
    Equal(true, gate.Accept(PadAction.Settings, 920));
    Equal(false, gate.Accept(PadAction.Settings, 1500));
});
Check("Button edges fire once", () => {
    var engine = new GamepadInputEngine();
    var pressed = new GamepadSnapshot(0, true, 1, GamepadButtons.A, 0, 0);
    engine.Update([pressed with { Packet = 0, Buttons = GamepadButtons.None }], -1, true);
    var first = engine.Update([pressed], 0, true);
    Equal(1, first.Count); Equal(PadAction.Confirm, first[0].Action); Equal(GamepadControl.Confirm, first[0].Control);
    Equal(0, engine.Update([pressed with { Packet = 2 }], 500, true).Count);
    Equal(0, engine.Update([pressed with { Packet = 3, Buttons = GamepadButtons.None }], 510, true).Count);
    Equal(1, engine.Update([pressed with { Packet = 4 }], 520, true).Count);
});
Check("Directions repeat at 400 and 140 milliseconds", () => {
    var engine = new GamepadInputEngine();
    var down = new GamepadSnapshot(0, true, 1, GamepadButtons.DPadDown, 0, 0);
    engine.Update([down with { Packet = 0, Buttons = GamepadButtons.None }], -1, true);
    var initial = engine.Update([down], 0, true);
    Equal(1, initial.Count); Equal(false, initial[0].IsRepeat); Equal(PadAction.Down, initial[0].Action);
    Equal(0, engine.Update([down with { Packet = 2 }], 399, true).Count);
    var firstRepeat = engine.Update([down with { Packet = 3 }], 400, true);
    Equal(1, firstRepeat.Count); Equal(true, firstRepeat[0].IsRepeat);
    Equal(0, engine.Update([down with { Packet = 4 }], 539, true).Count);
    Equal(1, engine.Update([down with { Packet = 5 }], 540, true).Count);
});
Check("A changed direction does not require global neutral", () => {
    var engine = new GamepadInputEngine();
    var right = new GamepadSnapshot(0, true, 1, GamepadButtons.DPadRight, 0, 0);
    engine.Update([right with { Packet = 0, Buttons = GamepadButtons.None }], -1, true);
    Equal(PadAction.Right, engine.Update([right], 0, true).Single().Action);
    var changed = engine.Update([right with { Packet = 2, Buttons = GamepadButtons.DPadDown }], 10, true);
    Equal(1, changed.Count); Equal(PadAction.Down, changed[0].Action);
});
Check("Stick hysteresis rejects boundary noise", () => {
    var engine = new GamepadInputEngine();
    var state = new GamepadSnapshot(0, true, 1, GamepadButtons.None, 15999, 0);
    Equal(0, engine.Update([state], 0, true).Count);
    Equal(PadAction.Right, engine.Update([state with { Packet = 2, LeftX = 16000 }], 10, true).Single().Action);
    Equal(0, engine.Update([state with { Packet = 3, LeftX = 12000 }], 20, true).Count);
    Equal(0, engine.Update([state with { Packet = 4, LeftX = 10000 }], 30, true).Count);
    Equal(PadAction.Right, engine.Update([state with { Packet = 5, LeftX = 16000 }], 40, true).Single().Action);
});
Check("Context suppression affects only its source", () => {
    var engine = new GamepadInputEngine();
    var confirmState = new GamepadSnapshot(0, true, 1, GamepadButtons.A, 0, 0);
    engine.Update([confirmState with { Packet = 0, Buttons = GamepadButtons.None }], -1, true);
    var confirm = engine.Update([confirmState], 0, true).Single();
    engine.Report(confirm, GamepadCommandResult.ContextChanged);
    var close = engine.Update([confirmState with { Packet = 2, Buttons = GamepadButtons.A | GamepadButtons.B }], 10, true);
    Equal(1, close.Count); Equal(PadAction.Close, close[0].Action);
    Equal(0, engine.Update([confirmState with { Packet = 3 }], 20, true).Count);
});
Check("Simultaneous controls retain action priority", () => {
    var engine = new GamepadInputEngine();
    var all = GamepadButtons.A | GamepadButtons.B | GamepadButtons.X | GamepadButtons.Y | GamepadButtons.Menu | GamepadButtons.View | GamepadButtons.DPadDown;
    engine.Update([new GamepadSnapshot(0, true, 0, GamepadButtons.None, 0, 0)], -1, true);
    var command = engine.Update([new GamepadSnapshot(0, true, 1, all, 0, 0)], 0, true).Single();
    Equal(PadAction.Close, command.Action); Equal(GamepadControl.Close, command.Control);
});
Check("Only a neutral-baselined controller can acquire ownership", () => {
    var engine = new GamepadInputEngine();
    var held = new GamepadSnapshot(0, true, 1, GamepadButtons.A, 0, 0);
    Equal(0, engine.Update([held], 0, true).Count);
    Equal(0, engine.Update([held with { Packet = 2, Buttons = GamepadButtons.None }], 10, true).Count);
    Equal(PadAction.Confirm, engine.Update([held with { Packet = 3 }], 20, true).Single().Action);
});
Check("Owner disconnect releases ownership", () => {
    var engine = new GamepadInputEngine();
    var neutral0 = new GamepadSnapshot(0, true, 1, GamepadButtons.None, 0, 0);
    var neutral1 = new GamepadSnapshot(1, true, 1, GamepadButtons.None, 0, 0);
    engine.Update([neutral0, neutral1], 0, true);
    Equal(PadAction.Confirm, engine.Update([neutral0 with { Packet = 2, Buttons = GamepadButtons.A }, neutral1], 10, true).Single().Action);
    var next = engine.Update([neutral0 with { Connected = false, Packet = 3 }, neutral1 with { Packet = 2, Buttons = GamepadButtons.B }], 20, true);
    Equal(1, next.Count); Equal(1, next[0].ControllerId); Equal(PadAction.Close, next[0].Action);
});
Check("Secondary drift cannot mask the active controller", () => {
    var engine = new GamepadInputEngine();
    var zero = new GamepadSnapshot(0, true, 1, GamepadButtons.None, 0, 0);
    var one = new GamepadSnapshot(1, true, 1, GamepadButtons.None, 0, 0);
    engine.Update([zero, one], 0, true);
    Equal(PadAction.Confirm, engine.Update([zero with { Packet = 2, Buttons = GamepadButtons.A }, one], 10, true).Single().Action);
    Equal(0, engine.Update([zero with { Packet = 3, Buttons = GamepadButtons.A }, one with { Packet = 2, LeftY = -20000 }], 20, true).Count);
});
Check("Activation suppresses only controls already held", () => {
    var engine = new GamepadInputEngine();
    var state = new GamepadSnapshot(0, true, 1, GamepadButtons.None, 0, 0);
    engine.Update([state], 0, true);
    engine.SetDeliveryEnabled(false, 10);
    engine.Update([state with { Packet = 2, Buttons = GamepadButtons.A }], 20, false);
    engine.SetDeliveryEnabled(true, 30);
    var next = engine.Update([state with { Packet = 3, Buttons = GamepadButtons.A | GamepadButtons.B }], 40, true);
    Equal(1, next.Count); Equal(PadAction.Close, next[0].Action);
});
Check("Busy-held direction does not fire after resume", () => {
    var engine = new GamepadInputEngine();
    var state = new GamepadSnapshot(0, true, 1, GamepadButtons.None, 0, 0);
    engine.Update([state], 0, true);
    engine.SetDeliveryEnabled(false, 10);
    engine.Update([state with { Packet = 2, Buttons = GamepadButtons.DPadDown }], 20, false);
    engine.SetDeliveryEnabled(true, 1000);
    Equal(0, engine.Update([state with { Packet = 3, Buttons = GamepadButtons.DPadDown }], 1010, true).Count);
    engine.Update([state with { Packet = 4 }], 1020, true);
    Equal(PadAction.Down, engine.Update([state with { Packet = 5, Buttons = GamepadButtons.DPadDown }], 1030, true).Single().Action);
});
Check("Diagnostics retain the newest 256 relevant events", () => {
    var engine = new GamepadInputEngine();
    var state = new GamepadSnapshot(0, true, 0, GamepadButtons.None, 0, 0);
    engine.Update([state], 0, true);
    for (int i = 1; i <= 300; i++)
    {
        var buttons = i % 2 == 0 ? GamepadButtons.None : GamepadButtons.A;
        engine.Update([state with { Packet = (uint)i, Buttons = buttons }], i, true);
    }
    var diagnostics = engine.GetDiagnostics();
    Equal(256, diagnostics.Count); Equal(300L, diagnostics[^1].Timestamp);
});
Console.WriteLine($"Passed: {passed}, Failed: {failed}, Skipped: 0");
return failed == 0 ? 0 : 1;
