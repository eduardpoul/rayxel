namespace Rayxel.Ui;

/// <summary>
/// Turns the design's base sizes into pixels for the screen we are on.
/// <para>
/// Every size is quoted at zoom 1 and multiplied by one factor derived from the
/// <em>monitor's</em> height. Height rather than width because that is what tracks how far
/// away a display is used from — an ultrawide is not read from further back than a 16:9
/// monitor of the same height. The monitor rather than the window because text on Windows
/// does not shrink when its window does: it used to follow the window's height, and a
/// 900-tall window on a 1440-tall monitor got fine print for no reason anyone could see.
/// </para>
/// <para>
/// The window still caps it, from below only: when the window is too narrow for the
/// bottom bar, or too short for it and a flyout, everything steps down to fit rather than
/// hanging off the edge.
/// </para>
/// </summary>
public static class Layout
{
    /// <summary>
    /// The prototype's own formula, verbatim:
    /// <c>z = min(1.6, max(0.88, 0.000528·h + 0.54))</c>.
    /// 900 → 1.02, 1080 → 1.11, 1440 → 1.30, 2160 → 1.60.
    /// </summary>
    public static float ZoomFor(int displayHeightPx) =>
        Comfort * Math.Min(1.60f, Math.Max(0.88f, 0.000528f * displayHeightPx + 0.54f));

    /// <summary>
    /// The one knob for overall size — raise it if the interface is fine print, lower it if
    /// the bar starts eating the picture. With the Fluent ramp's 14px body text this puts it
    /// at 20px on a 1440-tall monitor, window or full screen alike.
    /// </summary>
    private const float Comfort = 1.1f;

    /// <summary>The bottom bar's width at zoom 1, with a margin either side.</summary>
    private const float BarBaseWidth = 840f;

    /// <summary>Enough height at zoom 1 for the bar and a short flyout above it.</summary>
    private const float MinBaseHeight = 460f;

    public static float Factor { get; private set; } = 1f;

    /// <summary>
    /// Height of the monitor the window is on. Kept current by the window procedure, which
    /// sees the window move between monitors; 0 until it has been read.
    /// </summary>
    public static int DisplayHeight { get; set; }

    /// <summary>
    /// The largest factor the atlas was rasterised for. Fonts are built once at this size
    /// and scaled <em>down</em> at runtime, because a downscaled glyph looks soft while an
    /// upscaled one looks broken.
    /// </summary>
    public static float AtlasFactor { get; private set; } = 1f;

    public static void SetAtlasFactor(float factor) =>
        AtlasFactor = Math.Clamp(factor, 0.88f, 1.60f * Comfort);

    public static void Update(int windowWidthPx, int windowHeightPx)
    {
        float wanted = ZoomFor(DisplayHeight > 0 ? DisplayHeight : windowHeightPx);
        float fits = Math.Min(windowWidthPx / BarBaseWidth, windowHeightPx / MinBaseHeight);
        Factor = Math.Max(0.6f, Math.Min(wanted, fits));
    }

    /// <summary>Scales a base size. Rounded, so 1px rules stay 1px rather than 1.3.</summary>
    public static float S(float baseSize) => MathF.Round(baseSize * Factor);

    /// <summary>
    /// Scales a text size without rounding, and never below the handoff's 10px floor —
    /// "nothing below 10px" is the one type rule stated twice in the revision.
    /// </summary>
    public static float T(float baseSize) => MathF.Max(10f, baseSize) * Factor;

    /// <summary>How much ImGui must scale the rasterised glyphs to reach the current size.</summary>
    public static float FontScale => AtlasFactor <= 0 ? 1f : Factor / AtlasFactor;
}
