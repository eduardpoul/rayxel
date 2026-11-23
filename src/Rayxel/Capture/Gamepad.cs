using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Rayxel.Diagnostics;

namespace Rayxel.Capture;

/// <summary>
/// A controller at one instant, in the units Windows reports and the wire carries: the
/// button bits are XInput's own, triggers run 0 to 255, sticks -32768 to 32767 with right
/// and up positive.
/// </summary>
internal readonly record struct PadState(
    ushort Buttons, byte LeftTrigger, byte RightTrigger,
    short LeftX, short LeftY, short RightX, short RightY);

/// <summary>
/// Reads the first controller Windows offers through XInput: an Xbox pad, or anything
/// that presents itself as one. XInput is part of Windows, so this adds nothing to ship.
/// </summary>
internal sealed class Gamepad
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Reading
    {
        public uint Packet;
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LeftX, LeftY, RightX, RightY;
        // Not part of the documented struct. The variant of the call that also reports the
        // Guide button may write four more bytes, and they need somewhere to go.
        public uint Reserved;
    }

    /// <summary>
    /// XInputGetState as exported by ordinal 100: the same call with the Guide button
    /// included, which the named export leaves out. It is undocumented and has been there
    /// unchanged since Windows 8; if it ever is not, the named export is used instead and
    /// only that one button is lost.
    /// </summary>
    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    private static extern uint GetStateWithGuide(uint user, out Reading reading);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint GetState(uint user, out Reading reading);

    private int _user = -1;
    private long _nextSearchTicks;
    private bool _guideAvailable = true;

    /// <summary>Which of XInput's four slots is being read, or -1.</summary>
    public int User => _user;

    public bool TryRead(out PadState state)
    {
        state = default;
        if (_user < 0 && !Search()) return false;

        if (Read((uint)_user, out var r) != 0)
        {
            // Unplugged, or its batteries gave out. Look again in a while.
            _user = -1;
            _nextSearchTicks = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
            return false;
        }

        state = new PadState(r.Buttons, r.LeftTrigger, r.RightTrigger, r.LeftX, r.LeftY, r.RightX, r.RightY);
        return true;
    }

    /// <summary>
    /// Looks through the slots for a controller, at most once a second: asking about an
    /// empty slot makes Windows go and look for devices, which takes milliseconds. That is
    /// far too long to do on every pass of a loop that runs a thousand times a second.
    /// </summary>
    private bool Search()
    {
        long now = Stopwatch.GetTimestamp();
        if (now < _nextSearchTicks) return false;
        _nextSearchTicks = now + Stopwatch.Frequency;

        for (uint user = 0; user < 4; user++)
        {
            if (Read(user, out _) != 0) continue;
            _user = (int)user;
            return true;
        }
        return false;
    }

    private uint Read(uint user, out Reading reading)
    {
        if (_guideAvailable)
        {
            try { return GetStateWithGuide(user, out reading); }
            catch (EntryPointNotFoundException) { _guideAvailable = false; }
        }
        return GetState(user, out reading);
    }
}

/// <summary>
/// Sends this machine's controller to the sender, to be reproduced there as a gamepad the
/// Deck's games can see.
/// <para>
/// What goes out is the controller's whole state and never "this button went down". A lost
/// datagram is then repaired by the next one, and a button cannot stay held because the
/// datagram that released it went missing. It is sent the moment it changes and fifty
/// times a second besides, which is also how the sender knows the link is still there:
/// when these stop, it lets go of everything on its own.
/// </para>
/// <para>
/// Only while the window has the focus. XInput hands a controller's input to every
/// program that asks, focused or not, and a game being played on this machine must not
/// also be pressing buttons on the Deck.
/// </para>
/// </summary>
internal sealed class PadForwarder : IDisposable
{
    private readonly Socket _socket;
    private readonly uint _session;
    private readonly Func<bool> _focused;
    private Thread? _thread;
    private volatile bool _running;

    public PadForwarder(Socket socket, uint session, Func<bool> focused)
    {
        _socket = socket;
        _session = session;
        _focused = focused;
    }

    public void Start()
    {
        _running = true;
        _thread = new Thread(Run) { IsBackground = true, Name = "gamepad", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Run()
    {
        var pad = new Gamepad();
        byte[] packet = new byte[NetWire.PadSize];
        long repeatTicks = Stopwatch.Frequency / 50;

        PadState sent = default;
        bool everSent = false, wasConnected = false;
        long lastSendTicks = 0;
        uint sequence = 0;

        try
        {
            while (_running)
            {
                // The process runs with a one-millisecond timer, so this is how old a
                // press can be before it is noticed.
                Thread.Sleep(1);

                bool connected = pad.TryRead(out var state);
                if (connected != wasConnected)
                {
                    wasConnected = connected;
                    Log.Info(connected
                        ? $"gamepad: controller {pad.User + 1} found - its input goes to the sender while this window has the focus"
                        : "gamepad: controller gone - everything released on the sender");
                }

                // Until a controller has been seen, nothing is sent at all, so that one
                // does not appear on the Deck for a viewer who has none.
                if (!connected && !everSent) continue;
                if (!connected || !_focused()) state = default;

                long now = Stopwatch.GetTimestamp();
                if (everSent && state == sent && now - lastSendTicks < repeatTicks) continue;

                _socket.Send(packet, NetWire.WritePad(packet, _session, ++sequence, state), SocketFlags.None);
                sent = state;
                everSent = true;
                lastSendTicks = now;
            }
        }
        catch (DllNotFoundException)
        {
            Log.Warn("gamepad: XInput is not available on this system; controllers are not forwarded");
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            // The connection went away under us; whoever owns it deals with that.
        }
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(200);
        _thread = null;
    }
}
