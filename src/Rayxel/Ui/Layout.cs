namespace Rayxel.Ui;

/// <summary>
/// Turns the design's base sizes into pixels for the window we actually have.
/// <para>
/// Every size in the handoff is quoted at zoom 1 and multiplied by one factor derived from
/// the window's <em>height</em>. Height rather than width because that is what tracks how
/// far away a display is used from — an ultrawide is not read from further back than a
/// 16:9 monitor of the same height, and scaling by width made the panel take a quarter of
/// one.
/// </para>
/// </summary>
public static class Layout
{
    /// <summary>
    /// The prototype's own formula, verbatim:
    /// <c>z = min(1.6, max(0.88, 0.000528·h + 0.54))</c>.
    /// 900 → 1.02, 1080 → 1.11, 1440 → 1.30, 2160 → 1.60.
    /// </summary>
    public static float ZoomFor(int windowHeightPx) =>
        Comfort * Math.Min(1.60f, Math.Max(0.88f, 0.000528f * windowHeightPx + 0.54f));

    /// <summary>
    /// A deliberate step above the handoff's own numbers.
    /// <para>
    /// The design's scale is correct for the distance it was drawn at; on a real desk it
    /// reads small, and that has now been said twice. This is the one knob — raise it if
    /// the interface is still fine print, lower it if the panel starts eating the picture.
    /// An earlier attempt at the equivalent of 1.8 here was far too much, so it is
    /// deliberately modest.
    /// </para>
    /// </summary>
    private const float Comfort = 1.18f;

    public static float Factor { get; private set; } = 1f;

    /// <summary>
    /// The largest factor the atlas was rasterised for. Fonts are built once at this size
    /// and scaled <em>down</em> at runtime, because a downscaled glyph looks soft while an
    /// upscaled one looks broken.
    /// </summary>
    public static float AtlasFactor { get; private set; } = 1f;

    public static void SetAtlasFactor(float factor) =>
        AtlasFactor = Math.Clamp(factor, 0.88f, 1.60f * Comfort);

    public static void Update(int windowHeightPx) => Factor = ZoomFor(windowHeightPx);

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
