using System.Numerics;
using ImGuiNET;

namespace Rayxel.Ui;

/// <summary>
/// The Fluent controls, drawn onto the draw list.
/// <para>
/// ImGui's own widgets carry its look in their proportions, not only in their colours, so
/// the ones the interface shows — switch, slider, combo box, buttons, list rows — are
/// built here from an invisible button and a few shapes. That is also what keeps them
/// exactly the size the Fluent spec gives them at every zoom.
/// </para>
/// </summary>
public static class Widgets
{
    /// <summary>The ramp for this frame. Set once by the shell, so no control has to be handed it.</summary>
    public static Fonts F { get; private set; } = null!;

    public static void Use(Fonts fonts) => F = fonts;

    // ── text ─────────────────────────────────────────────────────────────────────

    public static void Text(ImFontPtr font, Vector4 colour, string text)
    {
        bool pushed = Push(font);
        ImGui.TextColored(colour, text);
        if (pushed) ImGui.PopFont();
    }

    /// <summary>Text that wraps at the right edge of the current window or column.</summary>
    public static void Wrapped(ImFontPtr font, Vector4 colour, string text)
    {
        ImGui.PushTextWrapPos(0f);
        Text(font, colour, text);
        ImGui.PopTextWrapPos();
    }

    public static void DrawText(ImDrawListPtr draw, ImFontPtr font, Vector2 at, Vector4 colour, string text)
    {
        bool pushed = Push(font);
        draw.AddText(at, Theme.U32(colour), text);
        if (pushed) ImGui.PopFont();
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
    /// Several runs of text on one line, sitting on a common baseline. ImGui lays items out
    /// from the top of the line, so mixing sizes with SameLine leaves the smaller ones
    /// riding high like superscripts. Drawn directly and claimed with one Dummy.
    /// </summary>
    public static void BaselineRow(params (ImFontPtr Font, Vector4 Colour, string Text, float Gap)[] parts)
    {
        if (parts.Length == 0) return;

        var origin = ImGui.GetCursorScreenPos();
        Span<Vector2> sizes = stackalloc Vector2[parts.Length];
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
            DrawText(draw, parts[i].Font, new Vector2(x, origin.Y + (tallest - sizes[i].Y)), parts[i].Colour, parts[i].Text);
            x += sizes[i].X;
        }

        ImGui.Dummy(new Vector2(width, tallest));
    }

    /// <summary>
    /// The right edge of the content area in window-local coordinates, which is what
    /// SameLine and PushTextWrapPos take. Valid at the start of a line — after an item,
    /// before SameLine has moved the cursor — which is everywhere it is called.
    /// </summary>
    public static float ContentRight() => ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;

    /// <summary>Places the next item so that <paramref name="width"/> pixels end at the content's right edge.</summary>
    public static void RightAlign(float width) =>
        ImGui.SameLine(Math.Max(0, ContentRight() - width));

    // ── rules ────────────────────────────────────────────────────────────────────

    /// <summary>A full-width 1px rule with <paramref name="padY"/> above and below.</summary>
    public static void Divider(float padY = 12f)
    {
        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        float pad = Math.Max(0, Layout.S(padY) - spacing);
        ImGui.Dummy(new Vector2(0, pad));
        var pos = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X;
        ImGui.GetWindowDrawList().AddLine(pos, pos + new Vector2(width, 0), Theme.U32(Theme.Divider));
        ImGui.Dummy(new Vector2(0, Math.Max(0, pad - spacing)));
    }

    /// <summary>The bar's group separator: a 1px stroke, 24 tall, centred on the line.</summary>
    public static void VerticalSeparator(float lineHeight)
    {
        ImGui.SameLine(0, Layout.S(4));
        var pos = ImGui.GetCursorScreenPos();
        float h = Layout.S(24);
        float y = pos.Y + (lineHeight - h) / 2f;
        ImGui.GetWindowDrawList().AddLine(new Vector2(pos.X, y), new Vector2(pos.X, y + h), Theme.U32(Theme.Divider));
        ImGui.Dummy(new Vector2(1, lineHeight));
        ImGui.SameLine(0, Layout.S(4));
    }

