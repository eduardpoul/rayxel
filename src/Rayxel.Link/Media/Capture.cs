namespace Rayxel.Link.Media;

/// <summary>What a capture and its source agreed on. Changes mid-stream when the size does.</summary>
/// <param name="Bt601">The colours are coded by BT.601 rather than BT.709.</param>
/// <param name="FullRange">Full 0-255 levels rather than the limited 16-235.</param>
/// <param name="Description">One line for the log: format, size, colours, how the buffers are shared.</param>
public readonly record struct CaptureFormat(int Width, int Height, bool Bt601, bool FullRange, string Description);

/// <summary>
/// One picture from a capture, in NV12: <see cref="Height"/> rows of luma, each
/// <see cref="Stride"/> bytes apart, then <see cref="Height"/>/2 rows of interleaved Cb Cr
/// at the same stride. The memory is the capture's and is valid only during the callback
/// that hands it over.
/// </summary>
public readonly unsafe struct CapturedFrame
{
    public readonly byte* Data;
    public readonly int Length;
    public readonly int Stride;
    public readonly int Width, Height;

    /// <summary>When this process was handed the frame, on <see cref="Clock.NowNs"/>: the earliest moment there is a time for.</summary>
    public readonly long ArrivalNs;

    public CapturedFrame(byte* data, int length, int stride, int width, int height, long arrivalNs)
    {
        Data = data;
        Length = length;
        Stride = stride;
        Width = width;
        Height = height;
        ArrivalNs = arrivalNs;
    }

    /// <summary>Bytes a whole NV12 picture of this size and stride takes.</summary>
    public int Needed => Stride * Height + Stride * (Height / 2);

    public ReadOnlySpan<byte> Span => new(Data, Length);
}

public delegate void FrameHandler(in CapturedFrame frame);

/// <summary>
/// A source of pictures: gamescope through PipeWire on a Steam Deck, the desktop on Windows,
/// a file for tests.
/// <para>
/// Frames and format changes arrive on the capture's own thread. A capture that cannot go on
/// says so through <see cref="Failed"/> - from inside its own callbacks, so it must not be
/// paused or disposed there, only afterwards and from another thread.
/// </para>
/// <para>
/// Stopping a capture can be dangerous to the source: gamescope has been seen to abort, and
/// take the game with it, when a consumer leaves its stream while a frame is on its way (see
/// src/rayxel-send/src/capture.h). So whoever owns one keeps it for as long as it can, and
/// when it has to go, calls <see cref="Pause"/>, waits <see cref="SettleMs"/>, and only then
/// disposes it. After <see cref="Pause"/> no callback is made again.
/// </para>
/// </summary>
public interface ICaptureSource : IDisposable
{
    /// <summary>How long to wait between <see cref="Pause"/> and disposing, so that the disconnect finds no frame in flight.</summary>
    public const int SettleMs = 200;

    /// <summary>
    /// Connects and starts delivering. A size of 0x0 is the source's own; a source that only
    /// scales down fits a larger request into its own size. False, with the reason, when it
    /// cannot be opened at all.
    /// </summary>
    bool Start(int width, int height, out string why);

    /// <summary>Whether pictures can arrive: connected and not paused.</summary>
    bool Streaming { get; }

    event Action<CaptureFormat>? FormatChanged;
    event FrameHandler? Frame;
    event Action<string>? Failed;

    void Pause();
}
