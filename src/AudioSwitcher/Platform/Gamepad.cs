using System.Runtime.InteropServices;
using AudioSwitcher.Core;

namespace AudioSwitcher.Platform;
public static class Gamepad
{
    [ThreadStatic] private static PadAction[]? controllerActions;
    [StructLayout(LayoutKind.Sequential)] public struct State { public uint Packet; public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LeftX, LeftY, RightX, RightY; }
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] private static extern uint GetState(uint index, out State state);
    public static bool TryRead(out PadAction action)
    {
        var actions = controllerActions ??= new PadAction[4];
        Array.Fill(actions, PadAction.None);
        int connected = 0;
        for (uint i = 0; i < 4; i++)
        {
            if (GetState(i, out var s) != 0) continue;
            actions[connected++] = Map(s);
        }
        action = SelectAction(actions);
        return connected != 0;
    }

    internal static PadAction SelectAction(IReadOnlyList<PadAction> actions)
    {
        foreach (var action in actions)
            if (action != PadAction.None) return action;
        return PadAction.None;
    }

    public static PadAction Map(State state) => (state.Buttons & 0x2000) != 0 ? PadAction.Close
        : (state.Buttons & 0x1000) != 0 ? PadAction.Confirm
        : (state.Buttons & 0x4000) != 0 ? PadAction.Secondary
        : (state.Buttons & 0x8000) != 0 ? PadAction.CreateOrEdit
        : (state.Buttons & 0x0010) != 0 ? PadAction.Settings
        : (state.Buttons & 0x0020) != 0 ? PadAction.Details
        : (state.Buttons & 0x0004) != 0 || state.LeftX < -16000 ? PadAction.Left
        : (state.Buttons & 0x0008) != 0 || state.LeftX > 16000 ? PadAction.Right
        : (state.Buttons & 1) != 0 || state.LeftY > 16000 ? PadAction.Up
        : (state.Buttons & 2) != 0 || state.LeftY < -16000 ? PadAction.Down : PadAction.None;
}
