using System.Numerics;
using ImGuiNET;

namespace Rayxel.Ui;

/// <summary>
/// The product mark: a diagonal chevron of five squares — a frame moving through the
/// pipeline, drawn in the same pixels the app scales.
/// <para>
/// Drawn from the geometry rather than uploaded as a texture. The handoff states it as
/// numbers on a grid, it is five rectangles, and a vector mark is sharp at every zoom the
/// layout produces — where a raster would have to be atlas'd at one size and resampled at
/// the rest. The masters it is transcribed from are in <c>Assets/Brand/svg/</c>.
/// </para>
/// <para>
/// The opacity ramp is the identity: never recolour the squares individually, and on a
/// coloured or photographic background use a monochrome base rather than the accent.
/// </para>
/// </summary>
public static class Brand
{
    /// <summary>Square origins and opacities on the 64-unit grid; squares are 9 wide, radius 2.</summary>
    private static readonly (float X, float Y, float Alpha)[] Chevron =
        [(16, 12, 0.4f), (28, 20, 0.7f), (40, 28, 1f), (28, 36, 0.7f), (16, 44, 0.4f)];

    /// <summary>
    /// The same mark on a 16-unit grid, squares 4 wide, radius 1. Below about 24px the five
    /// squares mush into a smear, so three carry the shape instead.
    /// </summary>
    private static readonly (float X, float Y, float Alpha)[] Compact =
        [(4, 2, 0.5f), (8, 6, 1f), (4, 10, 0.5f)];

    /// <summary>The size at which the three-square variant takes over, in pixels.</summary>
    private const float CompactBelow = 24f;

    /// <summary>
    /// Draws the mark into a <paramref name="size"/>-square box with its top-left corner at
    /// <paramref name="topLeft"/>. The geometry sits centred inside that box to within a
    /// grid unit, so centring the box centres the mark.
    /// </summary>
    /// <param name="colour">Base colour; the alpha of each square is scaled by it.</param>
    public static void Mark(ImDrawListPtr draw, Vector2 topLeft, float size, Vector4 colour)
    {
        bool compact = size < CompactBelow;
        var squares = compact ? Compact : Chevron;
        float unit = size / (compact ? 16f : 64f);
        var side = new Vector2((compact ? 4f : 9f) * unit);
        float radius = (compact ? 1f : 2f) * unit;

        foreach (var (x, y, alpha) in squares)
        {
            var at = topLeft + new Vector2(x * unit, y * unit);
            draw.AddRectFilled(at, at + side, Theme.U32(Theme.Alpha(colour, colour.W * alpha)), radius);
        }
    }

    /// <inheritdoc cref="Mark(ImDrawListPtr, Vector2, float, Vector4)"/>
    public static void Mark(ImDrawListPtr draw, Vector2 topLeft, float size) =>
        Mark(draw, topLeft, size, Theme.Accent);
}
