using System.Runtime.InteropServices;
using AudioSwitcher.Core;

namespace AudioSwitcher.Platform;
public static class Gamepad
{
    [StructLayout(LayoutKind.Sequential)] public struct State { public uint Packet; public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LeftX, LeftY, RightX, RightY; }
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] private static extern uint GetState(uint index, out State state);
    public static bool TryRead(out PadAction action)
    {
        action = PadAction.None;
        for (uint i = 0; i < 4; i++)
        {
            if (GetState(i, out var s) != 0) continue;
            action = (s.Buttons & 0x2000) != 0 ? PadAction.Close
                : (s.Buttons & 0x1000) != 0 ? PadAction.Confirm
                : (s.Buttons & 0x0104) != 0 || s.LeftX < -16000 ? PadAction.Left
                : (s.Buttons & 0x0208) != 0 || s.LeftX > 16000 ? PadAction.Right
                : (s.Buttons & 1) != 0 || s.LeftY > 16000 ? PadAction.Up
                : (s.Buttons & 2) != 0 || s.LeftY < -16000 ? PadAction.Down : PadAction.None;
            return true;
        }
        return false;
    }
}
