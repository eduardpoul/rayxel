namespace Rayxel.Link.Media;

/// <summary>What an encoder is opened for.</summary>
/// <param name="Fps">Nominal rate. Frames come when the source has one; this only tells the rate control how many bits one may have.</param>
/// <param name="BufferFrames">
/// How many frames' worth of bits the rate control may run ahead or behind. Small keeps every
/// frame near the same size, which is what a network wants.
/// </param>
/// <param name="MaxFrameBytes">Hard cap on one encoded frame, or 0 for none.</param>
/// <param name="Gop">Frames between keyframes, or 0 for "only the first and when asked".</param>
public sealed record EncoderConfig(
    Wire.Codec Codec, int Width, int Height, int Fps, int BitrateKbps,
    double BufferFrames, int MaxFrameBytes, int Gop, bool Bt601, bool FullRange);

/// <summary>
/// One encoded frame. The memory is the encoder's and is valid until its next
/// <see cref="IVideoEncoder.Encode"/>. HEVC: an access unit in Annex B form. AV1: a temporal
/// unit of OBUs, temporal delimiter first. PyroWave: the codec's packets one after another,
/// which its decoder takes all at once.
/// </summary>
public readonly unsafe struct EncodedFrame
{
    public readonly byte* Data;
    public readonly int Length;
    public readonly bool Keyframe;

    /// <summary>The picture into the encoder's own memory.</summary>
    public readonly long UploadNs;

    /// <summary>From handing it over to holding the bitstream.</summary>
    public readonly long EncodeNs;

    public EncodedFrame(byte* data, int length, bool keyframe, long uploadNs, long encodeNs)
    {
        Data = data;
        Length = length;
        Keyframe = keyframe;
        UploadNs = uploadNs;
        EncodeNs = encodeNs;
    }

    public ReadOnlySpan<byte> Span => new(Data, Length);
}

/// <summary>
/// Turns NV12 pictures into a codec's bitstream, one at a time, on the thread that calls it.
/// Opened by <see cref="Sender.ISenderPlatform.OpenEncoder"/> for one size and one rate; a
/// new size, bitrate or codec is a new encoder.
/// </summary>
public interface IVideoEncoder : IDisposable
{
    Wire.Codec Codec { get; }

    /// <summary>One line on what was opened: codec, profile, level, rate control, upload path.</summary>
    string Description { get; }

    /// <summary>Why the last <see cref="Encode"/> failed.</summary>
    string Error { get; }

    /// <summary>
    /// Encodes one picture. <paramref name="cursor"/>, when there is one, is laid over the
    /// picture on its way in, without touching the frame itself (it belongs to the capture).
    /// False on failure, with the reason in <see cref="Error"/>.
    /// </summary>
    bool Encode(in CapturedFrame frame, bool forceKeyframe, CursorOverlay? cursor, out EncodedFrame encoded);
}
