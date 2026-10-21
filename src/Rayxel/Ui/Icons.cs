using System.Numerics;
using ImGuiNET;

namespace Rayxel.Ui;

public enum Icon
{
    ChevronDown, ChevronUp, Close, More, Volume, Muted, Pulse,
    FullScreen, BackToWindow, Scale, Warning, Camera, MonitorOff, Lock
}

/// <summary>
/// The interface's icons, drawn as strokes on a 16-unit grid.
/// <para>
/// Not Segoe Fluent Icons, although that is what Windows itself uses: the font exists only
/// on Windows 11 (10 has the older MDL2 set under different code points), and an icon
/// that quietly turns into a box on one machine is worse than one drawn by hand. These
/// are a dozen polylines, sharp at every zoom the layout produces.
/// </para>
/// </summary>
public static class Icons
{
    /// <summary>Draws <paramref name="icon"/> into a <paramref name="size"/>-pixel square at <paramref name="topLeft"/>.</summary>
    public static void Draw(ImDrawListPtr draw, Icon icon, Vector2 topLeft, float size, Vector4 colour)
    {
        var p = new Pen(draw, topLeft, size / 16f, Theme.U32(colour));

        switch (icon)
        {
            case Icon.ChevronDown:
                p.Line3(4, 6, 8, 10, 12, 6);
                break;

            case Icon.ChevronUp:
                p.Line3(4, 10, 8, 6, 12, 10);
                break;

            case Icon.Close:
                p.Line(4, 4, 12, 12);
                p.Line(12, 4, 4, 12);
                break;

            case Icon.More:
                p.Dot(3.5f, 8, 1.1f);
                p.Dot(8, 8, 1.1f);
                p.Dot(12.5f, 8, 1.1f);
                break;

            case Icon.Volume:
                p.Speaker();
                p.Arc(8, 8, 3f, -0.8f, 0.8f);
                p.Arc(8, 8, 5.5f, -0.85f, 0.85f);
                break;

            case Icon.Muted:
                p.Speaker();
                p.Line(10.5f, 6.25f, 14f, 9.75f);
                p.Line(14f, 6.25f, 10.5f, 9.75f);
                break;

            case Icon.Pulse:
                p.Move(1.5f, 9); p.To(4.5f, 9); p.To(6.5f, 4); p.To(9.5f, 12); p.To(11.5f, 7.5f); p.To(14.5f, 7.5f);
                p.Stroke();
                break;

            case Icon.FullScreen:
                p.Line3(2.5f, 6, 2.5f, 2.5f, 6, 2.5f);
                p.Line3(10, 2.5f, 13.5f, 2.5f, 13.5f, 6);
                p.Line3(13.5f, 10, 13.5f, 13.5f, 10, 13.5f);
                p.Line3(6, 13.5f, 2.5f, 13.5f, 2.5f, 10);
                break;

            case Icon.BackToWindow:
                p.Line3(6, 2.5f, 6, 6, 2.5f, 6);
                p.Line3(10, 2.5f, 10, 6, 13.5f, 6);
                p.Line3(13.5f, 10, 10, 10, 10, 13.5f);
                p.Line3(2.5f, 10, 6, 10, 6, 13.5f);
                break;

            case Icon.Scale:
                p.Line3(9.5f, 2.5f, 13.5f, 2.5f, 13.5f, 6.5f);
                p.Line(13.5f, 2.5f, 9, 7);
                p.Line3(6.5f, 13.5f, 2.5f, 13.5f, 2.5f, 9.5f);
                p.Line(2.5f, 13.5f, 7, 9);
                break;

            case Icon.Warning:
                p.Move(8, 2.2f); p.To(14.3f, 13.3f); p.To(1.7f, 13.3f);
                p.Stroke(closed: true);
                p.Line(8, 6.5f, 8, 9.7f);
                p.Dot(8, 11.5f, 0.75f);
                break;

            case Icon.Camera:
                p.Rect(1.5f, 4, 11, 12, 1.5f);
                p.Move(11, 7); p.To(14.5f, 5); p.To(14.5f, 11); p.To(11, 9);
                p.Stroke(closed: true);
                break;

            case Icon.MonitorOff:
                p.Rect(1.5f, 2.5f, 14.5f, 11.5f, 1.5f);
                p.Line(5.5f, 14, 10.5f, 14);
                p.Line(8, 11.5f, 8, 14);
                p.Line(5.5f, 5, 10.5f, 9);
                p.Line(10.5f, 5, 5.5f, 9);
                break;

            case Icon.Lock:
                p.Rect(3, 7, 13, 14.5f, 1.5f);
                p.Move(5.5f, 7);
                p.ArcTo(8, 5, 2.5f, MathF.PI, MathF.PI * 2f);
                p.To(10.5f, 7);
                p.Stroke();
                break;
        }
    }

