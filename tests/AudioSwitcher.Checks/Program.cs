using AudioSwitcher.Core;
using AudioSwitcher.Platform;

int passed = 0, failed = 0;
void Check(string name, Action test) { try { test(); Console.WriteLine($"PASS {name}"); passed++; } catch (Exception e) { Console.WriteLine($"FAIL {name}: {e.Message}"); failed++; } }
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
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
Check("TV shortcut actions do not repeat while held", () => {
    var gate = new InputGate();
    Equal(true, gate.Accept(PadAction.Secondary, 0));
    Equal(false, gate.Accept(PadAction.Secondary, 900));
    gate.Accept(PadAction.None, 910);
    Equal(true, gate.Accept(PadAction.Settings, 920));
    Equal(false, gate.Accept(PadAction.Settings, 1500));
});
Console.WriteLine($"Passed: {passed}, Failed: {failed}, Skipped: 0");
return failed == 0 ? 0 : 1;
