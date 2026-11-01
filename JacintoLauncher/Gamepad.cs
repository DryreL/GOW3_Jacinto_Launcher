#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace JacintoLauncher;

internal enum PadAction { Next, Previous, Left, Right, Accept, Back, PageNext, PagePrevious }

/// <summary>Button and stick state in one neutral shape, whichever API it came from.</summary>
internal readonly record struct PadState(bool Up, bool Down, bool Left, bool Right, bool A, bool B, bool Lb, bool Rb);

/// <summary>Turns pad states into navigation actions: one per press, repeating while a direction is held.</summary>
internal sealed class PadMapper
{
    private const int RepeatDelay = 450, RepeatEvery = 150;
    private PadState last;
    private int heldFor;

    internal List<PadAction> Step(PadState now, int elapsedMilliseconds)
    {
        var actions = new List<PadAction>();
        if (now.A && !last.A) actions.Add(PadAction.Accept);
        if (now.B && !last.B) actions.Add(PadAction.Back);
        if (now.Lb && !last.Lb) actions.Add(PadAction.PagePrevious);
        if (now.Rb && !last.Rb) actions.Add(PadAction.PageNext);
        var direction = now.Up ? PadAction.Previous : now.Down ? PadAction.Next : now.Left ? PadAction.Left : now.Right ? PadAction.Right : (PadAction?)null;
        var lastDirection = last.Up ? PadAction.Previous : last.Down ? PadAction.Next : last.Left ? PadAction.Left : last.Right ? PadAction.Right : (PadAction?)null;
        if (direction != lastDirection) { heldFor = 0; if (direction is { } pressed) actions.Add(pressed); }
        else if (direction is { } held)
        {
            var before = heldFor;
            heldFor += elapsedMilliseconds;
            if (heldFor >= RepeatDelay && (before < RepeatDelay || (heldFor - RepeatDelay) / RepeatEvery != (before - RepeatDelay) / RepeatEvery)) actions.Add(held);
        }
        last = now;
        return actions;
    }
}

/// <summary>Reads the first pad: XInput on Windows (also under Proton), the joystick device on Linux. macOS reports nothing.</summary>
internal sealed class GamepadReader : IDisposable
{
    private const int StickThreshold = 16000;
    private readonly Thread? linuxThread;
    private volatile bool stop;
    private PadState linuxState;

    internal GamepadReader()
    {
        if (!OperatingSystem.IsLinux()) return;
        linuxThread = new Thread(ReadLinux) { IsBackground = true, Name = "gamepad" };
        linuxThread.Start();
    }

    internal PadState Read() => OperatingSystem.IsWindows() ? ReadXInput() : linuxState;

    public void Dispose() => stop = true;

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState { public uint Packet; public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LeftX, LeftY, RightX, RightY; }

    [DllImport("xinput1_4.dll")] private static extern uint XInputGetState(uint index, out XInputState state);

    private static PadState ReadXInput()
    {
        try
        {
            for (uint pad = 0; pad < 4; pad++)
            {
                if (XInputGetState(pad, out var s) != 0) continue;
                bool B(int mask) => (s.Buttons & mask) != 0;
                return new PadState(B(0x1) || s.LeftY > StickThreshold, B(0x2) || s.LeftY < -StickThreshold, B(0x4) || s.LeftX < -StickThreshold, B(0x8) || s.LeftX > StickThreshold, B(0x1000), B(0x2000), B(0x100), B(0x200));
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException) { } // Windows 7 or a stripped Wine prefix: keyboard and mouse still work.
        return default;
    }

    // Linux joystick API: 8-byte events (time, value, type, number). Blocking reads, so a retry loop finds a pad connected later.
    private void ReadLinux()
    {
        while (!stop)
        {
            try
            {
                var device = Directory.Exists("/dev/input") ? Array.Find(Directory.GetFiles("/dev/input", "js*"), _ => true) : null;
                if (device is null) { Thread.Sleep(3000); continue; }
                using var stream = new FileStream(device, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buffer = new byte[8];
                while (!stop && stream.Read(buffer, 0, 8) == 8)
                {
                    var value = BitConverter.ToInt16(buffer, 4);
                    var type = buffer[6] & 0x7F;
                    int number = buffer[7];
                    var s = linuxState;
                    if (type == 1) // Button: 0 A, 1 B, 4 LB, 5 RB on the common xpad mapping.
                    {
                        var on = value != 0;
                        s = number switch { 0 => s with { A = on }, 1 => s with { B = on }, 4 => s with { Lb = on }, 5 => s with { Rb = on }, _ => s };
                    }
                    else if (type == 2) // Axis: 0/1 left stick, 6/7 D-pad.
                    {
                        if (number is 1 or 7) s = s with { Up = value < -StickThreshold, Down = value > StickThreshold };
                        if (number is 0 or 6) s = s with { Left = value < -StickThreshold, Right = value > StickThreshold };
                    }
                    linuxState = s;
                }
                linuxState = default;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { linuxState = default; Thread.Sleep(3000); }
        }
    }
}
