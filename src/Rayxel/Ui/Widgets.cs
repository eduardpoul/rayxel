using System.Numerics;
using ImGuiNET;

namespace Rayxel.Ui;

/// <summary>
/// The control anatomy from the design system, built once so the screens stay readable.
/// Anything ImGui does not have as a widget is drawn straight onto the draw list, which
/// is also what keeps the specified radii and 1px rules exact.
/// </summary>
public static class Widgets
{
    /// <summary>Draws text in a specific size from the type scale.</summary>
    public static void Text(ImFontPtr font, Vector4 colour, string text)
    {
        bool pushed = Push(font);
        ImGui.TextColored(colour, text);
        if (pushed) ImGui.PopFont();
    }

    public static void TextSameLine(ImFontPtr font, Vector4 colour, string text, float spacing = -1f)
    {
        ImGui.SameLine(0, spacing < 0 ? ImGui.GetStyle().ItemInnerSpacing.X : spacing);
        Text(font, colour, text);
    }

    /// <summary>
    /// Right edge of the content area in window-local coordinates, which is what
    /// <c>ImGui.SameLine(x)</c> wants. Call it at the start of a line, before SameLine has
    /// moved the cursor.
    /// </summary>
    public static float RightEdge() => ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - _cardInsetRight;

    /// <summary>
    /// Right padding of the card currently open. ImGui's content region only knows about
    /// the left indent, so without this a right-aligned value lands on the card's border
    /// instead of inside it — which is how the HDR switch ended up half off the panel.
    /// </summary>
    private static float _cardInsetRight;

    /// <summary>Places the next item so that <paramref name="width"/> pixels end at the right edge.</summary>
    public static void RightAlign(float width) => ImGui.SameLine(Math.Max(0, RightEdge() - width));

