using Rayxel.Link.Media;
using Vortice.DXGI;

namespace Rayxel.Send.Windows;

/// <summary>
/// A pointer shape as desktop duplication hands it over. <see cref="Height"/> is the
/// pointer's own: a monochrome shape's buffer holds twice as many rows, its AND mask above
/// its XOR mask. Never changed once made, so it can be passed between threads as it is.
/// </summary>
internal sealed class PointerShape(PointerShapeType type, int width, int height, int pitch, byte[] pixels)
{
    public PointerShapeType Type { get; } = type;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int Pitch { get; } = pitch;
    public byte[] Pixels { get; } = pixels;
}

/// <summary>
/// The pointer, from the capture that sees it to the cursor that draws it.
/// <para>
/// Desktop duplication leaves the pointer out of the picture and reports it on the side,
/// with each frame it hands over: where it is, whether it shows, and a new shape whenever
/// the shape changes. So the capture is the only thing that knows it, and the cursor the
/// sender asks for (<see cref="WindowsPlatform.CreateCursor"/>) has to learn it from the
/// capture. The two are made independently and in either order - a sender makes a cursor
/// per viewer and keeps a capture across viewers, and a new capture may start after the
/// cursor that will draw its pointer - so neither holds the other. Both hold this instead,
/// one per platform.
/// </para>
/// <para>
/// The newest capture to start owns it; whatever an older one still says is ignored, and
/// a capture that stops clears it on its way out only if it is still the owner. Everything
/// is copied in and out under one lock: the writer is the capture's thread, the reader is
/// usually that same thread (the cursor is asked while a frame is being encoded) and now
/// and then another.
/// </para>
/// </summary>
internal sealed unsafe class PointerFeed
{
    /// <summary>What the cursor needs to draw the pointer over a frame.</summary>
    /// <param name="X">The shape's top left corner in the source's pixels; may lie off any edge.</param>
    /// <param name="SourceWidth">The desktop's own size, which the frame may be a smaller copy of.</param>
    public readonly record struct State(bool Visible, int X, int Y, int SourceWidth, int SourceHeight, PointerShape? Shape);

    private readonly object _gate = new();
    private object? _owner;
    private State _state;

    // The picture being handed over right now, if any: valid only on the capture's thread
    // and only while its frame callback runs.
    private byte* _frame;
    private int _frameStride, _frameWidth, _frameHeight, _frameThread;

    /// <summary>Makes <paramref name="owner"/> the capture whose pointer this is, and forgets the last one's.</summary>
    public void Claim(object owner)
    {
        lock (_gate)
        {
            _owner = owner;
            _state = default;
            _frame = null;
        }
    }

    /// <summary>The pointer as <paramref name="owner"/> now sees it. Ignored from a capture that is not the owner.</summary>
    public void Publish(object owner, in State state)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_owner, owner)) return;
            _state = state;
        }
    }

    /// <summary>A capture going away: there is no pointer until another one says so.</summary>
    public void Leave(object owner)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_owner, owner)) return;
            _owner = null;
            _state = default;
            _frame = null;
        }
    }

    public State Read()
    {
        lock (_gate) return _state;
    }

    /// <summary>
    /// Lends the frame being handed over to whoever draws the pointer, for the length of the
    /// callback. Some pointers invert what is under them rather than cover it - the text
    /// cursor is the common one - and an overlay of fixed colours can only do that if it is
    /// made from the pixels it will lie on.
    /// </summary>
    public void ShowFrame(object owner, in CapturedFrame frame)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_owner, owner)) return;
            _frame = frame.Data;
            _frameStride = frame.Stride;
            _frameWidth = frame.Width;
            _frameHeight = frame.Height;
            _frameThread = Environment.CurrentManagedThreadId;
        }
    }

    public void HideFrame(object owner)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_owner, owner)) _frame = null;
        }
    }

    /// <summary>
    /// The frame lent by <see cref="ShowFrame"/>, when there is one of this size and the
    /// caller is inside its callback: on the capture's thread, while it lasts. Anywhere else
    /// the memory may already be gone, so there is none.
    /// </summary>
    public bool TryBorrowFrame(int width, int height, out byte* data, out int stride)
    {
        lock (_gate)
        {
            bool ok = _frame != null && _frameWidth == width && _frameHeight == height &&
                      _frameThread == Environment.CurrentManagedThreadId;
            data = ok ? _frame : null;
            stride = ok ? _frameStride : 0;
            return ok;
        }
    }
}
