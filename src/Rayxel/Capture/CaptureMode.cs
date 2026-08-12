namespace Rayxel.Capture;

/// <summary>
/// What actually crosses the wire for a mode — which is not the same thing as the media
/// subtype the app receives.
/// </summary>
public enum WireTransport
{
    /// <summary>Uncompressed pixels on the wire. No codec anywhere in the path.</summary>
    Raw,

    /// <summary>
    /// The subtype says uncompressed, but the capture stack is decoding a codec for us.
    /// Costs a hardware encode on the device and a decode on the host — the exact latency
    /// we are trying to remove, hidden behind an innocent-looking NV12.
    /// <para>
    /// Usually MJPEG, because that is what most UVC cards carry, but H.264 and HEVC cards
    /// do the same thing and it is just as invisible.
    /// </para>
    /// </summary>
    CompressedDecoded,

    /// <summary>Compressed and delivered as-is; the decode is ours to do.</summary>
    Compressed
}

/// <summary>
/// Why we believe a mode carries a codec. Two independent detectors run, and them
/// agreeing is what makes the claim worth showing to a user.
/// </summary>
[Flags]
public enum TransportEvidence
{
    None = 0,

    /// <summary>
    /// The capture stack published this type directly before the compressed type it is
    /// the decoded output of. Structural, and exact where it applies.
    /// </summary>
    Adjacency = 1,

    /// <summary>
    /// Its uncompressed payload would not fit through the link the device's own other
    /// modes reveal. Physical, and works on hardware whose publishing order we have
    /// never seen.
    /// </summary>
    Bandwidth = 2
}

/// <summary>
/// One media type the device exposes. UVC devices report a frame-rate <em>range</em> per
/// type rather than one entry per rate, so <see cref="MinFps"/>/<see cref="MaxFps"/>
/// are what the UI offers and <see cref="Fps"/> is the type's nominal rate.
/// </summary>
public sealed record CaptureMode(
    int TypeIndex,
    VideoFormat Format,
    int Width,
    int Height,
    double Fps,
    double MinFps,
    double MaxFps)
{
    /// <summary>Set by <see cref="CaptureDevice.ClassifyTransport"/> once the whole list is known.</summary>
    public WireTransport Transport { get; init; } = WireTransport.Raw;

    /// <summary>Which detectors fired. Empty for modes believed raw.</summary>
    public TransportEvidence Evidence { get; init; } = TransportEvidence.None;

    /// <summary>
    /// The codec on the wire — the mode's own for <see cref="WireTransport.Compressed"/>,
    /// its twin's for <see cref="WireTransport.CompressedDecoded"/>. Null when raw, and
    /// null when bandwidth alone gave it away and we never saw which codec it was.
    /// </summary>
    public string? WireCodec { get; init; }

    /// <summary>True when a codec sits in the path, however the mode is labelled.</summary>
    public bool HasCodecInPath => Transport != WireTransport.Raw;

    public bool IsCompressed => Format.Compression == Compression.Lossy;

    /// <summary>
    /// Formats the renderer has a sampling path for. Held here rather than in the renderer
    /// because three separate places need to filter on it — the menu, the startup pick and
    /// the device probe — and they must agree, or a device looks capable and shows black.
    /// </summary>
    public bool IsRenderable => Format.Name is "NV12" or "P010";

    /// <summary>
    /// Renderable and fast enough to play through — this card advertises rates down to a
    /// few frames a second, and nobody is going to pick 5 fps for a game. They only
    /// cluttered a list that already has ninety entries.
    /// <para>
    /// This is the honest set, redundancy included. Diagnostics want it; the UI wants
    /// <see cref="IsOfferable"/>.
    /// </para>
    /// </summary>
    public bool IsUsable => IsRenderable && MaxFps >= 29.5;

    /// <summary>
    /// A codec-carrying mode that has a genuine uncompressed twin at the same format, size
    /// and rate. There is no reason to ever pick it: it costs an encode on the device and a
    /// decode on the host to deliver exactly what the twin delivers for neither.
    /// <para>
    /// Set by <see cref="CaptureDevice.ClassifyTransport"/>, and deliberately <em>not</em>
    /// set on compressed modes that reach a rate no uncompressed format does — 4K60,
    /// 1440p144, 1080p240 exist only because of the codec, and hiding those would remove
    /// capability rather than clutter.
    /// </para>
    /// </summary>
    public bool IsRedundant { get; init; }

    /// <summary>Worth putting in front of a user.</summary>
    public bool IsOfferable => IsUsable && !IsRedundant;

    /// <summary>
    /// Short tag for the UI. A trailing <c>?</c> means the mode claims to be uncompressed
    /// while carrying a codec — the thing no other capture app tells you.
    /// </summary>
    public string WireTag => Transport switch
    {
        WireTransport.Raw => "raw",
        WireTransport.Compressed => (WireCodec ?? Format.Name).ToLowerInvariant(),
        _ => (WireCodec ?? "codec").ToLowerInvariant() + "?"
    };

    /// <summary>Uncompressed bytes/second at the given rate; 0 for lossy formats.</summary>
    public double BytesPerSecond(double fps) =>
        IsCompressed ? 0 : Format.BytesPerFrame(Width, Height) * fps;

    public double MegabytesPerSecond(double fps) => BytesPerSecond(fps) / (1024.0 * 1024.0);

    /// <summary>
    /// Rates worth offering in the UI: the standard set, clamped to what this type
    /// supports, always including the range endpoints.
    /// </summary>
    public IEnumerable<double> SelectableRates()
    {
        double[] standard = [24, 25, 30, 50, 60, 90, 100, 120, 144, 240];
        var rates = standard.Where(r => r >= MinFps - 0.5 && r <= MaxFps + 0.5).ToList();
        if (!rates.Any(r => Math.Abs(r - MaxFps) < 0.5)) rates.Add(MaxFps);
        if (!rates.Any(r => Math.Abs(r - MinFps) < 0.5)) rates.Insert(0, MinFps);
        return rates.Distinct().OrderBy(r => r);
    }

    public string Describe(double fps) =>
        $"{Width}x{Height} @ {fps:0.##} {Format.Name}" + (IsCompressed ? " (compressed)" : "");

    public override string ToString() => Describe(MaxFps);
}