    /// <summary>
    /// The filled circle a warning leads with: <c>!</c> for caution and critical, <c>i</c>
    /// for information, cut out of the fill in <paramref name="glyph"/>.
    /// </summary>
    public static void SeverityBadge(ImDrawListPtr draw, Vector2 centre, float radius,
                                     Vector4 fill, Vector4 glyph, bool exclamation)
    {
        draw.AddCircleFilled(centre, radius, Theme.U32(fill));

        uint c = Theme.U32(glyph);
        float t = MathF.Max(1f, radius * 0.22f);
        float dot = MathF.Max(0.8f, radius * 0.14f);

        if (exclamation)
        {
            draw.AddLine(centre + new Vector2(0, -radius * 0.5f), centre + new Vector2(0, radius * 0.12f), c, t);
            draw.AddCircleFilled(centre + new Vector2(0, radius * 0.45f), dot, c);
        }
        else
        {
            draw.AddCircleFilled(centre + new Vector2(0, -radius * 0.45f), dot, c);
            draw.AddLine(centre + new Vector2(0, -radius * 0.12f), centre + new Vector2(0, radius * 0.5f), c, t);
        }
    }

    /// <summary>
    /// Grid-unit drawing on one draw list. A struct with explicit methods rather than
    /// lambdas over point arrays: this runs every frame at the display's rate, and the
    /// frame loop is kept free of garbage.
    /// </summary>
    private readonly struct Pen(ImDrawListPtr draw, Vector2 origin, float unit, uint colour)
    {
        private float Thickness => MathF.Max(1f, 1.2f * unit);

        private Vector2 P(float x, float y) => origin + new Vector2(x * unit, y * unit);

        public void Move(float x, float y) => draw.PathLineTo(P(x, y));
        public void To(float x, float y) => draw.PathLineTo(P(x, y));

        public void Stroke(bool closed = false) =>
            draw.PathStroke(colour, closed ? ImDrawFlags.Closed : ImDrawFlags.None, Thickness);

        public void Line(float x1, float y1, float x2, float y2) =>
            draw.AddLine(P(x1, y1), P(x2, y2), colour, Thickness);

        public void Line3(float x1, float y1, float x2, float y2, float x3, float y3)
        {
            Move(x1, y1); To(x2, y2); To(x3, y3);
            Stroke();
        }

        public void Dot(float x, float y, float r) =>
            draw.AddCircleFilled(P(x, y), MathF.Max(1f, r * unit), colour);

        public void Rect(float x1, float y1, float x2, float y2, float rounding) =>
            draw.AddRect(P(x1, y1), P(x2, y2), colour, rounding * unit, ImDrawFlags.None, Thickness);

        public void Arc(float cx, float cy, float r, float from, float to)
        {
            draw.PathArcTo(P(cx, cy), r * unit, from, to);
            Stroke();
        }

        public void ArcTo(float cx, float cy, float r, float from, float to) =>
            draw.PathArcTo(P(cx, cy), r * unit, from, to);

        public void Speaker()
        {
            Move(2.5f, 6); To(4.8f, 6); To(8, 3.3f); To(8, 12.7f); To(4.8f, 10); To(2.5f, 10);
            Stroke(closed: true);
        }
    }
}