    // ── buttons ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The transparent button the bar is made of: nothing at rest, a faint fill on hover,
    /// fainter while pressed, and the hover fill held while its flyout is open.
    /// </summary>
    public static bool SubtleButton(string id, Vector2 size, bool active, out Vector2 min)
    {
        min = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton(id, size);
        bool hovered = ImGui.IsItemHovered();
        bool held = ImGui.IsItemActive();

        var fill = held ? Theme.SubtlePressed : hovered || active ? Theme.SubtleHover : default;
        if (fill.W > 0)
            ImGui.GetWindowDrawList().AddRectFilled(min, min + size, Theme.U32(fill), Layout.S(Theme.RadiusControl));
        return clicked;
    }

    /// <summary>A 36px square subtle button with a 16px icon in the middle.</summary>
    public static bool IconButton(string id, Icon icon, bool active, Vector4 colour, string tooltip)
    {
        bool clicked = IconButtonSized(id, icon, Layout.S(36), colour, active);
        Tooltip(tooltip);
        return clicked;
    }

    public static bool IconButtonSized(string id, Icon icon, float side, Vector4 colour, bool active = false)
    {
        float glyph = Layout.S(16);
        bool clicked = SubtleButton(id, new Vector2(side), active, out var min);
        Icons.Draw(ImGui.GetWindowDrawList(), icon, min + new Vector2((side - glyph) / 2f), glyph, colour);
        return clicked;
    }

    /// <summary>
    /// Fluent's standard button, or the accent one. One accent button per surface at most;
    /// it is the action the surface exists for.
    /// </summary>
    public static bool Button(string label, Vector2 size, bool accent = false, bool enabled = true)
    {
        var min = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton(label, size) && enabled;
        bool hovered = ImGui.IsItemHovered() && enabled;
        bool held = ImGui.IsItemActive() && enabled;

        var draw = ImGui.GetWindowDrawList();
        float r = Layout.S(Theme.RadiusControl);

        Vector4 fill, text;
        if (accent)
        {
            fill = !enabled ? Theme.Alpha(Theme.TextPrimary, 0.16f)
                 : held ? Theme.Alpha(Theme.Accent, 0.8f)
                 : hovered ? Theme.Alpha(Theme.Accent, 0.9f)
                 : Theme.Accent;
            text = enabled ? Theme.TextOnAccent : Theme.TextDisabled;
        }
        else
        {
            fill = held ? Theme.ControlFillPressed : hovered ? Theme.ControlFillHover : Theme.ControlFill;
            text = !enabled ? Theme.TextDisabled : held ? Theme.TextSecondary : Theme.TextPrimary;
        }

        draw.AddRectFilled(min, min + size, Theme.U32(fill), r);
        if (!accent) draw.AddRect(min, min + size, Theme.U32(Theme.ControlStroke), r);

        var measured = Measure(F.Body, label);
        DrawText(draw, F.Body, min + (size - measured) * 0.5f, text, label);
        return clicked;
    }

