namespace Rayxel.Link.Input;

/// <summary>
/// The viewer's gamepad, reproduced on the sender: on Linux a uinput device that looks like
/// an Xbox controller. It is given the controller's whole state each time and works out the
/// changes itself, so a lost datagram is repaired by the next one.
/// </summary>
public interface IPadSink : IDisposable
{
    void Apply(in PadState state);

    /// <summary>Anything held: a button, a trigger, a stick off centre.</summary>
    bool Active { get; }

    /// <summary>Lets go of everything, as when the link breaks.</summary>
    void Release();
}

/// <summary>
/// The viewer's keyboard and mouse, reproduced on the sender. Keys come as the whole set that
/// is down (bit n is Linux key code n); the mouse as running totals of movement and wheel
/// (120ths of a notch), from which the sink takes the difference to the last totals it saw.
/// The first mouse message only sets the starting point.
/// </summary>
public interface IDeskSink : IDisposable
{
    void Keys(ReadOnlySpan<byte> down);

    void Mouse(byte buttons, int x, int y, int wheel, int hwheel);

    /// <summary>A key or a mouse button held.</summary>
    bool Active { get; }

    void Release();
}
