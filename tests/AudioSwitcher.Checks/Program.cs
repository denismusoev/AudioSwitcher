using AudioSwitcher.Core;

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
Console.WriteLine($"Passed: {passed}, Failed: {failed}, Skipped: 0");
return failed == 0 ? 0 : 1;
