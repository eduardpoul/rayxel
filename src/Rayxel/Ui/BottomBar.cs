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
        // The word for the wire — raw, zip — is in the list of modes, on every row; here the
        // dot's colour says it. Only "no signal" is spelt out, being a state, not a property.
        string tag = noSignal ? Strings.T("stNoSignal") : "";
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
        // is real and the picture is unchanged. The cost itself is not on the bar — it is
        // in the flyout, beside the others it can be compared with.
        bool wasted = upscaler.Index == 2 && model.ScalerInert;

        _scalerMin = TechniqueChip("##scalerchip", Icon.Scale, wasted ? Theme.Caution : Theme.TextPrimary, label, "",
            Theme.TextSecondary, model.Flyout == Flyout.Scaler, line,
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

        _frameGenMin = TechniqueChip("##framegenchip", Icon.Frames, Theme.TextPrimary,
            off ? Strings.T("fgChip") : generator.Short(), state,
            off ? Theme.TextTertiary : Theme.TextSecondary, model.Flyout == Flyout.FrameGen, line,
            Strings.T("ttFrameGen"), () => model.ToggleFlyout(Flyout.FrameGen));
    }

    /// <summary>
    /// A bar chip for a technique: its icon, what is chosen, a quieter note beside it and
    /// the chevron of the flyout it opens. Returns where it was drawn, for anchoring that
    /// flyout.
    /// </summary>
    private static Vector2 TechniqueChip(string id, Icon glyph, Vector4 glyphColour, string label, string note,
                                         Vector4 noteColour, bool open, float line, string tooltip, Action toggle)
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

        Icons.Draw(draw, glyph, new Vector2(x, cy - icon / 2f), icon, glyphColour);
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
    // called on the left, an ordinary combo box on the right. What there is to explain
    // about a row sits behind an info button beside its label, not on the surface: the
    // flyout is for choosing, and whoever wants the reasoning asks for it.
    private const float RowInset = 12f, RowLabel = 132f, RowHeight = 32f, RowGap = 6f;

    /// <summary>
    /// Which upscaler, then what goes with it: DLSS's network and the one really running,
    /// FSR's variant, NIS's sharpness. Each one's cost is in the list it is chosen from,
    /// and the chosen one's is in the header — the only places it is shown.
    /// </summary>
    private static bool ScalerFlyout(AppModel model)
    {
        var f = Widgets.F;
        BeginFlyout("##scalers", new Vector2(_scalerMin.X, _barMin.Y - Layout.S(8)), new Vector2(0, 1),
                    Layout.S(420), new Vector2(Layout.S(4)), Vector2.Zero);

        var current = model.Upscaler;
        Header(Strings.T("scalerTitle"), string.Format(Strings.T("costPerFrame"), current.CostText));

        // How much enlarging there is to do at all, which is what decides whether any of
        // this matters. Part of what the technique's info button says.
        string scale = model.Pump.CurrentMode is { Height: > 0 } mode && model.ScaleFactor > 0 && !model.ScalerInert
            ? string.Format(Strings.T("scaleUp"), model.ScaleLabel, mode.Height, (int)Math.Round(mode.Height * model.ScaleFactor))
            : "";
        TechniqueRows(model, "##up", model.Upscalers, current, scale, t => model.SelectScaler(t.Index));

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

        // The one thing said out loud: at ×1 or below nothing is enlarged, and an upscaler
        // that is on is being paid for and doing nothing.
        if (model.ScalerInert && current.Index != 0)
            Note(string.Format(Strings.T("scaleInert"), current.Short()), Theme.Caution);

        ImGui.Dummy(new Vector2(0, Layout.S(12)));
        return EndFlyout();
    }

    /// <summary>
    /// Frame generation, the same rows for the same kind of choice: which generator, and
    /// how many frames it shows for each one that arrives.
    /// <para>
    /// Nothing is behind it yet. The generators are in the list, greyed, each with its
    /// reason behind an info mark, and the technique's own info button is amber and says
    /// the same; the multiplier is on show and cannot be set. What a multiplier would come
    /// to — the rate, and the least delay it can add — is arithmetic, worded as such, behind
    /// the multiplier's info button.
    /// </para>
    /// </summary>
    private static bool FrameGenFlyout(AppModel model)
    {
        BeginFlyout("##framegen", new Vector2(_frameGenMin.X, _barMin.Y - Layout.S(8)), new Vector2(0, 1),
                    Layout.S(420), new Vector2(Layout.S(4)), Vector2.Zero);

        var current = model.FrameGenerator;
        Header(Strings.T("fgTitle"), string.Format(Strings.T("costPerFrame"), current.CostText));
        TechniqueRows(model, "##fg", model.FrameGenerators, current, "", t => model.SelectFrameGen(t.Index));

        ImGui.Dummy(new Vector2(0, Layout.S(12)));
        return EndFlyout();
    }

    /// <summary>
    /// The rows every technique has: which one, and the variant it is set to.
    /// <para>
    /// What is really running has no row of its own: the variant's box shows it. When the
    /// choice has been overridden the box cannot be set, and the reason and the way out are
    /// written under it.
    /// </para>
    /// </summary>
    private static void TechniqueRows(AppModel model, string id, IReadOnlyList<Technique> techniques,
                                      Technique current, string extraInfo, Action<Technique> select)
    {
        // The list: a name and its cost, one line each, and beside the list what the row
        // under the pointer or the keys is. One that cannot be used stays in it, greyed.
        var items = new Widgets.ComboItem[techniques.Count];
        int index = 0;
        bool anyOtherUsable = false;
        for (int i = 0; i < items.Length; i++)
        {
            var technique = techniques[i];
            string? why = technique.Unavailable();
            items[i] = new(technique.Short(), why is null ? technique.CostText : "", why is null, ItemInfo(technique, why));
            if (ReferenceEquals(technique, current)) index = i;
            else if (why is null) anyOtherUsable = true;
        }

        int openAt = Math.Min(model.OpenTechniqueAt, items.Length - 1);
        model.OpenTechniqueAt = -1;

        // Amber when there is nothing here to choose between: that is then the thing to read.
        float width = BeginRow(Strings.T("tvTechnique"), true, TechniqueInfo(techniques, extraInfo),
                               warning: !anyOtherUsable, shown: model.ShowTechniqueInfo);
        if (Widgets.Combo(id, items[index].Text, items, index, out int chosen, width, true, openAt))
            select(techniques[chosen]);

        if (current.Variants.Count == 0) return;

        string? asked = current.Variant();
        var running = current.Running();
        string? overridden = running?.Overridden;

        // Greyed when there is nothing for it to set: the technique cannot be used, the
        // variants are only on show beside something that is off, or something else has
        // overridden the choice — and then nothing chosen here would be obeyed.
        bool settable = current.Usable && !current.VariantsLocked && overridden is null;

        var variants = new Widgets.ComboItem[current.Variants.Count];
        int variant = 0, runs = -1;
        for (int i = 0; i < variants.Length; i++)
        {
            if (current.Variants[i].Id == asked) variant = i;
            if (current.Variants[i].Text == running?.Text) runs = i;
        }
        for (int i = 0; i < variants.Length; i++)
        {
            var v = current.Variants[i];
            // Marked in the list only when it is not simply the one chosen.
            variants[i] = new(v.Text, i == runs && runs != variant ? Strings.T("tvRunning").ToLowerInvariant() : "",
                              true, current.VariantInfo(v.Id));
        }

        // The box shows what is running. That is the variant chosen, except when the choice
        // was left to the technique — then the choice and what it came to — or overridden,
        // and then only what runs: the choice is not in force.
        string preview = runs < 0 || runs == variant ? variants[variant].Text
            : overridden is null ? $"{variants[variant].Text} · {variants[runs].Text}"
            : variants[runs].Text;
        int shown = overridden is not null && runs >= 0 ? runs : variant;

        // A list that cannot be opened cannot speak for its rows, so the label does.
        width = BeginRow(Strings.T(current.VariantsLabel), settable || overridden is not null,
                         settable ? null : current.VariantInfo(current.Variants[shown].Id));
        int variantAt = settable ? Math.Min(model.OpenVariantAt, variants.Length - 1) : -1;
        model.OpenVariantAt = -1;
        // Overridden, the box is shut but what it shows is what runs, so it is not greyed.
        if (Widgets.Combo(id + "variant", preview, variants, variant, out int picked, width, settable, variantAt,
                          valueStands: overridden is not null))
            current.SetVariant(current.Variants[picked].Id);

        // Why it cannot be set and what to do about it, written out: this is something to
        // act on, not something to look up.
        if (overridden is not null) Note(overridden, Theme.Caution);
    }

    /// <summary>
    /// What a technique's row in the list says about itself: its full name, what it does
    /// and what it costs — or, for one that cannot be used, why not in place of the cost.
    /// </summary>
    private static string ItemInfo(Technique technique, string? why) =>
        $"{technique.Name()}\n{technique.Description()}\n" +
        (why ?? string.Format(Strings.T("costPerFrame"), technique.CostText));

    /// <summary>
    /// What the technique's info button says, which is what no single row of the list
    /// does: anything the caller adds about the choice as a whole, and each technique that
    /// cannot be used with its reason — those sharing a reason named together, so a
    /// sentence is not repeated per technique. Empty when there is neither.
    /// </summary>
    private static string TechniqueInfo(IReadOnlyList<Technique> techniques, string extra)
    {
        var text = new System.Text.StringBuilder(extra);

        string? reason = null;
        foreach (var technique in techniques)
        {
            string? why = technique.Unavailable();
            if (why is null) continue;

            if (why == reason) text.Append(", ").Append(technique.Short());
            else
            {
                if (reason is not null) text.Append(": ").Append(reason);
                if (text.Length > 0) text.Append("\n\n");
                text.Append(technique.Short());
                reason = why;
            }
        }
        if (reason is not null) text.Append(": ").Append(reason);
        return text.ToString();
    }

    /// <summary>
    /// Starts a row: writes its label, with an info button after it when there is something
    /// to say, and leaves the cursor where the control goes. Returns the width the control
    /// has. A row is one control tall; a control that is not must claim that height itself.
    /// </summary>
    private static float BeginRow(string label, bool enabled = true, string? info = null,
                                  bool warning = false, bool shown = false)
    {
        var f = Widgets.F;
        ImGui.Dummy(new Vector2(0, Layout.S(RowGap)));

        var pos = ImGui.GetCursorScreenPos();
        float inset = Layout.S(RowInset), labelWidth = Layout.S(RowLabel), row = Layout.S(RowHeight);
        float control = ImGui.GetContentRegionAvail().X - inset * 2 - labelWidth;

        var size = Widgets.Measure(f.Body, label);
        Widgets.DrawText(ImGui.GetWindowDrawList(), f.Body, new Vector2(pos.X + inset, pos.Y + (row - size.Y) / 2f),
                         enabled ? Theme.TextPrimary : Theme.TextDisabled, label);

        if (info is { Length: > 0 })
        {
            ImGui.SetCursorScreenPos(new Vector2(pos.X + inset + size.X + Layout.S(4), pos.Y + (row - Layout.S(20)) / 2f));
            Widgets.InfoButton("##info" + label, info, warning, shown);
        }

        ImGui.SetCursorScreenPos(new Vector2(pos.X + inset + labelWidth, pos.Y));
        return control;
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
