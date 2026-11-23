using System.Numerics;
using ImGuiNET;
using Rayxel.Capture;
using Rayxel.Native;
using Rayxel.Render;

namespace Rayxel.Ui;

/// <summary>
/// The command bar along the bottom edge, and everything that opens above it.
/// <para>
/// It replaces the full-height panel that used to sit down the right side. Someone is
/// playing a game through this window, so the controls have to be one narrow strip that
/// appears when the mouse moves and goes away five seconds after it stops. The two
/// choices made most often — capture mode and scaler — are dropdowns on the bar itself;
/// everything set once and left alone lives behind "…".
/// </para>
/// <para>
/// All of it is drawn into the swapchain like the rest of the overlay. A real Windows
/// flyout would be a second window over the video, and a window over the video takes it
/// off its overlay plane — a whole frame of latency, for showing a menu.
/// </para>
/// </summary>
public static class BottomBar
{
    // RTX Video HDR on top of RTX VSR, measured on this machine with --bench. What the
    // upscalers cost is with their descriptions, in AppModel.
    private const double HdrCostMs = 0.66;

    // This frame's layout, for anchoring what opens above the bar. The bar is always
    // drawn first, so these are current by the time a flyout reads them.
    private static Vector2 _barMin, _modeMin, _scalerMin, _frameGenMin, _moreMax;

    private const ImGuiWindowFlags SurfaceFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
        | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
        | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize
        | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoCollapse;

    public static void Draw(AppModel model)
    {
        float fade = model.BarFade;
        if (fade <= 0.001f) return;

        var display = new Vector2(model.Renderer.Width, model.Renderer.Height);
        // It rises a few pixels as it fades in, the way a Fluent surface enters.
        float slide = (1f - fade) * Layout.S(8);

        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, fade);
        bool hovered = Bar(model, display, slide);
        hovered |= model.Flyout switch
        {
            Flyout.Mode => ModeFlyout(model),
            Flyout.Scaler => ScalerFlyout(model),
            Flyout.FrameGen => FrameGenFlyout(model),
            Flyout.More => MoreFlyout(model),
            _ => false
        };
        ImGui.PopStyleVar();

