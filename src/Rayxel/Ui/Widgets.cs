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
    public static bool Combo(string id, string preview, IReadOnlyList<string> options, int current, out int chosen,
                             float width = 0f, bool enabled = true)
    {
        var items = new ComboItem[options.Count];
        for (int i = 0; i < items.Length; i++) items[i] = new(options[i]);
        return Combo(id, preview, items, current, out chosen, width, enabled);
    }

    /// <summary>
    /// One row of a combo box's list: its name, an optional quiet note at the right end,
    /// whether it can be chosen, and what there is to say about it — shown beside the list
    /// while the pointer or the arrow keys are on the row. A row that cannot be chosen keeps
    /// its place, greyed, with a mark that says it has a reason to read.
    /// </summary>
    public readonly record struct ComboItem(string Text, string Trailing = "", bool Enabled = true, string? Info = null);

    // The list that is open, if one is: which combo box it belongs to, the row the arrow
    // keys have reached, and the frame it was last drawn in — a list whose flyout went away
    // is never told it closed, so "open" has to mean "drawn just now".
    private static string? _openCombo;
    private static int _comboCursor = -1, _comboFrame = -1;
    private static bool _comboCloseAsked;

    /// <summary>True while a combo box's list is dropped open.</summary>
    public static bool ComboOpen => _openCombo is not null && ImGui.GetFrameCount() - _comboFrame <= 1;

    /// <summary>
    /// Closes the open list, as Esc should: the innermost thing that is open is the list,
    /// not the flyout it belongs to.
    /// </summary>
    public static void CloseCombo() => _comboCloseAsked = true;

    /// <summary>
    /// The same combo box over rows that carry more than a name. The list is as wide as
    /// the box and a row is one line, as Fluent's is.
    /// <para>
    /// A row that has something to say about itself says it in a tooltip standing beside
    /// the list, level with the row: under the pointer, or at the row the arrow keys have
    /// reached. Up and Down walk the rows — every row, so one that cannot be chosen can
    /// still be read — Enter takes the one they are on, and Esc closes the list.
    /// <paramref name="openAt"/> drops the list open with the keys already on that row.
    /// </para>
    /// </summary>
    public static bool Combo(string id, string preview, ReadOnlySpan<ComboItem> options, int current, out int chosen,
                             float width = 0f, bool enabled = true, int openAt = -1, Vector4? previewColour = null)
    {
        chosen = current;
        if (width <= 0) width = ImGui.GetContentRegionAvail().X;
        var size = new Vector2(width, Layout.S(32));
        string popup = id + "##list";

        bool opened = ComboFace(id, preview, size, enabled, out var min, previewColour) || openAt >= 0;
        if (opened)
        {
            ImGui.OpenPopup(popup);
            _openCombo = id;
            _comboCursor = Math.Min(openAt, options.Length - 1);
        }

        float rowH = Layout.S(36), padY = Layout.S(4);
        float listH = Math.Min(rowH * options.Length + padY * 2, Layout.S(360));
        var display = ImGui.GetIO().DisplaySize;
        bool above = min.Y + size.Y + Layout.S(4) + listH > display.Y - Layout.S(8);

        ImGui.SetNextWindowPos(above ? new Vector2(min.X, min.Y - Layout.S(4)) : new Vector2(min.X, min.Y + size.Y + Layout.S(4)),
                               ImGuiCond.Always, above ? new Vector2(0, 1) : Vector2.Zero);
        ImGui.SetNextWindowSize(new Vector2(width, listH));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Layout.S(4), padY));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);

        bool changed = false;
        if (ImGui.BeginPopup(popup))
        {
            if (_openCombo != id) { _openCombo = id; _comboCursor = -1; }
            _comboFrame = ImGui.GetFrameCount();

            if (_comboCloseAsked) ImGui.CloseCurrentPopup();

            // The pointer and the keys take turns: moving the mouse hands the rows back to
            // it, an arrow key takes them again from wherever the selection is.
            if (!opened && ImGui.GetIO().MouseDelta != Vector2.Zero) _comboCursor = -1;
            int step = (ImGui.IsKeyPressed(ImGuiKey.DownArrow) ? 1 : 0) - (ImGui.IsKeyPressed(ImGuiKey.UpArrow) ? 1 : 0);
            if (step != 0 && options.Length > 0)
                _comboCursor = Math.Clamp((_comboCursor >= 0 ? _comboCursor : current) + step, 0, options.Length - 1);

            if (_comboCursor >= 0 && _comboCursor < options.Length && ImGui.IsKeyPressed(ImGuiKey.Enter))
            {
                if (options[_comboCursor].Enabled)
                {
                    changed = _comboCursor != current;
                    chosen = _comboCursor;
                    ImGui.CloseCurrentPopup();
                }
            }

            var draw = ImGui.GetWindowDrawList();
            float inset = Layout.S(12), gap = Layout.S(8), glyph = Layout.S(16), r = Layout.S(Theme.RadiusControl);
            var listMin = ImGui.GetWindowPos();
            float listRight = listMin.X + ImGui.GetWindowSize().X;

            for (int i = 0; i < options.Length; i++)
            {
                var option = options[i];
                if (ListRow($"{id}#{i}", rowH, i == current, option.Enabled, out var rowMin, out var rowMax) && i != current)
                {
                    chosen = i;
                    changed = true;
                    ImGui.CloseCurrentPopup();
                }
                bool hovered = ImGui.IsItemHovered();
                bool reached = i == _comboCursor;
                float cy = (rowMin.Y + rowMax.Y) / 2f, right = rowMax.X - inset;

                // Where the keys are: the fill a pointer would give it, and Fluent's focus
                // stroke, since there is no pointer there to explain the fill.
                if (reached)
                {
                    if (!hovered) draw.AddRectFilled(rowMin, rowMax, Theme.U32(Theme.SubtleHover), r);
                    draw.AddRect(rowMin, rowMax, Theme.U32(Theme.ControlStrong), r);
                }

                // A row that cannot be chosen is marked as having a reason to read.
                if (!option.Enabled && option.Info is not null)
                {
                    Icons.Draw(draw, Icon.Info, new Vector2(right - glyph, cy - glyph / 2f), glyph, Theme.Caution);
                    right -= glyph + gap;
                }

                if (option.Trailing.Length > 0)
                {
                    var note = Measure(F.Caption, option.Trailing);
                    DrawText(draw, F.Caption, new Vector2(right - note.X, cy - note.Y / 2f),
                             option.Enabled ? Theme.TextSecondary : Theme.TextDisabled, option.Trailing);
                    right -= note.X + gap;
                }

                var t = Measure(F.Body, option.Text);
                draw.PushClipRect(rowMin, new Vector2(right, rowMax.Y), true);
                DrawText(draw, F.Body, new Vector2(rowMin.X + inset, cy - t.Y / 2f),
                         option.Enabled ? Theme.TextPrimary : Theme.TextDisabled, option.Text);
                draw.PopClipRect();

                // What the row says about itself, beside the list and level with the row. The
                // keys' row when they have one, otherwise the pointer's — never both.
                bool speaks = _comboCursor >= 0 ? reached : hovered;
                if (speaks && option.Info is { Length: > 0 } info)
                {
                    // To the right when there is room for it, and growing upwards from a
                    // row low in the window, so it never runs off an edge: its height is
                    // not known until it has been drawn.
                    float room = Layout.S(320) + Layout.S(24);
                    bool toLeft = listRight + Layout.S(6) + room > display.X;
                    bool upwards = rowMax.Y > display.Y * 0.6f;
                    ImGui.SetNextWindowPos(
                        new Vector2(toLeft ? listMin.X - Layout.S(6) : listRight + Layout.S(6), upwards ? rowMax.Y : rowMin.Y),
                        ImGuiCond.Always, new Vector2(toLeft ? 1f : 0f, upwards ? 1f : 0f));
                    ShowTooltip(info);
                }
            }
            ImGui.EndPopup();
        }
        else if (_openCombo == id) _openCombo = null;

        // Asked for by Esc between frames; spent whether or not a list was there to take it.
        if (_openCombo is null || _openCombo == id) _comboCloseAsked = false;

        ImGui.PopStyleVar(2);
        return changed;
    }

    /// <summary>
    /// Fluent's info button: a small mark beside a label, with what there is to say about
    /// it in a tooltip. The tooltip stands on the button rather than following the pointer,
    /// so it reads as belonging to that label. <paramref name="warning"/> colours the mark
    /// when the thing to read is the reason something is not as asked.
    /// </summary>
    public static void InfoButton(string id, string text, bool warning = false, bool shown = false)
    {
        float side = Layout.S(20), glyph = Layout.S(16);
        var min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, new Vector2(side));
        bool hovered = ImGui.IsItemHovered();

        Icons.Draw(ImGui.GetWindowDrawList(), Icon.Info, min + new Vector2((side - glyph) / 2f), glyph,
                   warning ? Theme.Caution : hovered ? Theme.TextPrimary : Theme.TextSecondary);
        if (!hovered && !shown) return;

        ImGui.SetNextWindowPos(new Vector2(min.X, min.Y - Layout.S(6)), ImGuiCond.Always, new Vector2(0f, 1f));
        ShowTooltip(text);
    }

    /// <summary>
    /// A combo box's closed face alone — the value and the chevron — for a caller that
    /// opens a list of its own under it. True when it was clicked. Disabled, it keeps its
    /// value in view and takes no clicks; saying why is the caller's business.
    /// </summary>
    public static bool ComboFace(string id, string preview, Vector2 size, bool enabled, out Vector2 min,
                                 Vector4? previewColour = null)
    {
        min = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton(id, size) && enabled;
        bool hovered = ImGui.IsItemHovered() && enabled;
        bool held = ImGui.IsItemActive() && enabled;

        var draw = ImGui.GetWindowDrawList();
        float r = Layout.S(Theme.RadiusControl);
        var fill = !enabled || held ? Theme.ControlFillPressed : hovered ? Theme.ControlFillHover : Theme.ControlFill;
        draw.AddRectFilled(min, min + size, Theme.U32(fill), r);
        draw.AddRect(min, min + size, Theme.U32(Theme.ControlStroke), r);

        float pad = Layout.S(11), chevron = Layout.S(12);
        var textSize = Measure(F.Body, preview);
        draw.PushClipRect(min, min + new Vector2(size.X - pad - chevron - Layout.S(4), size.Y), true);
        DrawText(draw, F.Body, new Vector2(min.X + pad, min.Y + (size.Y - textSize.Y) / 2f),
                 !enabled ? Theme.TextDisabled : previewColour ?? Theme.TextPrimary, preview);
        draw.PopClipRect();
        Icons.Draw(draw, Icon.ChevronDown, new Vector2(min.X + size.X - pad - chevron, min.Y + (size.Y - chevron) / 2f),
                   chevron, enabled ? Theme.TextSecondary : Theme.TextDisabled);
        return clicked;
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
    /// Parts of a whole as segments filling a 6px track end to end: what a delay is made
    /// of. A part too small to draw as a segment is left out rather than drawn as a dot.
    /// </summary>
    public static void StackedBar(ReadOnlySpan<double> values, ReadOnlySpan<Vector4> colours, double total, float height = 6f)
    {
        height = Layout.S(height);
        float width = ImGui.GetContentRegionAvail().X;
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();

        draw.AddRectFilled(pos, pos + new Vector2(width, height), Theme.U32(Theme.Alpha(Theme.TextPrimary, 0.12f)), height / 2f);

        if (total > 0)
        {
            float gap = Layout.S(2), x = pos.X, right = pos.X + width;
            for (int i = 0; i < values.Length && x < right; i++)
            {
                float w = Math.Min((float)(values[i] / total * width), right - x);
                if (w < 1.5f + gap) continue;
                draw.AddRectFilled(new Vector2(x, pos.Y), new Vector2(x + w - gap, pos.Y + height), Theme.U32(colours[i]), height / 2f);
                x += w;
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