    /// <summary>Shows <paramref name="text"/> as a tooltip for the last item, after the usual hover delay.</summary>
    public static void Tooltip(string text)
    {
        if (text.Length == 0 || !ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip)) return;
        ShowTooltip(text);
    }

    public static void ShowTooltip(string text)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Layout.S(8), Layout.S(6)));
        ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, Layout.S(Theme.RadiusControl));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Theme.Surface);
        if (ImGui.BeginTooltip())
        {
            ImGui.PushTextWrapPos(Layout.S(320));
            Text(F.Caption, Theme.TextPrimary, text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }

    // ── switch and slider ────────────────────────────────────────────────────────

    /// <summary>
    /// Fluent's toggle switch, 40×20. Off is an outline with a pale knob, on is the accent
    /// with a dark one; the knob grows by a pixel on each side under the pointer.
    /// </summary>
    public static bool ToggleSwitch(string id, ref bool value, bool enabled = true)
    {
        var pos = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton(id, SwitchSize) && enabled;
        if (clicked) value = !value;
        DrawSwitch(ImGui.GetWindowDrawList(), pos, value, enabled,
                   ImGui.IsItemHovered() && enabled, ImGui.IsItemActive() && enabled);
        return clicked;
    }

    public static Vector2 SwitchSize => new(Layout.S(40), Layout.S(20));

    /// <summary>The switch's pixels alone, for a caller whose hit area is larger than the track.</summary>
    public static void DrawSwitch(ImDrawListPtr draw, Vector2 pos, bool value, bool enabled, bool hovered, bool held)
    {
        var size = SwitchSize;
        float r = size.Y / 2f;
        var centreY = pos.Y + r;

        float knob = Layout.S(hovered || held ? 7f : 6f);
        float travel = size.X - r * 2f;

        if (value && enabled)
        {
            draw.AddRectFilled(pos, pos + size, Theme.U32(hovered ? Theme.Alpha(Theme.Accent, 0.9f) : Theme.Accent), r);
            draw.AddCircleFilled(new Vector2(pos.X + r + travel, centreY), knob, Theme.U32(Theme.TextOnAccent));
        }
        else if (value)
        {
            draw.AddRectFilled(pos, pos + size, Theme.U32(Theme.Alpha(Theme.TextPrimary, 0.16f)), r);
            draw.AddCircleFilled(new Vector2(pos.X + r + travel, centreY), knob, Theme.U32(Theme.TextDisabled));
        }
        else
        {
            if (hovered) draw.AddRectFilled(pos, pos + size, Theme.U32(Theme.SubtleHover), r);
            draw.AddRect(pos, pos + size, Theme.U32(enabled ? Theme.ControlStrong : Theme.Alpha(Theme.TextPrimary, 0.16f)), r);
            draw.AddCircleFilled(new Vector2(pos.X + r, centreY), knob,
                Theme.U32(enabled ? Theme.TextSecondary : Theme.TextDisabled));
        }
    }

    /// <summary>
    /// A setting's name on the left and its switch on the right, both centred on one row —
    /// the shape every on/off setting in Windows takes.
    /// </summary>
    public static bool LabelSwitch(string label, string id, ref bool value, bool enabled = true)
    {
        var pos = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X;
        float row = Layout.S(32);
        var size = SwitchSize;

        var text = Measure(F.Body, label);
        DrawText(ImGui.GetWindowDrawList(), F.Body, new Vector2(pos.X, pos.Y + (row - text.Y) / 2f),
                 enabled ? Theme.TextPrimary : Theme.TextDisabled, label);

        ImGui.SetCursorScreenPos(new Vector2(pos.X + width - size.X, pos.Y + (row - size.Y) / 2f));
        bool clicked = ToggleSwitch(id, ref value, enabled);
        ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y));
        ImGui.Dummy(new Vector2(width, row));
        return clicked;
    }

    /// <summary>A label on the left and a value right-aligned on the same line, as a slider's header.</summary>
    public static void LabelValue(string label, string value, Vector4 valueColour, bool enabled = true) =>
        LabelValueWidth(label, value, valueColour, ImGui.GetContentRegionAvail().X, enabled);

    public static void LabelValueWidth(string label, string value, Vector4 valueColour, float width, bool enabled = true)
    {
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();

        var l = Measure(F.Body, label);
        var v = Measure(F.Body, value);
        DrawText(draw, F.Body, pos, enabled ? Theme.TextPrimary : Theme.TextDisabled, label);
        DrawText(draw, F.Body, new Vector2(pos.X + width - v.X, pos.Y), enabled ? valueColour : Theme.TextDisabled, value);
        ImGui.Dummy(new Vector2(width, Math.Max(l.Y, v.Y)));
    }

    /// <summary>
    /// Fluent's slider: a 4px track filled with the accent up to the value, and a 20px
    /// thumb whose accent centre swells under the pointer and shrinks while dragged.
    /// </summary>
    public static bool Slider(string id, ref float value, float min, float max, bool enabled = true, float width = 0f)
    {
        if (width <= 0) width = ImGui.GetContentRegionAvail().X;
        float thumb = Layout.S(20);
        var pos = ImGui.GetCursorScreenPos();

        ImGui.InvisibleButton(id, new Vector2(width, thumb));
        bool hovered = ImGui.IsItemHovered() && enabled;
        bool held = ImGui.IsItemActive() && enabled;

        // The thumb's centre travels between half a thumb from either end, so it never
        // hangs over the edge of whatever the slider sits in.
        float left = pos.X + thumb / 2f, right = pos.X + width - thumb / 2f;
        bool changed = false;
        if (held && right > left)
        {
            float t = Math.Clamp((ImGui.GetIO().MousePos.X - left) / (right - left), 0f, 1f);
            float next = min + t * (max - min);
            if (Math.Abs(next - value) > float.Epsilon) { value = next; changed = true; }
        }

        float frac = max > min ? Math.Clamp((value - min) / (max - min), 0f, 1f) : 0f;
        float x = left + frac * (right - left);
        float cy = pos.Y + thumb / 2f;
        float track = Layout.S(4);

        var draw = ImGui.GetWindowDrawList();
        var fill = enabled ? Theme.Accent : Theme.TextDisabled;
        draw.AddRectFilled(new Vector2(left, cy - track / 2f), new Vector2(right, cy + track / 2f),
            Theme.U32(enabled ? Theme.ControlStrong : Theme.Alpha(Theme.TextPrimary, 0.16f)), track / 2f);
        draw.AddRectFilled(new Vector2(left, cy - track / 2f), new Vector2(x, cy + track / 2f), Theme.U32(fill), track / 2f);

        draw.AddCircleFilled(new Vector2(x, cy), thumb / 2f, Theme.U32(new Vector4(0.271f, 0.271f, 0.271f, 1f)));
        draw.AddCircle(new Vector2(x, cy), thumb / 2f, Theme.U32(Theme.ControlStroke));
        float inner = Layout.S(held ? 5f : hovered ? 7f : 6f);
        draw.AddCircleFilled(new Vector2(x, cy), inner, Theme.U32(fill));

        return changed;
    }

    // ── combo box and list rows ──────────────────────────────────────────────────

    /// <summary>
    /// Fluent's combo box: a 32px control with the value and a chevron, opening a list of
    /// rows under it — or over it, when there is no room below, which there usually is
    /// not in a flyout that sits on the bottom edge.
    /// </summary>
    public static bool Combo(string id, string preview, IReadOnlyList<string> options, int current, out int chosen)
    {
        chosen = current;
        float width = ImGui.GetContentRegionAvail().X;
        var size = new Vector2(width, Layout.S(32));
        var min = ImGui.GetCursorScreenPos();

        bool clicked = ImGui.InvisibleButton(id, size);
        bool hovered = ImGui.IsItemHovered();
        bool held = ImGui.IsItemActive();
        if (clicked) ImGui.OpenPopup(id + "##list");

        var draw = ImGui.GetWindowDrawList();
        float r = Layout.S(Theme.RadiusControl);
        draw.AddRectFilled(min, min + size, Theme.U32(held ? Theme.ControlFillPressed : hovered ? Theme.ControlFillHover : Theme.ControlFill), r);
        draw.AddRect(min, min + size, Theme.U32(Theme.ControlStroke), r);

        float pad = Layout.S(11), chevron = Layout.S(12);
        var textSize = Measure(F.Body, preview);
        draw.PushClipRect(min, min + new Vector2(width - pad - chevron - Layout.S(4), size.Y), true);
        DrawText(draw, F.Body, new Vector2(min.X + pad, min.Y + (size.Y - textSize.Y) / 2f), Theme.TextPrimary, preview);
        draw.PopClipRect();
        Icons.Draw(draw, Icon.ChevronDown, new Vector2(min.X + width - pad - chevron, min.Y + (size.Y - chevron) / 2f),
                   chevron, Theme.TextSecondary);

        float rowH = Layout.S(36), padY = Layout.S(4);
        float listH = Math.Min(rowH * options.Count + padY * 2, Layout.S(360));
        float display = ImGui.GetIO().DisplaySize.Y;
        bool above = min.Y + size.Y + Layout.S(4) + listH > display - Layout.S(8);

        ImGui.SetNextWindowPos(above ? new Vector2(min.X, min.Y - Layout.S(4)) : new Vector2(min.X, min.Y + size.Y + Layout.S(4)),
                               ImGuiCond.Always, above ? new Vector2(0, 1) : Vector2.Zero);
        ImGui.SetNextWindowSize(new Vector2(width, listH));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Layout.S(4), padY));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);

        bool changed = false;
        if (ImGui.BeginPopup(id + "##list"))
        {
            for (int i = 0; i < options.Count; i++)
            {
                if (ListRow($"{id}#{i}", rowH, i == current, true, out var rowMin, out var rowMax) && i != current)
                {
                    chosen = i;
                    changed = true;
                    ImGui.CloseCurrentPopup();
                }
                var t = Measure(F.Body, options[i]);
                DrawText(ImGui.GetWindowDrawList(), F.Body,
                         new Vector2(rowMin.X + Layout.S(12), rowMin.Y + (rowMax.Y - rowMin.Y - t.Y) / 2f),
                         Theme.TextPrimary, options[i]);
            }
            ImGui.EndPopup();
        }

        ImGui.PopStyleVar(2);
        return changed;
    }

    /// <summary>
    /// A full-width list row: transparent at rest, a faint fill under the pointer, and when
    /// selected that fill plus the accent pill on the left edge — Fluent's list selection.
    /// The caller draws the content into the returned rectangle.
    /// </summary>
    public static bool ListRow(string id, float height, bool selected, bool enabled, out Vector2 min, out Vector2 max)
    {
        float width = ImGui.GetContentRegionAvail().X;
        min = ImGui.GetCursorScreenPos();
        max = min + new Vector2(width, height);

        bool clicked = ImGui.InvisibleButton(id, new Vector2(width, height)) && enabled;
        bool hovered = ImGui.IsItemHovered() && enabled;
        bool held = ImGui.IsItemActive() && enabled;

        var draw = ImGui.GetWindowDrawList();
        var fill = held ? Theme.SubtlePressed : selected || hovered ? Theme.SubtleHover : default;
        if (fill.W > 0) draw.AddRectFilled(min, max, Theme.U32(fill), Layout.S(Theme.RadiusControl));

        if (selected)
        {
            float pill = Layout.S(16), w = Layout.S(3);
            float y = min.Y + (height - pill) / 2f;
            draw.AddRectFilled(new Vector2(min.X, y), new Vector2(min.X + w, y + pill), Theme.U32(Theme.Accent), w / 2f);
        }

        return clicked;
    }

    // ── measurement graphics ─────────────────────────────────────────────────────

    /// <summary>
    /// GPU, CPU and headroom against the frame budget, as segments on a 6px track. Over
    /// budget is drawn as a full bar of cost, which is the honest picture: there is no
    /// headroom.
    /// </summary>
    public static void BudgetBar(double gpuMs, double cpuMs, double budgetMs, float height = 6f)
    {
        height = Layout.S(height);
        float width = ImGui.GetContentRegionAvail().X;
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();

        draw.AddRectFilled(pos, pos + new Vector2(width, height), Theme.U32(Theme.Alpha(Theme.TextPrimary, 0.12f)), height / 2f);

        if (budgetMs > 0)
        {
            float x = pos.X;
            float gap = Layout.S(2);
            Segment(gpuMs, gpuMs + cpuMs > budgetMs ? Theme.Critical : Theme.Accent);
            Segment(cpuMs, new Vector4(0.54f, 0.54f, 0.54f, 1f));

            void Segment(double ms, Vector4 colour)
            {
                float w = (float)Math.Min(ms / budgetMs * width, pos.X + width - x);
                if (w < 1.5f) return;
                draw.AddRectFilled(new Vector2(x, pos.Y), new Vector2(x + w, pos.Y + height), Theme.U32(colour), height / 2f);
                x += w + gap;
            }
        }

        ImGui.Dummy(new Vector2(width, height));
    }

    /// <summary>
    /// Frame time against a dashed budget line. Not ImGui.PlotLines: the budget line is
    /// the point, and PlotLines cannot draw it.
    /// </summary>
    public static void FrameGraph(ReadOnlySpan<float> samples, double budgetMs, float height = 44f)
    {
        height = Layout.S(height);
        float width = ImGui.GetContentRegionAvail().X;
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();

        float scale = (float)Math.Max(budgetMs * 1.25, 0.001);
        float budgetY = pos.Y + height * (1f - (float)budgetMs / scale);

        // Dashed, because a solid line reads as data rather than as a threshold.
        for (float x = pos.X; x < pos.X + width; x += Layout.S(8))
            draw.AddLine(new Vector2(x, budgetY), new Vector2(Math.Min(x + Layout.S(3), pos.X + width), budgetY),
                         Theme.U32(Theme.TextTertiary), 1f);

        if (samples.Length >= 2)
        {
            float step = width / (samples.Length - 1);
            Span<Vector2> points = stackalloc Vector2[samples.Length];
            for (int i = 0; i < samples.Length; i++)
            {
                float v = Math.Clamp(samples[i] / scale, 0f, 1f);
                points[i] = new Vector2(pos.X + i * step, pos.Y + height * (1f - v));
            }
            for (int i = 0; i < samples.Length - 1; i++)
                draw.AddLine(points[i], points[i + 1], Theme.U32(Theme.Accent), Math.Max(1f, Layout.S(1.5f)));
        }

        ImGui.Dummy(new Vector2(width, height));
    }
}