        // Anything under the pointer, being dragged, or dropped open keeps the bar up.
        if (hovered || ImGui.IsAnyItemActive()
            || ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
            model.PanelActivity();

        // Light dismiss, as every Fluent flyout does: a click on anything that is not ours
        // closes it. The click still reaches the video, where a left click does nothing.
        //
        // AllowWhenBlockedByActiveItem is not optional here. The press that lands on a row
        // makes that row the active item in this very frame, and without the flag ImGui
        // then reports no window hovered at all — so the flyout closed on the press, its
        // row never saw the release, and no choice in any flyout ever took effect.
        if (model.Flyout != Flyout.None && ImGui.IsMouseClicked(ImGuiMouseButton.Left)
            && !ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow
                                      | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem
                                      | ImGuiHoveredFlags.AllowWhenBlockedByPopup))
            model.Flyout = Flyout.None;
    }

    // ── the bar ──────────────────────────────────────────────────────────────────

    private static bool Bar(AppModel model, Vector2 display, float slide)
    {
        ImGui.SetNextWindowPos(new Vector2(display.X / 2f, display.Y - Layout.S(24) + slide),
                               ImGuiCond.Always, new Vector2(0.5f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Layout.S(6)));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);

        bool hovered = false;
        if (ImGui.Begin("##bar", SurfaceFlags))
        {
            float line = Layout.S(36);
            if (model.Warning is { } warning)
            {
                WarningBadge(model, warning, line);
                Widgets.VerticalSeparator(line);
            }
            ModeChip(model, line);
            Widgets.VerticalSeparator(line);
            ScalerChip(model, line);
            Widgets.VerticalSeparator(line);
            FrameGenChip(model, line);
            Widgets.VerticalSeparator(line);
            HdrSwitch(model, line);
            Widgets.VerticalSeparator(line);
            Buttons(model);

            _barMin = ImGui.GetWindowPos();
            hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows
                                            | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        }
        ImGui.End();

        ImGui.PopStyleVar(3);
        return hovered;
    }

    /// <summary>
    /// The capture mode, with a dot in front of it in the wire's colour: green when the
    /// picture arrives uncompressed, amber when a codec is carrying it. What else costs
    /// latency has its own badge at the head of the bar.
    /// </summary>
    private static void ModeChip(AppModel model, float line)
    {
        var f = Widgets.F;
        var mode = model.Pump.CurrentMode;
        bool noSignal = model.Status == AppStatus.NoSignal;

        string main = mode is null ? "—"
            : $"{mode.Width}×{mode.Height} · {Strings.Num(model.Pump.CurrentFps, "0.##")} {Strings.T("hz")}";
        string tag = noSignal ? Strings.T("stNoSignal") : mode?.WireTag ?? "";
        var tagColour = noSignal ? Theme.TextTertiary
                      : mode?.Transport == WireTransport.Raw ? Theme.Success : Theme.Caution;

        float pad = Layout.S(10), gap = Layout.S(8), dot = Layout.S(8), chevron = Layout.S(12);
        var mainSize = Widgets.Measure(f.Body, main);
        var tagSize = Widgets.Measure(f.Caption, tag);
        float width = pad + dot + gap + mainSize.X + (tag.Length > 0 ? gap + tagSize.X : 0) + gap + chevron + pad;

        bool open = model.Flyout == Flyout.Mode;
        if (Widgets.SubtleButton("##modechip", new Vector2(width, line), open, out var min))
            model.ToggleFlyout(Flyout.Mode);
        _modeMin = min;

        Widgets.Tooltip(mode?.HasCodecInPath == true
            ? $"{Strings.Sentence(Strings.T("whyCodec"))}\n{Strings.T("ttMode")}"
            : Strings.T("ttMode"));

        var draw = ImGui.GetWindowDrawList();
        float cy = min.Y + line / 2f, x = min.X + pad;

        if (noSignal) draw.AddCircle(new Vector2(x + dot / 2f, cy), dot / 2f - 0.5f, Theme.U32(Theme.TextTertiary), 0, 1f);
        else draw.AddCircleFilled(new Vector2(x + dot / 2f, cy), dot / 2f, Theme.U32(tagColour));
        x += dot + gap;

        Widgets.DrawText(draw, f.Body, new Vector2(x, cy - mainSize.Y / 2f), Theme.TextPrimary, main);
        x += mainSize.X + gap;

        if (tag.Length > 0)
        {
            Widgets.DrawText(draw, f.Caption, new Vector2(x, cy - tagSize.Y / 2f), tagColour, tag);
            x += tagSize.X + gap;
        }

        Icons.Draw(draw, open ? Icon.ChevronDown : Icon.ChevronUp, new Vector2(x, cy - chevron / 2f), chevron, Theme.TextSecondary);
    }

    /// <summary>
    /// The upscaler in use, with the variant that is really running and what it costs,
    /// opening the choice of them.
    /// </summary>
    private static void ScalerChip(AppModel model, float line)
    {
        var upscaler = model.Upscaler;
        string label = upscaler.Index == 0 ? upscaler.Name()
            : upscaler.Running() is { } running ? $"{upscaler.Short()} {running.Text}"
            : upscaler.Short();

        // Paying for an upscaler that has nothing to enlarge is worth a colour: the cost
        // is real and the picture is unchanged.
        bool wasted = upscaler.Index == 2 && model.ScalerInert;

        _scalerMin = TechniqueChip("##scalerchip", Icon.Scale, label, upscaler.Index == 0 ? "" : upscaler.CostText,
            wasted ? Theme.Caution : Theme.TextSecondary, model.Flyout == Flyout.Scaler, line,
            wasted ? string.Format(Strings.T("scaleInert"), label) : Strings.T("ttScaler"),
            () => model.ToggleFlyout(Flyout.Scaler));
    }

    /// <summary>
    /// Frame generation beside it, the same shape: what is on and what it costs. Off says
    /// so in a word rather than leaving a blank, because a blank reads as "not there".
    /// </summary>
    private static void FrameGenChip(AppModel model, float line)
    {
        var generator = model.FrameGenerator;
        bool off = generator.Index == 0;
        string state = off ? Strings.T("fgStateOff")
            : $"×{model.FrameGenOutput(generator.Variant() ?? "2").Factor}";

        _frameGenMin = TechniqueChip("##framegenchip", Icon.Frames, off ? Strings.T("fgChip") : generator.Short(), state,
            off ? Theme.TextTertiary : Theme.TextSecondary, model.Flyout == Flyout.FrameGen, line,
            Strings.T("ttFrameGen"), () => model.ToggleFlyout(Flyout.FrameGen));
    }

    /// <summary>
    /// A bar chip for a technique: its icon, what is chosen, a quieter note beside it and
    /// the chevron of the flyout it opens. Returns where it was drawn, for anchoring that
    /// flyout.
    /// </summary>
    private static Vector2 TechniqueChip(string id, Icon glyph, string label, string note, Vector4 noteColour,
                                         bool open, float line, string tooltip, Action toggle)
    {
        var f = Widgets.F;

        ImGui.SameLine(0, 0);
        float pad = Layout.S(10), gap = Layout.S(8), icon = Layout.S(16), chevron = Layout.S(12);
        var labelSize = Widgets.Measure(f.Body, label);
        var noteSize = Widgets.Measure(f.Caption, note);
        float width = pad + icon + gap + labelSize.X + (note.Length > 0 ? gap + noteSize.X : 0) + gap + chevron + pad;

        if (Widgets.SubtleButton(id, new Vector2(width, line), open, out var min)) toggle();
        Widgets.Tooltip(tooltip);

        var draw = ImGui.GetWindowDrawList();
        float cy = min.Y + line / 2f, x = min.X + pad;

        Icons.Draw(draw, glyph, new Vector2(x, cy - icon / 2f), icon, Theme.TextPrimary);
        x += icon + gap;
        Widgets.DrawText(draw, f.Body, new Vector2(x, cy - labelSize.Y / 2f), Theme.TextPrimary, label);
        x += labelSize.X + gap;

        if (note.Length > 0)
        {
            Widgets.DrawText(draw, f.Caption, new Vector2(x, cy - noteSize.Y / 2f), noteColour, note);
            x += noteSize.X + gap;
        }

        Icons.Draw(draw, open ? Icon.ChevronDown : Icon.ChevronUp, new Vector2(x, cy - chevron / 2f), chevron, Theme.TextSecondary);
        return min;
    }

    /// <summary>
    /// RTX Video HDR. The whole group is the hit area — label and switch — because a 40px
    /// switch at the bottom of a 3440px screen is a small target. When HDR cannot be had
    /// the switch greys out and the tooltip says why, rather than merely refusing.
    /// </summary>
    private static void HdrSwitch(AppModel model, float line)
    {
        var f = Widgets.F;
        var (can, reason) = model.HdrReachable();
        bool on = model.HdrOn;

        const string label = "HDR";
        float pad = Layout.S(8), gap = Layout.S(10);
        var labelSize = Widgets.Measure(f.Body, label);
        var sw = Widgets.SwitchSize;
        float width = pad + labelSize.X + gap + sw.X + pad;

        var min = ImGui.GetCursorScreenPos();
        if (ImGui.InvisibleButton("##hdr", new Vector2(width, line)) && can) model.ToggleHdr();
        bool hovered = ImGui.IsItemHovered() && can;
        bool held = ImGui.IsItemActive() && can;
        Widgets.Tooltip(can ? $"RTX Video HDR · +{Strings.Ms(HdrCostMs)} · H" : Strings.Sentence(reason));

        var draw = ImGui.GetWindowDrawList();
        float cy = min.Y + line / 2f;
        Widgets.DrawText(draw, f.Body, new Vector2(min.X + pad, cy - labelSize.Y / 2f),
                         can ? Theme.TextPrimary : Theme.TextDisabled, label);
        Widgets.DrawSwitch(draw, new Vector2(min.X + pad + labelSize.X + gap, cy - sw.Y / 2f), on && can, can, hovered, held);
    }

    // The frame budget used to be a block here too. It took a fifth of the bar to repeat a
    // number the readout already carries, so it lives only there now; a budget that is
    // actually blown still raises the badge at the head of the bar.

    private static void Buttons(AppModel model)
    {
        float gap = Layout.S(2);

        if (Widgets.IconButton("##audio", model.AudioEnabled ? Icon.Volume : Icon.Muted, false,
                               model.AudioEnabled ? Theme.TextPrimary : Theme.TextTertiary,
                               Strings.T(model.AudioEnabled ? "ttAudioOn" : "ttAudioOff")))
        {
            model.AudioEnabled = !model.AudioEnabled;
            model.RestartAudio();
            model.Touch();
        }

        ImGui.SameLine(0, gap);
        if (Widgets.IconButton("##tele", Icon.Pulse, model.ShowTelemetry,
                               model.ShowTelemetry ? Theme.AccentText : Theme.TextPrimary, Strings.T("ttTele")))
            model.CycleTelemetry();

        ImGui.SameLine(0, gap);
        bool full = model.WindowKind != WindowKind.Windowed;
        if (Widgets.IconButton("##full", full ? Icon.BackToWindow : Icon.FullScreen, false, Theme.TextPrimary,
                               Strings.T(full ? "ttWindow" : "ttFull")))
            model.SetWindowKind(full ? WindowKind.Windowed : WindowKind.BorderlessFullscreen);

        ImGui.SameLine(0, gap);
        if (Widgets.IconButton("##more", Icon.More, model.Flyout == Flyout.More, Theme.TextPrimary, Strings.T("params")))
            model.ToggleFlyout(Flyout.More);
        _moreMax = ImGui.GetItemRectMax();
    }

    // ── flyouts ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Room above the bar for a flyout. In a short window the tallest of them would run off
    /// the top, so they stop here and scroll instead.
    /// </summary>
    private static float FlyoutMaxHeight => Math.Max(Layout.S(160), _barMin.Y - Layout.S(16));

    private static bool BeginFlyout(string id, Vector2 anchor, Vector2 pivot, float width, Vector2 padding, Vector2 spacing)
    {
        ImGui.SetNextWindowPos(anchor, ImGuiCond.Always, pivot);
        ImGui.SetNextWindowSize(new Vector2(width, 0), ImGuiCond.Always);
        ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0), new Vector2(width, FlyoutMaxHeight));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, padding);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, spacing);
        return ImGui.Begin(id, SurfaceFlags & ~(ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse));
    }

    /// <summary>Closes a flyout and says whether the pointer was over it or anything it opened.</summary>
    private static bool EndFlyout()
    {
        bool hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows
                                             | ImGuiHoveredFlags.AllowWhenBlockedByPopup
                                             | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        ImGui.End();
        ImGui.PopStyleVar(2);
        return hovered;
    }

    /// <summary>A flyout's title row, with an optional quiet note on the right.</summary>
    private static void Header(string title, string note)
    {
        var f = Widgets.F;
        var pos = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X, height = Layout.S(40), inset = Layout.S(12);
        var draw = ImGui.GetWindowDrawList();

        var t = Widgets.Measure(f.BodyStrong, title);
        Widgets.DrawText(draw, f.BodyStrong, new Vector2(pos.X + inset, pos.Y + (height - t.Y) / 2f), Theme.TextPrimary, title);

        if (note.Length > 0)
        {
            var n = Widgets.Measure(f.Caption, note);
            // Sat on the title's baseline rather than centred: the two sizes centred
            // independently look as though the note has slipped.
            Widgets.DrawText(draw, f.Caption, new Vector2(pos.X + width - inset - n.X, pos.Y + (height + t.Y) / 2f - n.Y),
                             Theme.TextTertiary, note);
        }

        ImGui.Dummy(new Vector2(width, height));
    }

    private static void GroupLabel(string text, float height)
    {
        var f = Widgets.F;
        var pos = ImGui.GetCursorScreenPos();
        var size = Widgets.Measure(f.Caption, text);
        Widgets.DrawText(ImGui.GetWindowDrawList(), f.Caption,
                         new Vector2(pos.X + Layout.S(12), pos.Y + (height - size.Y) / 2f), Theme.TextTertiary, text);
        ImGui.Dummy(new Vector2(ImGui.GetContentRegionAvail().X, height));
    }

    /// <summary>
    /// Every offerable mode, uncompressed first. Only NV12 and P010 are listed — nothing
    /// else can be displayed — and modes that only repeat another are already gone.
    /// </summary>
    private static bool ModeFlyout(AppModel model)
    {
        var f = Widgets.F;
        BeginFlyout("##modes", new Vector2(_modeMin.X, _barMin.Y - Layout.S(8)), new Vector2(0, 1),
                    Layout.S(520), new Vector2(Layout.S(4)), Vector2.Zero);

        if (model.Active is { } probe)
        {
            var modes = probe.Renderable.Where(m => m.IsOfferable)
                .OrderByDescending(m => m.Width * m.Height)
                .ThenByDescending(m => m.MaxFps)
                .ToList();
            var raw = modes.Where(m => m.Transport == WireTransport.Raw).ToList();
            var packed = modes.Where(m => m.Transport != WireTransport.Raw).ToList();

            // The link figure is the divisor of the wire-time column, so it has to come
            // from this device, never from a constant.
            string link = probe.WireMiBs > 0 && !probe.Device.IsVirtual
                ? " · " + string.Format(Strings.T("capNote"), probe.WireMiBs.ToString("0"))
                : "";
            Header(Strings.T("modeTitle"), probe.Device.FriendlyName + link);

            float row = Layout.S(36), label = Layout.S(28), rule = Layout.S(9);
            float content = label + raw.Count * row + (packed.Count > 0 ? rule + label + packed.Count * row : 0);

            // The list scrolls inside the flyout rather than the flyout scrolling as a whole,
            // so the header and the footer stay put. Header, footer and padding take about
            // 110 at zoom 1 of whatever room there is.
            float room = Math.Max(Layout.S(120), FlyoutMaxHeight - Layout.S(110));
            if (ImGui.BeginChild("##modelist", new Vector2(0, Math.Min(content, Math.Min(Layout.S(400), room)))))
            {
                GroupLabel(Strings.T("rawGroup"), label);
                foreach (var m in raw) ModeRow(model, probe, m, row);

                if (packed.Count > 0)
                {
                    var pos = ImGui.GetCursorScreenPos();
                    float w = ImGui.GetContentRegionAvail().X;
                    ImGui.GetWindowDrawList().AddLine(new Vector2(pos.X + Layout.S(8), pos.Y + rule / 2f),
                        new Vector2(pos.X + w - Layout.S(8), pos.Y + rule / 2f), Theme.U32(Theme.Divider));
                    ImGui.Dummy(new Vector2(w, rule));

                    GroupLabel(Strings.T("codecGroup"), label);
                    foreach (var m in packed) ModeRow(model, probe, m, row);
                }
            }
            ImGui.EndChild();

            // The footer: how much of the device's list this is, and the one warning worth
            // saying once rather than on every row it applies to. A rule above it, so a row
            // half scrolled out of the list reads as the list's edge, not as a glitch.
            float width = ImGui.GetContentRegionAvail().X, inset = Layout.S(12);
            var ruleAt = ImGui.GetCursorScreenPos();
            ImGui.GetWindowDrawList().AddLine(new Vector2(ruleAt.X + Layout.S(8), ruleAt.Y),
                new Vector2(ruleAt.X + width - Layout.S(8), ruleAt.Y), Theme.U32(Theme.Divider));
            ImGui.Dummy(new Vector2(0, Layout.S(8)));
            var start = ImGui.GetCursorScreenPos();
            var draw = ImGui.GetWindowDrawList();
            var shown = string.Format(Strings.T("modesShown"), modes.Count, probe.Modes.Count);
            var close = Strings.T("closeM");
            var closeSize = Widgets.Measure(f.Caption, close);
            Widgets.DrawText(draw, f.Caption, start + new Vector2(inset, 0), Theme.TextTertiary, shown);
            Widgets.DrawText(draw, f.Caption, start + new Vector2(width - inset - closeSize.X, 0), Theme.TextTertiary, close);
            ImGui.Dummy(new Vector2(width, closeSize.Y));

            if (modes.Any(m => m.Format.IsHighBitDepth))
            {
                ImGui.Dummy(new Vector2(0, Layout.S(2)));
                ImGui.Indent(inset);
                Widgets.Text(f.Caption, Theme.Caution, Strings.T("tenBitCost"));
                ImGui.Unindent(inset);
            }
            ImGui.Dummy(new Vector2(0, Layout.S(8)));
        }

        return EndFlyout();
    }

    private static void ModeRow(AppModel model, DeviceProbe probe, CaptureMode m, float height)
    {
        var f = Widgets.F;
        bool current = model.Pump.CurrentMode?.TypeIndex == m.TypeIndex;

        if (Widgets.ListRow($"##m{m.TypeIndex}", height, current, true, out var min, out var max) && !current)
        {
            model.SelectMode(m, m.MaxFps);
            model.Flyout = Flyout.None;
        }

        var draw = ImGui.GetWindowDrawList();
        float cy = (min.Y + max.Y) / 2f;

        void Left(ImFontPtr font, Vector4 colour, string text, float x)
        {
            var s = Widgets.Measure(font, text);
            Widgets.DrawText(draw, font, new Vector2(x, cy - s.Y / 2f), colour, text);
        }

        void Right(ImFontPtr font, Vector4 colour, string text, float right)
        {
            var s = Widgets.Measure(font, text);
            Widgets.DrawText(draw, font, new Vector2(right - s.X, cy - s.Y / 2f), colour, text);
        }

        Left(f.Body, Theme.TextPrimary, $"{m.Width}×{m.Height}", min.X + Layout.S(12));
        Left(f.Body, Theme.TextPrimary, $"{Strings.Num(m.MaxFps, "0.##")} {Strings.T("hz")}", max.X - Layout.S(262));

        // 10-bit is the one choice here that quietly costs something: twice the bytes,
        // twice the wire time, and a delivery that stutters. Marked on the format, since
        // the time alone does not say why.
        Left(f.Caption, m.Format.IsHighBitDepth ? Theme.Caution : Theme.TextSecondary, m.Format.Name, max.X - Layout.S(172));

        // What the mode costs before anything of ours runs. Blank for a codec mode: fewer
        // bytes cross the wire, but a decode follows, and the total is not something the
        // mode list can state.
        double ms = m.WireMs(probe.WireMiBs);
        Right(f.Caption, m.Format.IsHighBitDepth ? Theme.Caution : Theme.TextSecondary,
              ms > 0 ? Strings.Ms(ms, "0.0") : "—", max.X - Layout.S(64));

        Right(f.Caption, m.Transport == WireTransport.Raw ? Theme.Success : Theme.Caution, m.WireTag, max.X - Layout.S(12));
    }

    // ── techniques: upscaling and frame generation ───────────────────────────────

    // Both flyouts are rows of settings, the way a game's graphics menu is: what it is
    // called on the left, the choice on the right. The first row picks the technique from
    // a list that gives each one's cost and, for one that cannot be used, the reason; the
    // rows under it belong to whichever technique is chosen.
    private const float RowInset = 12f, RowLabel = 120f, RowHeight = 32f, RowGap = 6f;

    /// <summary>
    /// Which upscaler, then what goes with it: DLSS's network and the one really running,
    /// FSR's variant, NIS's sharpness. The footer says how much enlarging there is to do at
    /// all, which is what decides whether any of this matters.
    /// </summary>
    private static bool ScalerFlyout(AppModel model)
    {
        var f = Widgets.F;
        BeginFlyout("##scalers", new Vector2(_scalerMin.X, _barMin.Y - Layout.S(8)), new Vector2(0, 1),
                    Layout.S(420), new Vector2(Layout.S(4)), Vector2.Zero);

        var current = model.Upscaler;
        Header(Strings.T("scalerTitle"), string.Format(Strings.T("costPerFrame"), current.CostText));

        TechniqueRows(model, "##up", model.Upscalers, current, t => model.SelectScaler(t.Index));

        if (current.Index == 1 && model.Renderer.Nis is { } nis)
        {
            float width = BeginRow(Strings.T("sharpness"));
            float sharp = nis.Sharpness;
            var value = Strings.Num(sharp, "0.00");
            float valueWidth = Layout.S(40);
            var at = ImGui.GetCursorScreenPos();

            // The slider is a thumb tall, the row a control tall: centred in it by hand.
            ImGui.SetCursorScreenPos(at + new Vector2(0, (Layout.S(RowHeight) - Layout.S(20)) / 2f));
            if (Widgets.Slider("##sharp", ref sharp, 0f, 1f, true, width - valueWidth))
            {
                nis.Sharpness = MathF.Round(sharp * 20f) / 20f;
                model.Touch();
            }

            // Above 0.5 the value turns amber: at the small scale factors this app usually
            // runs at, that reads as oversharpening rather than as detail.
            var size = Widgets.Measure(f.Body, value);
            Widgets.DrawText(ImGui.GetWindowDrawList(), f.Body,
                new Vector2(at.X + width - size.X, at.Y + (Layout.S(RowHeight) - size.Y) / 2f),
                sharp > 0.5f ? Theme.Caution : Theme.TextSecondary, value);
            ImGui.SetCursorScreenPos(at);
            ImGui.Dummy(new Vector2(width, Layout.S(RowHeight)));
        }

        // How much enlarging there is to do at all. At ×1 or below nothing is enlarged and
        // every upscaler only costs.
        float inset = Layout.S(RowInset);
        ImGui.Dummy(new Vector2(0, Layout.S(10)));
        ImGui.Indent(inset);
        ImGui.PushTextWrapPos(Widgets.ContentRight() - inset);
        if (model.Pump.CurrentMode is { Height: > 0 } mode && model.ScaleFactor > 0)
        {
            if (model.ScalerInert)
                Widgets.Text(f.Caption, Theme.Caution, string.Format(Strings.T("scaleInert"), model.Upscalers[2].Name()));
            else
                Widgets.Text(f.Caption, Theme.TextTertiary, string.Format(Strings.T("scaleUp"), model.ScaleLabel,
                    mode.Height, (int)Math.Round(mode.Height * model.ScaleFactor)));
        }
        ImGui.PopTextWrapPos();
        ImGui.Unindent(inset);
        ImGui.Dummy(new Vector2(0, Layout.S(8)));

        return EndFlyout();
    }

    /// <summary>
    /// Frame generation, the same rows for the same kind of choice: which generator, how
    /// many frames it shows for each one that arrives, and what that comes to on this
    /// source and this display.
    /// <para>
    /// Nothing is behind it yet, and it says so in a sentence rather than by being grey
    /// alone. The two figures it does show are arithmetic and are worded as such: the rate
    /// a multiplier makes, and the least delay any generator of this kind adds. The GPU
    /// time nobody has measured, so it reads as not measured.
    /// </para>
    /// </summary>
    private static bool FrameGenFlyout(AppModel model)
    {
        var f = Widgets.F;
        BeginFlyout("##framegen", new Vector2(_frameGenMin.X, _barMin.Y - Layout.S(8)), new Vector2(0, 1),
                    Layout.S(420), new Vector2(Layout.S(4)), Vector2.Zero);

        var current = model.FrameGenerator;
        bool on = current.Index != 0 && current.Usable;
        Header(Strings.T("fgTitle"), string.Format(Strings.T("costPerFrame"), current.CostText));

        TechniqueRows(model, "##fg", model.FrameGenerators, current, t => model.SelectFrameGen(t.Index));

        if (model.Pump.CurrentFps > 0)
        {
            var (factor, hz) = model.FrameGenOutput(current.Variant() ?? "2");
            string hzUnit = Strings.T("hz");
            string result = $"{Strings.Num(model.Pump.CurrentFps, "0.##")} → {Strings.Num(hz, "0.##")} {hzUnit}";
            if (model.DisplayHz > 0) result += " · " + string.Format(Strings.T("fgDisplay"), model.DisplayHz);

            TextRow(Strings.T("fgResult"), result, on ? Theme.TextSecondary : Theme.TextTertiary, on);
            TextRow(Strings.T("fgCost"),
                    string.Format(Strings.T("fgCostV"), Strings.Ms(model.FrameGenDelayFloorMs(factor), "0.0")),
                    on ? Theme.TextSecondary : Theme.TextTertiary, on);

            // Frames past the display's own rate are generated and never shown.
            if (model.DisplayHz > 0 && hz > model.DisplayHz + 0.5)
                Note(string.Format(Strings.T("fgPast"), model.DisplayHz), on ? Theme.Caution : Theme.TextTertiary);
        }

        // Why nothing here can be turned on, said once for all of them when the reason is
        // the same; the list gives each generator's own reason when they differ.
        string? blocked = null;
        foreach (var generator in model.FrameGenerators)
        {
            if (generator.Index == 0) continue;
            blocked = generator.Unavailable();
            if (blocked is null) break;
        }
        if (blocked is not null) Note(blocked, Theme.Caution);

        ImGui.Dummy(new Vector2(0, Layout.S(10)));
        ImGui.Indent(Layout.S(RowInset));
        Widgets.Text(f.Caption, Theme.TextTertiary, Strings.T("closeG"));
        ImGui.Unindent(Layout.S(RowInset));
        ImGui.Dummy(new Vector2(0, Layout.S(8)));

        return EndFlyout();
    }

    /// <summary>
    /// The rows every technique has: which one, the variant it is set to, and what is
    /// really running when that can differ from what was asked.
    /// </summary>
    private static void TechniqueRows(AppModel model, string id, IReadOnlyList<Technique> techniques,
                                      Technique current, Action<Technique> select)
    {
        float width = BeginRow(Strings.T("tvTechnique"));
        bool open = model.OpenTechniqueList;
        model.OpenTechniqueList = false;
        TechniqueCombo(id, techniques, current, width, open, select);

        if (current.Variants.Count > 0)
        {
            // Greyed when there is nothing for it to set: the technique cannot be used, or
            // the variants are only on show beside something that is off.
            bool settable = current.Usable && !current.VariantsLocked;
            width = BeginRow(Strings.T(current.VariantsLabel), settable);

            string? asked = current.Variant();
            int index = 0;
            var names = new string[current.Variants.Count];
            for (int i = 0; i < names.Length; i++)
            {
                names[i] = current.Variants[i].Text;
                if (current.Variants[i].Id == asked) index = i;
            }
            if (Widgets.Combo(id + "variant", names[index], names, index, out int chosen, width, settable))
                current.SetVariant(current.Variants[chosen].Id);
        }

        // Amber when something overrode the choice, because the row above is then not
        // being obeyed.
        if (current.Running() is { } running)
            TextRow(Strings.T("tvRunning"),
                    running.Overridden ? string.Format(Strings.T("dlssForced"), running.Text) : running.Text,
                    running.Overridden ? Theme.Caution : Theme.TextSecondary);
    }

    /// <summary>
    /// The technique's combo box. Its list is wider than the box — the width of the flyout —
    /// because each row carries what the bar's chip cannot: one line on what the technique
    /// does and what it costs, or, for one that cannot be used, why not. Such a row cannot
    /// be chosen; it is there to be read.
    /// </summary>
    private static void TechniqueCombo(string id, IReadOnlyList<Technique> techniques, Technique current,
                                       float width, bool open, Action<Technique> select)
    {
        var f = Widgets.F;
        string popup = id + "##list";
        var size = new Vector2(width, Layout.S(RowHeight));
        if (Widgets.ComboFace(id, current.Name(), size, true, out var min) || open) ImGui.OpenPopup(popup);

        float inset = Layout.S(12), padY = Layout.S(4), costColumn = Layout.S(72);
        float listLeft = ImGui.GetWindowPos().X + Layout.S(4);
        float listWidth = ImGui.GetWindowSize().X - Layout.S(8);
        float wrap = listWidth - Layout.S(8) - inset * 2 - costColumn;

        // A reason can run to two lines in some languages, so every row is measured.
        Span<float> heights = stackalloc float[techniques.Count];
        float nameH = Widgets.Measure(f.Body, "0").Y, listH = padY * 2;
        for (int i = 0; i < techniques.Count; i++)
        {
            string second = techniques[i].Unavailable() ?? techniques[i].Description();
            heights[i] = Math.Max(Layout.S(52), Layout.S(18) + nameH + Widgets.MeasureWrapped(f.Caption, second, wrap).Y);
            listH += heights[i];
        }

        // Downwards only while the list ends clear of the bar. The flyout stands on the bar,
        // so that is rare; otherwise it opens upwards, over the flyout's own header.
        bool above = min.Y + size.Y + Layout.S(4) + listH > _barMin.Y - Layout.S(8);
        ImGui.SetNextWindowPos(above ? new Vector2(listLeft, min.Y - Layout.S(4)) : new Vector2(listLeft, min.Y + size.Y + Layout.S(4)),
                               ImGuiCond.Always, above ? new Vector2(0, 1) : Vector2.Zero);
        ImGui.SetNextWindowSize(new Vector2(listWidth, listH));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Layout.S(4), padY));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);

        if (ImGui.BeginPopup(popup))
        {
            var draw = ImGui.GetWindowDrawList();
            for (int i = 0; i < techniques.Count; i++)
            {
                var technique = techniques[i];
                string? why = technique.Unavailable();
                bool usable = why is null, selected = ReferenceEquals(technique, current);

                if (Widgets.ListRow($"{id}#{technique.Index}", heights[i], selected, usable, out var rowMin, out var rowMax)
                    && !selected)
                {
                    select(technique);
                    ImGui.CloseCurrentPopup();
                }
                var next = ImGui.GetCursorScreenPos();

                float top = rowMin.Y + Layout.S(8);
                Widgets.DrawText(draw, f.Body, new Vector2(rowMin.X + inset, top),
                                 usable ? Theme.TextPrimary : Theme.TextDisabled, technique.Name());

                ImGui.SetCursorScreenPos(new Vector2(rowMin.X + inset, top + nameH + Layout.S(2)));
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrap);
                Widgets.Text(f.Caption, usable ? Theme.TextTertiary : Theme.Alpha(Theme.Caution, 0.8f),
                             why ?? technique.Description());
                ImGui.PopTextWrapPos();
                ImGui.SetCursorScreenPos(next);

                // The cost where it can be compared with the one above and below it; a lock
                // where there is no using it, so the column never shows a figure for
                // something that cannot be had.
                float cy = (rowMin.Y + rowMax.Y) / 2f;
                if (usable)
                {
                    string cost = technique.CostText;
                    var c = Widgets.Measure(f.Caption, cost);
                    Widgets.DrawText(draw, f.Caption, new Vector2(rowMax.X - inset - c.X, cy - c.Y / 2f), Theme.TextSecondary, cost);
                }
                else
                {
                    float glyph = Layout.S(16);
                    Icons.Draw(draw, Icon.Lock, new Vector2(rowMax.X - inset - glyph, cy - glyph / 2f), glyph, Theme.Caution);
                }
            }
            ImGui.EndPopup();
        }

        ImGui.PopStyleVar(2);
    }

    /// <summary>
    /// Starts a row: writes its label and leaves the cursor where the control goes.
    /// Returns the width the control has. A row is one control tall; a control that is not
    /// must claim that height itself.
    /// </summary>
    private static float BeginRow(string label, bool enabled = true)
    {
        var f = Widgets.F;
        ImGui.Dummy(new Vector2(0, Layout.S(RowGap)));

        var pos = ImGui.GetCursorScreenPos();
        float inset = Layout.S(RowInset), labelWidth = Layout.S(RowLabel);
        float control = ImGui.GetContentRegionAvail().X - inset * 2 - labelWidth;

        var size = Widgets.Measure(f.Body, label);
        Widgets.DrawText(ImGui.GetWindowDrawList(), f.Body,
                         new Vector2(pos.X + inset, pos.Y + (Layout.S(RowHeight) - size.Y) / 2f),
                         enabled ? Theme.TextPrimary : Theme.TextDisabled, label);

        ImGui.SetCursorScreenPos(new Vector2(pos.X + inset + labelWidth, pos.Y));
        return control;
    }

    /// <summary>A row whose right-hand side is a statement rather than a control.</summary>
    private static void TextRow(string label, string value, Vector4 colour, bool enabled = true)
    {
        var f = Widgets.F;
        float width = BeginRow(label, enabled);
        var at = ImGui.GetCursorScreenPos();

        // Wrapped, and the row grows with it: a statement in German is not a short thing.
        // Starts where a combo box's own text does, so the column reads as one.
        float lead = Layout.S(11);
        width -= lead;
        at.X += lead;
        float textH = Widgets.MeasureWrapped(f.Body, value, width).Y;
        float row = Math.Max(Layout.S(RowHeight), textH + Layout.S(8));

        ImGui.SetCursorScreenPos(at + new Vector2(0, (row - textH) / 2f));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width);
        Widgets.Text(f.Body, colour, value);
        ImGui.PopTextWrapPos();
        ImGui.SetCursorScreenPos(at);
        ImGui.Dummy(new Vector2(width, row));
    }

    /// <summary>A sentence under the rows, across the flyout's width.</summary>
    private static void Note(string text, Vector4 colour)
    {
        float inset = Layout.S(RowInset);
        ImGui.Dummy(new Vector2(0, Layout.S(8)));
        ImGui.Indent(inset);
        ImGui.PushTextWrapPos(Widgets.ContentRight() - inset);
        Widgets.Text(Widgets.F.Caption, colour, text);
        ImGui.PopTextWrapPos();
        ImGui.Unindent(inset);
    }

    /// <summary>
    /// Everything set once and then left alone: which card, where its sound goes, how
    /// bright the interface is in HDR, the window and the language. And the way out.
    /// </summary>
    private static bool MoreFlyout(AppModel model)
    {
        var f = Widgets.F;
        BeginFlyout("##more", new Vector2(_moreMax.X, _barMin.Y - Layout.S(8)), new Vector2(1, 1),
                    Layout.S(380), new Vector2(Layout.S(16), Layout.S(14)), new Vector2(Layout.S(8), Layout.S(6)));

        Widgets.Text(f.BodyStrong, Theme.TextPrimary, Strings.T("params"));
        ImGui.Dummy(new Vector2(0, Layout.S(2)));

        DeviceSection(model);
        Widgets.Divider(12f);
        AudioSection(model);

        if (model.Renderer.SwapChainIsPq)
        {
            Widgets.Divider(12f);
            BrightnessSection(model);
        }

        Widgets.Divider(12f);
        WindowAndLanguage(model);

        Widgets.Divider(12f);
        Widgets.Wrapped(f.Caption, Theme.TextTertiary, Strings.T("hotkeys"));
        ImGui.Dummy(new Vector2(0, Layout.S(4)));

        // The handoff had this return to the device picker; it asks and then closes,
        // because that is what the confirmation dialog exists to guard.
        var label = Strings.T("exit");
        var size = new Vector2(Math.Max(Layout.S(96), Widgets.Measure(f.Body, label).X + Layout.S(32)), Layout.S(32));
        Widgets.RightAlign(size.X);
        if (Widgets.Button(label, size)) model.ConfirmQuit = true;

        return EndFlyout();
    }

    private static bool Selectable(AppModel model, CaptureDeviceInfo d) =>
        !model.Probed.TryGetValue(d.SymbolicLink, out var p)
        || p.Availability is not (DeviceAvailability.Busy or DeviceAvailability.Unusable);

    private static void DeviceSection(AppModel model)
    {
        var f = Widgets.F;
        Widgets.Text(f.Caption, Theme.TextSecondary, Strings.T("device"));

        var devices = model.Devices.Where(d => Selectable(model, d)).ToList();
        int current = Math.Max(0, devices.FindIndex(d => model.Pump.Device.Is(d)));
        var names = devices.Select(d => d.FriendlyName).ToList();

        if (names.Count > 0 && Widgets.Combo("##device", model.Pump.Device.FriendlyName, names, current, out int chosen))
            model.SwitchDevice(devices[chosen]);

        // What the hardware actually is: the adapter doing the work and the wire the
        // picture comes over, with the throughput this card measured on it.
        var parts = new List<string> { model.Renderer.AdapterName };
        if (model.Active is { } a && !a.Device.IsVirtual)
        {
            if (a.Link.Known) parts.Add(a.Link.LinkClass);
            if (a.WireMiBs > 0) parts.Add(string.Format(Strings.T("capNote"), a.WireMiBs.ToString("0")));
        }
        Widgets.Wrapped(f.Caption, Theme.TextTertiary, string.Join(" · ", parts));
    }

    private static void AudioSection(AppModel model)
    {
        var f = Widgets.F;
        bool on = model.AudioEnabled;
        if (Widgets.LabelSwitch(Strings.T("audioSend"), "##audiosend", ref on))
        {
            model.AudioEnabled = on;
            model.RestartAudio();
            model.Touch();
        }

        if (!model.AudioEnabled) return;

        if (model.AudioInputs.Count == 0)
        {
            Widgets.Wrapped(f.Caption, Theme.Caution, Strings.Sentence(Strings.T("audioNoInput")));
            return;
        }

        Endpoint("audioIn", model.AudioInputs, model.AudioInputId,
            id => { model.AudioInputId = id; model.RestartAudio(); model.Touch(); });
        Endpoint("audioOut", model.AudioOutputs, model.AudioOutputId,
            id => { model.AudioOutputId = id; model.RestartAudio(); model.Touch(); });

        if (!model.Audio.Running && model.Audio.LastError is { } err)
            Widgets.Wrapped(f.Caption, Theme.Critical, err);
    }

    private static void Endpoint(string labelKey, List<Audio.AudioEndpointInfo> options, string? currentId, Action<string> pick)
    {
        if (options.Count == 0) return;

        var current = options.FirstOrDefault(o => o.Id == currentId)
                      ?? options.FirstOrDefault(o => o.IsDefault)
                      ?? options[0];

        Widgets.Text(Widgets.F.Caption, Theme.TextSecondary, Strings.Sentence(Strings.T(labelKey)));
        var names = options.Select(o => o.Name).ToList();
        if (Widgets.Combo($"##{labelKey}", current.Name, names, options.IndexOf(current), out int chosen))
            pick(options[chosen].Id);
    }

    /// <summary>
    /// Both levels matter whenever the surface is PQ, and they are two different pictures:
    /// the interface must stay dim enough not to burn an OLED, the video should look like
    /// the console intended, and one number cannot serve both.
    /// </summary>
    private static void BrightnessSection(AppModel model)
    {
        var f = Widgets.F;
        string nits = Strings.T("nits");

        float overlay = model.OverlayNits;
        Widgets.LabelValue(Strings.T("uiBright"), $"{overlay:0} {nits}", overlay > 250f ? Theme.Caution : Theme.TextSecondary);
        if (Widgets.Slider("##uinits", ref overlay, 80f, 400f))
        {
            model.OverlayNits = MathF.Round(overlay / 10f) * 10f;
            model.Touch();
        }

        // Our shader only encodes the video while the driver filter is off; with RTX HDR
        // on, the driver produces the PQ values and this would do nothing.
        if (!model.HdrOn)
        {
            ImGui.Dummy(new Vector2(0, Layout.S(4)));
            float video = model.VideoNits;
            Widgets.LabelValue(Strings.T("videoBright"), $"{video:0} {nits}", Theme.TextSecondary);
            if (Widgets.Slider("##videonits", ref video, 80f, 600f))
            {
                model.VideoNits = MathF.Round(video / 10f) * 10f;
                model.Touch();
            }
        }
        else
        {
            Widgets.Wrapped(f.Caption, Theme.TextTertiary,
                $"{Strings.T("videoBright")}: {Strings.T("videoBrightDriver")}");
        }
    }

    /// <summary>
    /// Two window modes, not three. A topmost borderless window and a borderless one were
    /// the same swapchain on the same overlay plane, with two names nobody could tell apart.
    /// </summary>
    private static void WindowAndLanguage(AppModel model)
    {
        var f = Widgets.F;
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(Layout.S(4), 0));
        if (ImGui.BeginTable("##winlang", 2, ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableNextColumn();
            Widgets.Text(f.Caption, Theme.TextSecondary, Strings.T("window"));
            string[] kinds = [Strings.T("wWindowed"), Strings.T("wBorderless")];
            int kind = model.WindowKind == WindowKind.Windowed ? 0 : 1;
            if (Widgets.Combo("##winmode", kinds[kind], kinds, kind, out int chosenKind))
                model.SetWindowKind(chosenKind == 0 ? WindowKind.Windowed : WindowKind.BorderlessFullscreen);

            ImGui.TableNextColumn();
            Widgets.Text(f.Caption, Theme.TextSecondary, Strings.T("language"));
            if (Widgets.Combo("##lang", Strings.LanguageNames[Strings.CurrentIndex], Strings.LanguageNames,
                              Strings.CurrentIndex, out int chosenLang))
            {
                Strings.Use(chosenLang);
                model.Touch();
            }

            ImGui.EndTable();
        }
        ImGui.PopStyleVar();
    }

    // ── the warning badge ────────────────────────────────────────────────────────

    /// <summary>
    /// A red "!" at the head of the bar while something is quietly costing latency that the
    /// user can fix: the window being composed by Windows, or the processing not fitting
    /// in a frame. The explanation is its tooltip, and for a composed window a click is
    /// the fix.
    /// <para>
    /// It used to be an InfoBar above the bar with a ×. Closing it did not hold: the
    /// composition reading flickers for a second whenever DXGI cannot say how the window
    /// is presented, and every flicker brought the bar back. A badge needs no closing — it
    /// is small, it goes away with the bar, and it is gone as soon as the cause is.
    /// </para>
    /// </summary>
    private static void WarningBadge(AppModel model, string warning, float line)
    {
        bool composed = warning == "composed";
        bool canFix = composed && model.WindowKind == WindowKind.Windowed;

        var size = new Vector2(Layout.S(36), line);
        if (Widgets.SubtleButton("##warning", size, false, out var min) && canFix)
            model.SetWindowKind(WindowKind.BorderlessFullscreen);

        Icons.SeverityBadge(ImGui.GetWindowDrawList(), min + size / 2f, Layout.S(8),
                            Theme.Alert, Theme.TextPrimary, exclamation: true);

        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip)) return;

        string text = composed
            ? Strings.T("infoComposedTitle") + "\n" + string.Format(Strings.T("infoComposedBody"),
                  Strings.Num(model.BudgetMs > 0 ? model.BudgetMs : 8.33, "0.0"))
            : Strings.T("infoBudgetTitle") + "\n" + Strings.T("infoBudgetBody");
        if (canFix) text += "\n\n" + Strings.T("warnFix");
        Widgets.ShowTooltip(text);
    }
}
