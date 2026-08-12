namespace Rayxel.Capture;

/// <summary>
/// What one device's mode list reveals about the wire behind it.
/// <para>
/// Nothing here is read from the device — Windows exposes no link speed for a capture
/// source. It is inferred from the payload rates the device is willing to offer, which
/// is why the evidence is reported alongside the number.
/// </para>
/// </summary>
public sealed record LinkBudget(double CeilingMiBs, int AgreeingGeometries, string LinkClass)
{
    public static readonly LinkBudget Unknown = new(0, 0, "unknown");

    /// <summary>
    /// True when distinct resolutions converge on the same byte rate. One mode alone at
    /// the top is just the largest mode the device offers, and says nothing about a limit.
    /// </summary>
    public bool Corroborated => AgreeingGeometries >= 2;

    public bool Known => CeilingMiBs > 0;

    public double Gbits => CeilingMiBs * 1024 * 1024 * 8 / 1e9;

    /// <summary>Rate above which a mode cannot be genuinely uncompressed on this device.</summary>
    public double SuspectAboveMiBs => Corroborated ? CeilingMiBs * 1.15 : double.MaxValue;

    public string Describe() => !Known
        ? "no uncompressed mode offered — link budget unknown"
        : Corroborated
            ? $"{CeilingMiBs:0} MiB/s ({Gbits:0.0} Gbit/s), {AgreeingGeometries} geometries agree — {LinkClass}"
            : $"{CeilingMiBs:0} MiB/s from a single mode — a floor, not a ceiling";

    /// <summary>
    /// Derives the budget from already-classified modes.
    /// <para>
    /// Only genuinely raw modes carry a payload whose size we know: a compressed mode's
    /// wire size depends on the picture, so it tells us nothing about capacity.
    /// </para>
    /// </summary>
    public static LinkBudget Measure(IEnumerable<CaptureMode> modes)
    {
        var rates = modes
            .Where(m => m.Transport == WireTransport.Raw && m.MaxFps > 0)
            .Select(m => (Geometry: (m.Width, m.Height, Fps: Math.Round(m.MaxFps)),
                          Rate: m.MegabytesPerSecond(m.MaxFps)))
            .Where(x => x.Rate > 0)
            .ToList();

        if (rates.Count == 0) return Unknown;

        // Walk candidate rates from the top and take the first that more than one distinct
        // geometry reaches. 1080p120 and 2160p30 are the same number of bytes per second;
        // a device offering both at that rate is showing us where its wire runs out. A lone
        // peak is reported too, but flagged as uncorroborated so nothing is inferred from it.
        double best = rates.Max(x => x.Rate);
        foreach (var candidate in rates.Select(x => x.Rate).Distinct().OrderByDescending(r => r))
        {
            int agreeing = rates.Where(x => x.Rate >= candidate * 0.97)
                                .Select(x => x.Geometry)
                                .Distinct()
                                .Count();
            if (agreeing >= 2)
                return new LinkBudget(candidate, agreeing, Classify(candidate));
        }

        return new LinkBudget(best, 1, Classify(best));
    }

    /// <summary>
    /// Names the bracket the ceiling falls in. Deliberately phrased as a resemblance:
    /// we are reading a link speed off the modes a device chose to advertise, and a card
    /// is free to advertise less than its wire can carry.
    /// </summary>
    private static string Classify(double miBs) => miBs switch
    {
        < 6 => "USB 1.1 class",
        < 48 => "USB 2.0 class (480 Mbit/s)",
        < 520 => "USB 3.x Gen 1 class (5 Gbit/s)",
        < 1150 => "USB 3.x Gen 2 class (10 Gbit/s)",
        _ => "internal or USB4 class"
    };
}