    /// <summary>
    /// Several runs of text on one line, sitting on a common baseline.
    /// <para>
    /// ImGui lays items out from the top of the line, so mixing font sizes with SameLine
    /// leaves the smaller ones riding high like a superscript — which is exactly how
    /// "120 fps" ended up floating above "1920x1080". Drawn directly and claimed with a
    /// single Dummy, so the window extent stays valid.
    /// </para>
    /// </summary>
    public static void BaselineRow(params (ImFontPtr Font, Vector4 Colour, string Text, float Gap)[] parts)
    {
        if (parts.Length == 0) return;

        var origin = ImGui.GetCursorScreenPos();
        var sizes = new Vector2[parts.Length];
        float tallest = 0, width = 0;

        for (int i = 0; i < parts.Length; i++)
        {
            sizes[i] = Measure(parts[i].Font, parts[i].Text);
            tallest = Math.Max(tallest, sizes[i].Y);
            width += sizes[i].X + (i > 0 ? Layout.S(parts[i].Gap) : 0);
        }

        var draw = ImGui.GetWindowDrawList();
        float x = origin.X;
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) x += Layout.S(parts[i].Gap);
            bool pushed = Push(parts[i].Font);
            draw.AddText(new Vector2(x, origin.Y + (tallest - sizes[i].Y)),
                         Theme.U32(parts[i].Colour), parts[i].Text);
            if (pushed) ImGui.PopFont();
            x += sizes[i].X;
        }

        ImGui.Dummy(new Vector2(width, tallest));
    }

    public static Vector2 Measure(ImFontPtr font, string text)
    {
        bool pushed = Push(font);
        var size = ImGui.CalcTextSize(text);
        if (pushed) ImGui.PopFont();
        return size;
    }

    private static bool Push(ImFontPtr font)
    {
        unsafe { if (font.NativePtr == null) return false; }
        ImGui.PushFont(font);
        return true;
    }

    /// <summary>
    /// A status pill: coloured text on a wash of the same colour. The wire tag is the
    /// most important one in the app — it is how a mode that lies about being
    /// uncompressed announces itself.
    /// </summary>
    public static void Pill(ImFontPtr font, string text, Vector4 colour, float bgAlpha = 0.14f)
    {
        var size = Measure(font, text);
        var pos = ImGui.GetCursorScreenPos();
        var padding = new Vector2(Layout.S(8), Layout.S(3));
        var box = size + padding * 2;

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + box, Theme.U32(Theme.Alpha(colour, bgAlpha)), Theme.RadiusPill);

        bool pushed = Push(font);
        draw.AddText(pos + padding, Theme.U32(colour), text);
        if (pushed) ImGui.PopFont();

        ImGui.Dummy(box);
    }

    /// <summary>A 7px status dot with the glow the design gives it.</summary>
    public static void StatusDot(Vector4 colour, float designDiameter = 7f)
    {
        var pos = ImGui.GetCursorScreenPos();
        float diameter = Layout.S(designDiameter);
        float r = diameter / 2f;
        var centre = new Vector2(pos.X + r, pos.Y + ImGui.GetTextLineHeight() / 2f);

        var draw = ImGui.GetWindowDrawList();
        // Stands in for `box-shadow: 0 0 10px`: two faint rings rather than a real blur.
        draw.AddCircleFilled(centre, r * 2.6f, Theme.U32(Theme.Alpha(colour, 0.10f)));
        draw.AddCircleFilled(centre, r * 1.7f, Theme.U32(Theme.Alpha(colour, 0.18f)));
        draw.AddCircleFilled(centre, r, Theme.U32(colour));

        ImGui.Dummy(new Vector2(diameter, ImGui.GetTextLineHeight()));
    }

    /// <summary>A full-width 1px rule in the divider colour.</summary>
    public static void Divider(float padY = 4f)
    {
        padY = Layout.S(padY);
        ImGui.Dummy(new Vector2(0, padY));
        var pos = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X;
        ImGui.GetWindowDrawList().AddLine(pos, pos + new Vector2(width, 0), Theme.U32(Theme.Divider));
        ImGui.Dummy(new Vector2(0, padY));
    }

    /// <summary>
    /// The frame budget bar: GPU, CPU and headroom as proportional segments with a 2px
    /// gap, on a faint track. Segments that would round to nothing are dropped rather
    /// than drawn as slivers.
    /// </summary>
    public static void BudgetBar(double gpuMs, double cpuMs, double budgetMs, float height = 4f)
    {
        height = Layout.S(height);
        float width = ImGui.GetContentRegionAvail().X;
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();

        draw.AddRectFilled(pos, pos + new Vector2(width, height),
            Theme.U32(new Vector4(1, 1, 1, 0.06f)), Theme.RadiusPill);

        if (budgetMs > 0)
        {
            double headroom = Math.Max(0, budgetMs - gpuMs - cpuMs);
            // Over budget is drawn as a full bar of cost with no headroom, which is the
            // honest picture: there is none.
            (double ms, Vector4 colour)[] segments =
            [
                (gpuMs, Theme.Alpha(Theme.Warn, 0.8f)),
                (cpuMs, Theme.Alpha(Theme.TextPrimary, 0.35f)),
                (headroom, Theme.Alpha(Theme.Good, 0.8f))
            ];

            float x = pos.X;
            foreach (var (ms, colour) in segments)
            {
                float w = (float)(ms / budgetMs) * width;
                if (w < 1.5f) continue;
                draw.AddRectFilled(new Vector2(x, pos.Y), new Vector2(x + w - Layout.S(2), pos.Y + height),
                    Theme.U32(colour), Theme.RadiusPill);
                x += w;
            }
        }

        ImGui.Dummy(new Vector2(width, height));
    }

    /// <summary>One cell of the metric grid: label above, value and unit below.</summary>
    public static void Metric(Fonts fonts, string label, string value, string unit, Vector4? valueColour = null)
    {
        ImGui.BeginGroup();
        Text(fonts.Micro, Theme.Alpha(Theme.TextPrimary, 0.62f), label);
        Text(fonts.MonoValue, valueColour ?? Theme.TextPrimary, value);
        if (unit.Length > 0)
        {
            ImGui.SameLine(0, 3);
            Text(fonts.Micro, Theme.TextMuted, unit);
        }
        ImGui.EndGroup();
    }

    /// <summary>
    /// A rounded surface to group controls on. Returns the draw-list index so the caller
    /// can close it with <see cref="EndCard"/> once the height is known — ImGui does not
    /// know how tall a group is until it has been laid out.
    /// </summary>
    public static Vector2 BeginCard(Vector4 background, float paddingX = 11f, float paddingY = 9f)
    {
        paddingX = Layout.S(paddingX); paddingY = Layout.S(paddingY);
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1);
        ImGui.Dummy(new Vector2(0, Math.Max(0, paddingY - ImGui.GetStyle().ItemSpacing.Y)));

        // Indent both edges, so a right-aligned value lands inside the card rather than
        // on its border. Only the left is an Indent; the right comes off the item width.
        ImGui.Indent(paddingX);
        ImGui.PushItemWidth(-paddingX);
        _cardInsetRight = paddingX;
        return start;
    }

    public static void EndCard(Vector2 start, Vector4 background, float paddingX = 11f, float paddingY = 9f, float radius = Theme.RadiusCard)
    {
        paddingX = Layout.S(paddingX); paddingY = Layout.S(paddingY);
        _cardInsetRight = 0;
        ImGui.PopItemWidth();
        ImGui.Unindent(paddingX);
        ImGui.Dummy(new Vector2(0, Math.Max(0, paddingY - ImGui.GetStyle().ItemSpacing.Y)));

        // The card spans the content width exactly. Adding the padding back on here was a
        // bug: the padding is inside the card, not extra width around it, and the cards
        // spilled past the panel edge because of it.
        float width = ImGui.GetContentRegionAvail().X;
        var end = new Vector2(start.X + width, ImGui.GetCursorScreenPos().Y);

        var draw = ImGui.GetWindowDrawList();
        draw.ChannelsSetCurrent(0);
        draw.AddRectFilled(start, end, Theme.U32(background), Layout.S(radius));
        draw.ChannelsMerge();

        // A gap after every card, always. Without it two cards share an edge and read as
        // one block — which is how the audio card looked like part of the HDR card once
        // the brightness row disappeared and the HDR card shrank to a single line.
        ImGui.Dummy(new Vector2(0, Layout.S(5)));
    }

    /// <summary>
    /// Label on the left, value right-aligned on the same line. Used everywhere a card
    /// has a name and a number, which is most of them.
    /// </summary>
    public static void LabelValue(ImFontPtr labelFont, Vector4 labelColour, string label,
                                  ImFontPtr valueFont, Vector4 valueColour, string value,
                                  float rightInset = 0f)
    {
        Text(labelFont, labelColour, label);
        float w = Measure(valueFont, value).X;
        ImGui.SameLine(Math.Max(0, RightEdge() - w - rightInset));
        Text(valueFont, valueColour, value);
    }

    /// <summary>
    /// A radio row from the design: full-width, rounded, with a ring-and-dot on the left
    /// and a cost on the right. The note underneath is what the row will do, not what it
    /// is — inactive rows warn that they take the frame path over from the current one.
    /// </summary>
    /// <param name="scale">
    /// Shown immediately left of the cost, on the active row only. Empty otherwise — the
    /// design puts it there so the number that decides whether an upscaler does anything
    /// sits next to what it costs.
    /// </param>
    public static bool RadioRow(Fonts fonts, string label, string note, string cost,
                                bool selected, bool enabled = true,
                                string scale = "", Vector4? scaleColour = null)
    {
        float padX = Layout.S(11), padY = Layout.S(8), gap = Layout.S(9);
        float ringOuter = Layout.S(15) / 2f;

        // A note only earns its line when it says something. Repeating "this replaces the
        // current one" under every unselected row was three lines of the same sentence,
        // and it pushed everything else off the panel.
        bool hasNote = note.Length > 0;

        var start = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X;
        float labelH = Measure(fonts.Heading, label).Y;
        float height = labelH + padY * 2 + (hasNote ? Measure(fonts.Micro, note).Y + Layout.S(2) : 0);

        bool clicked = ImGui.InvisibleButton(label, new Vector2(width, height)) && enabled;
        bool hovered = ImGui.IsItemHovered() && enabled;

        var background = selected ? Theme.CardActive
                       : hovered ? Theme.CardHover
                       : Theme.RowInactive;

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + new Vector2(width, height), Theme.U32(background), Layout.S(Theme.RadiusRow));

        // A 15px ring at 1.5px with a 7px dot, per the control anatomy — all scaled.
        var ringCentre = new Vector2(start.X + padX + ringOuter, start.Y + height / 2f);
        draw.AddCircle(ringCentre, ringOuter, Theme.U32(selected ? Theme.Accent : new Vector4(1, 1, 1, 0.22f)),
                       0, Math.Max(1f, Layout.S(1.5f)));
        if (selected) draw.AddCircleFilled(ringCentre, Layout.S(7) / 2f, Theme.U32(Theme.Accent));

        float textX = start.X + padX + ringOuter * 2 + gap;
        var labelColour = !enabled ? Theme.Alpha(Theme.TextPrimary, 0.35f)
                        : selected ? Theme.AccentText : Theme.TextPrimary;

        bool pushed = Push(fonts.Heading);
        draw.AddText(new Vector2(textX, start.Y + padY), Theme.U32(labelColour), label);
        if (pushed) ImGui.PopFont();

        if (hasNote)
        {
            pushed = Push(fonts.Micro);
            var noteColour = !enabled ? Theme.Alpha(Theme.Warn, 0.5f) : Theme.TextMuted;
            draw.AddText(new Vector2(textX, start.Y + padY + labelH + Layout.S(2)), Theme.U32(noteColour), note);
            if (pushed) ImGui.PopFont();
        }

        float right = start.X + width - Layout.S(11);
        float costY = start.Y + (height - Measure(fonts.MonoCost, "0").Y) / 2f;

        if (cost.Length > 0)
        {
            var costSize = Measure(fonts.MonoCost, cost);
            right -= costSize.X;
            pushed = Push(fonts.MonoCost);
            draw.AddText(new Vector2(right, costY),
                         Theme.U32(selected ? Theme.AccentText : Theme.TextMuted), cost);
            if (pushed) ImGui.PopFont();
        }

        // The scale factor sits immediately left of the cost, on the active row only.
        if (scale.Length > 0)
        {
            var scaleSize = Measure(fonts.MonoCost, scale);
            right -= scaleSize.X + Layout.S(9);
            pushed = Push(fonts.MonoCost);
            draw.AddText(new Vector2(right, costY), Theme.U32(scaleColour ?? Theme.TextMuted), scale);
            if (pushed) ImGui.PopFont();
        }

        return clicked && !selected;
    }

    /// <summary>The 32x18 switch, with the knob travel the design specifies.</summary>
    public static bool Toggle(string id, ref bool value, bool enabled = true)
    {
        var pos = ImGui.GetCursorScreenPos();
        var size = new Vector2(Layout.S(36), Layout.S(20));

        bool clicked = ImGui.InvisibleButton(id, size) && enabled;
        if (clicked) value = !value;

        var track = !enabled ? new Vector4(1, 1, 1, 0.06f)
                  : value ? Theme.Alpha(Theme.Accent, 0.60f)
                  : new Vector4(1, 1, 1, 0.11f);

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + size, Theme.U32(track), size.Y / 2f);

        // `knob` is a radius. The previous version subtracted it from the track width as
        // though it were a diameter, so the "on" position pushed half the knob outside the
        // track — which is what made every switch look crooked.
        float inset = size.Y * 0.14f;
        float knob = size.Y / 2f - inset;
        float knobX = value ? pos.X + size.X - inset - knob : pos.X + inset + knob;
        draw.AddCircleFilled(new Vector2(knobX, pos.Y + size.Y / 2f),
            knob, Theme.U32(Theme.Alpha(Theme.TextPrimary, enabled ? 0.95f : 0.4f)));

        return clicked;
    }

    /// <summary>
    /// The frame-time graph: 70 samples against a dashed budget line. Deliberately not a
    /// ImGui.PlotLines — the budget line is the whole point, and PlotLines cannot draw it.
    /// </summary>
    public static void FrameGraph(ReadOnlySpan<float> samples, double budgetMs, float height = 44f)
    {
        height = Layout.S(height);
        float width = ImGui.GetContentRegionAvail().X;
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();

        float scale = (float)Math.Max(budgetMs * 1.25, 0.001);
        float budgetY = pos.Y + height * (1f - (float)budgetMs / scale);

        // Dashed, because a solid line here reads as data rather than as a threshold.
        for (float x = pos.X; x < pos.X + width; x += Layout.S(8))
            draw.AddLine(new Vector2(x, budgetY), new Vector2(Math.Min(x + Layout.S(3), pos.X + width), budgetY),
                         Theme.U32(Theme.Alpha(Theme.Bad, 0.45f)), 1f);

        if (samples.Length >= 2)
        {
            float step = width / (samples.Length - 1);
            Span<Vector2> points = stackalloc Vector2[samples.Length];
            for (int i = 0; i < samples.Length; i++)
            {
                float v = Math.Clamp(samples[i] / scale, 0f, 1f);
                points[i] = new Vector2(pos.X + i * step, pos.Y + height * (1f - v));
            }

            // Area fill first, as a strip of quads down to the baseline.
            uint fill = Theme.U32(Theme.Alpha(Theme.Accent, 0.10f));
            float baseline = pos.Y + height;
            for (int i = 0; i < samples.Length - 1; i++)
                draw.AddQuadFilled(points[i], points[i + 1],
                    new Vector2(points[i + 1].X, baseline), new Vector2(points[i].X, baseline), fill);

            for (int i = 0; i < samples.Length - 1; i++)
                draw.AddLine(points[i], points[i + 1], Theme.U32(Theme.Alpha(Theme.Accent, 0.8f)), Layout.S(1.4f));
        }

        ImGui.Dummy(new Vector2(width, height));
    }
}
