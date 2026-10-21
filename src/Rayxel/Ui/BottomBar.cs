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
    // Measured on this machine with --bench rather than taken from a vendor sheet: against
    // our own blit NIS is free to inside the noise and RTX VSR costs 4.1 ms, HDR on top of
    // it 0.66. Shown as fixed figures, because a cost that moves while it is being read
    // cannot be compared with the one beside it.
    private const double NisCostMs = 0.1, VsrCostMs = 4.1, HdrCostMs = 0.66;

    // This frame's layout, for anchoring what opens above the bar. The bar is always
    // drawn first, so these are current by the time a flyout reads them.
    private static Vector2 _barMin, _modeMin, _scalerMin, _moreMax;

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
            HdrSwitch(model, line);
            Widgets.VerticalSeparator(line);
            Budget(model, line);
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

    /// <summary>The scaler in use and what it costs, opening the choice of three.</summary>
    private static void ScalerChip(AppModel model, float line)
    {
        var f = Widgets.F;
        int index = model.ScalerIndex;

        string label = index switch
        {
            1 => Strings.T("pNis"),
            2 => model.Renderer.Processor?.Vendor == VideoProcessor.GpuVendor.Intel ? Strings.T("pVsrIntel") : Strings.T("pVsr"),
            _ => Strings.T("pNone")
        };
        string cost = index switch
        {
            1 => "< " + Strings.Ms(NisCostMs, "0.0"),
            2 => "+" + Strings.Ms(VsrCostMs, "0.0"),
            _ => ""
        };

        // Paying for an upscaler that has nothing to enlarge is worth a colour: the cost
        // is real and the picture is unchanged.
        bool wasted = index == 2 && model.ScalerInert;

        ImGui.SameLine(0, 0);
        float pad = Layout.S(10), gap = Layout.S(8), icon = Layout.S(16), chevron = Layout.S(12);
        var labelSize = Widgets.Measure(f.Body, label);
        var costSize = Widgets.Measure(f.Caption, cost);
        float width = pad + icon + gap + labelSize.X + (cost.Length > 0 ? gap + costSize.X : 0) + gap + chevron + pad;

        bool open = model.Flyout == Flyout.Scaler;
        if (Widgets.SubtleButton("##scalerchip", new Vector2(width, line), open, out var min))
            model.ToggleFlyout(Flyout.Scaler);
        _scalerMin = min;
        Widgets.Tooltip(wasted ? string.Format(Strings.T("scaleInert"), label) : Strings.T("ttScaler"));

        var draw = ImGui.GetWindowDrawList();
        float cy = min.Y + line / 2f, x = min.X + pad;

        Icons.Draw(draw, Icon.Scale, new Vector2(x, cy - icon / 2f), icon, Theme.TextPrimary);
        x += icon + gap;
        Widgets.DrawText(draw, f.Body, new Vector2(x, cy - labelSize.Y / 2f), Theme.TextPrimary, label);
        x += labelSize.X + gap;

        if (cost.Length > 0)
        {
            Widgets.DrawText(draw, f.Caption, new Vector2(x, cy - costSize.Y / 2f),
                             wasted ? Theme.Caution : Theme.TextSecondary, cost);
            x += costSize.X + gap;
        }

        Icons.Draw(draw, open ? Icon.ChevronDown : Icon.ChevronUp, new Vector2(x, cy - chevron / 2f), chevron, Theme.TextSecondary);
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

    /// <summary>
    /// What our own work costs against the frame, as a number and a thin bar under it.
    /// The split and the headroom are in the tooltip; the full picture is the telemetry.
    /// </summary>
    private static void Budget(AppModel model, float line)
    {
        var f = Widgets.F;
        bool live = model.Pump.State == CaptureState.Streaming && model.BudgetMs > 0;
        double spent = model.ShownGpuMs + model.ShownCpuMs;
        double budget = model.BudgetMs;

        string text = live
            ? string.Format(Strings.T("budgetOf"), Strings.Num(spent, "0.00"), Strings.Num(budget, "0.00"))
            : "—";

        float pad = Layout.S(8);
        var textSize = Widgets.Measure(f.Caption, text);
        float inner = Math.Max(Layout.S(96), textSize.X);
        float width = pad + inner + pad;

        var min = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton("##budget", new Vector2(width, line));
        if (live)
            Widgets.Tooltip(string.Format(Strings.T("ttBudget"),
                Strings.Num(model.ShownGpuMs, "0.00"), Strings.Num(model.ShownCpuMs, "0.00"),
                Strings.Num(budget, "0.00"), Strings.Num(model.HeadroomMs, "0.00")));

        var draw = ImGui.GetWindowDrawList();
        float track = Layout.S(4), gap = Layout.S(5);
        float top = min.Y + (line - textSize.Y - gap - track) / 2f;
        Widgets.DrawText(draw, f.Caption, new Vector2(min.X + pad, top), Theme.TextSecondary, text);

        var trackMin = new Vector2(min.X + pad, top + textSize.Y + gap);
        draw.AddRectFilled(trackMin, trackMin + new Vector2(inner, track), Theme.U32(Theme.Alpha(Theme.TextPrimary, 0.14f)), track / 2f);
        if (live)
        {
            float frac = (float)Math.Clamp(spent / budget, 0, 1);
            if (frac * inner >= 1f)
                draw.AddRectFilled(trackMin, trackMin + new Vector2(frac * inner, track),
                    Theme.U32(spent > budget ? Theme.Critical : Theme.Accent), track / 2f);
        }
    }

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

    /// <summary>
    /// None, NIS or the driver's upscaler, each with what it costs and one line on what it
    /// does. NIS brings its sharpness with it; the footer says how much enlarging there is
    /// to do at all, which is what decides whether any of this matters.
    /// </summary>
    private static bool ScalerFlyout(AppModel model)
    {
        var f = Widgets.F;
        BeginFlyout("##scalers", new Vector2(_scalerMin.X, _barMin.Y - Layout.S(8)), new Vector2(0, 1),
                    Layout.S(420), new Vector2(Layout.S(4)), Vector2.Zero);

        Header(Strings.T("scalerTitle"), Strings.T("perFrame"));

        var proc = model.Renderer.Processor;
        bool driverOk = model.DriverPathAvailable && proc?.SuperResolutionSupported == true;
        bool intel = proc?.Vendor == VideoProcessor.GpuVendor.Intel;
        string vsrName = intel ? Strings.T("pVsrIntel") : "RTX Video Super Resolution";

        ScalerRow(model, 0, Strings.T("pNone"), Strings.T("dNone"), Strings.Ms(0, "0"), true);
        ScalerRow(model, 1, Strings.T("pNis"), Strings.T("dNis"), "< " + Strings.Ms(NisCostMs, "0.0"), true);
        ScalerRow(model, 2, vsrName,
                  driverOk ? Strings.T(intel ? "dVsrIntel" : "dVsr") : Strings.Sentence(Strings.T("naVendor")),
                  "+" + Strings.Ms(VsrCostMs, "0.0"), driverOk);

        float inset = Layout.S(12);
        if (model.ScalerIndex == 1 && model.Renderer.Nis is { } nis)
        {
            ImGui.Dummy(new Vector2(0, Layout.S(8)));
            ImGui.Indent(inset);
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, Layout.S(4)));
            float width = ImGui.GetContentRegionAvail().X - inset;
            float sharp = nis.Sharpness;

            // Above 0.5 the value turns amber: at the small scale factors this app usually
            // runs at, that reads as oversharpening rather than as detail.
            Widgets.LabelValueWidth(Strings.T("sharpness"), Strings.Num(sharp, "0.00"),
                                    sharp > 0.5f ? Theme.Caution : Theme.TextSecondary, width);
            if (Widgets.Slider("##sharp", ref sharp, 0f, 1f, true, width))
            {
                nis.Sharpness = MathF.Round(sharp * 20f) / 20f;
                model.Touch();
            }
            ImGui.PopStyleVar();
            ImGui.Unindent(inset);
        }

        // How much enlarging there is to do at all. At ×1 or below nothing is enlarged and
        // every upscaler only costs.
        ImGui.Dummy(new Vector2(0, Layout.S(6)));
        ImGui.Indent(inset);
        ImGui.PushTextWrapPos(Widgets.ContentRight() - inset);
        if (model.Pump.CurrentMode is { Height: > 0 } mode && model.ScaleFactor > 0)
        {
            if (model.ScalerInert)
                Widgets.Text(f.Caption, Theme.Caution, string.Format(Strings.T("scaleInert"), vsrName));
            else
                Widgets.Text(f.Caption, Theme.TextTertiary, string.Format(Strings.T("scaleUp"), model.ScaleLabel,
                    mode.Height, (int)Math.Round(mode.Height * model.ScaleFactor)));
        }
        ImGui.PopTextWrapPos();
        ImGui.Unindent(inset);
        ImGui.Dummy(new Vector2(0, Layout.S(8)));

        return EndFlyout();
    }

    private static void ScalerRow(AppModel model, int index, string label, string description, string cost, bool enabled)
    {
        var f = Widgets.F;
        bool selected = model.ScalerIndex == index;

        if (Widgets.ListRow($"##scaler{index}", Layout.S(56), selected, enabled, out var min, out var max) && !selected)
        {
            model.SelectScaler(index);
            // NIS stays open: choosing it is when its sharpness becomes the next question.
            if (index != 1) model.Flyout = Flyout.None;
        }

        var draw = ImGui.GetWindowDrawList();
        float inset = Layout.S(12);
        var l = Widgets.Measure(f.Body, label);
        var d = Widgets.Measure(f.Caption, description);
        float top = min.Y + (max.Y - min.Y - l.Y - d.Y - Layout.S(1)) / 2f;

        Widgets.DrawText(draw, f.Body, new Vector2(min.X + inset, top), enabled ? Theme.TextPrimary : Theme.TextDisabled, label);
        Widgets.DrawText(draw, f.Caption, new Vector2(min.X + inset, top + l.Y + Layout.S(1)),
                         enabled ? Theme.TextTertiary : Theme.Alpha(Theme.Caution, 0.8f), description);

        var c = Widgets.Measure(f.Caption, cost);
        Widgets.DrawText(draw, f.Caption, new Vector2(max.X - inset - c.X, (min.Y + max.Y - c.Y) / 2f),
                         enabled ? Theme.TextSecondary : Theme.TextDisabled, cost);
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
