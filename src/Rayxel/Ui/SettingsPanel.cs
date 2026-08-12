using System.Numerics;
using ImGuiNET;
using Rayxel.Capture;
using Rayxel.Native;
using Rayxel.Render;

namespace Rayxel.Ui;

/// <summary>
/// The panel down the right edge, to the revised handoff.
/// <para>
/// Opaque, not glass. The design specifies a blur behind it; a blur needs a second pass
/// over the frame inside an 8.33 ms budget, so the alpha carries what the blur would have.
/// Over live video the semi-transparent version was simply unreadable — the picture moves
/// and the text lands on whatever colour is behind it that frame.
/// </para>
/// <para>
/// Nothing here moves ImGui's cursor without a real item claiming the space afterwards.
/// Positioning by SetCursorScreenPos alone is what tripped the "code uses SetCursorPos to
/// extend window boundaries" assertion when the mode table opened.
/// </para>
/// </summary>
public static class SettingsPanel
{
    /// <summary>Base width at zoom 1. 352 × 1.30 = 458px on a 1440-tall display.</summary>
    public const float Width = 352f;

    public static void Draw(AppModel model, Fonts fonts)
    {
        float width = Layout.S(Width);
        ImGui.SetNextWindowPos(new Vector2(model.Renderer.Width - width, 0), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, model.Renderer.Height), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Layout.S(13), Layout.S(12)));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Layout.S(8), Layout.S(8)));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.PanelSolid);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, new Vector4(1, 1, 1, 0.14f));

        // The whole panel is one scroll area, per the revision.
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBringToFrontOnFocus;

        if (ImGui.Begin("##settings", flags))
        {
            Header(model, fonts);
            DeviceCard(model, fonts);
            CaptureMode(model, fonts);
            Scaler(model, fonts);
            if (model.ScalerIndex == 1) Sharpness(model, fonts);
            HdrCard(model, fonts);
            AudioCard(model, fonts);
            WindowModeSelect(model, fonts);
            LanguageSelect(model, fonts);

            // The footer group sits at the bottom edge while the content is shorter than
            // the panel, and scrolls away with everything else when it is not.
            float footerHeight = Layout.S(112);
            float slack = ImGui.GetContentRegionAvail().Y - footerHeight;
            if (slack > 0) ImGui.Dummy(new Vector2(0, slack));
            Footer(model, fonts);

            if (ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows
                                      | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)
                || ImGui.IsAnyItemActive()
                || ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
                model.PanelActivity();
        }
        ImGui.End();

        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
    }

    // ── shared pieces ────────────────────────────────────────────────────────────

    private static bool PushFont(ImFontPtr font)
    {
        unsafe { if (font.NativePtr == null) return false; }
        ImGui.PushFont(font);
        return true;
    }

    private static void DrawText(ImDrawListPtr draw, ImFontPtr font, Vector2 at, Vector4 colour, string text)
    {
        bool pushed = PushFont(font);
        draw.AddText(at, Theme.U32(colour), text);
        if (pushed) ImGui.PopFont();
    }

    /// <summary>Section labels are lowercase in every language except German.</summary>
    private static void SectionLabel(Fonts fonts, string key)
    {
        var text = Strings.T(key);
        if (Strings.Current != "de") text = text.ToLowerInvariant();
        Widgets.Text(fonts.Small, Theme.TextMuted, text);
    }

    /// <summary>
    /// A full-width select shaped like the design's: rounded row, value on the left, caret
    /// on the right. ImGui's own combo frame does not match, so only the popup is reused.
    /// </summary>
    private static bool Select(Fonts fonts, string id, string value, float height,
                               IReadOnlyList<string> options, int current, out int chosen)
    {
        chosen = current;
        float width = ImGui.GetContentRegionAvail().X;
        var start = ImGui.GetCursorScreenPos();

        ImGui.SetNextItemWidth(width);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding,
            new Vector2(Layout.S(11), (height - ImGui.GetTextLineHeight()) / 2f));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Theme.CardRow);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Theme.CardHover);
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.TextPrimary);

        bool pushed = PushFont(fonts.Body);
        bool changed = false;
        if (ImGui.BeginCombo(id, value, ImGuiComboFlags.HeightRegular))
        {
            for (int i = 0; i < options.Count; i++)
                if (ImGui.Selectable(options[i], i == current) && i != current) { chosen = i; changed = true; }
            ImGui.EndCombo();
        }
        if (pushed) ImGui.PopFont();

        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar();
        _ = start;
        return changed;
    }

    // ── header ───────────────────────────────────────────────────────────────────

    private static void Header(AppModel model, Fonts fonts)
    {
        Widgets.StatusDot(model.StatusColour, 8f);
        ImGui.SameLine(0, Layout.S(8));
        Widgets.Text(fonts.Name, model.StatusColour, model.StatusWord);

        float box = Layout.S(22);
        Widgets.RightAlign(box);
        if (CloseCross("##close", box)) model.ShowSettings = false;
    }

    private static bool CloseCross(string id, float box)
    {
        var pos = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton(id, new Vector2(box, box));
        bool hovered = ImGui.IsItemHovered();

        var draw = ImGui.GetWindowDrawList();
        if (hovered)
            draw.AddRectFilled(pos, pos + new Vector2(box, box),
                Theme.U32(new Vector4(1, 1, 1, 0.07f)), Layout.S(6));

        var tint = Theme.U32(hovered ? Theme.TextPrimary : Theme.Alpha(Theme.TextPrimary, 0.4f));
        float inset = box * 0.31f;
        float thick = Math.Max(1f, Layout.S(1.3f));
        draw.AddLine(pos + new Vector2(inset, inset), pos + new Vector2(box - inset, box - inset), tint, thick);
        draw.AddLine(pos + new Vector2(box - inset, inset), pos + new Vector2(inset, box - inset), tint, thick);
        return clicked;
    }

    // ── device ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Device name and link rate, then what the hardware actually is. The revision moved
    /// this to the top: it answers "what am I even looking at" before any setting does.
    /// </summary>
    private static void DeviceCard(AppModel model, Fonts fonts)
    {
        var start = Widgets.BeginCard(Theme.CardRow, 12f, 9f);

        float rowH = ImGui.GetFrameHeight();
        var rowStart = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X;
        var draw = ImGui.GetWindowDrawList();

        float dotR = Layout.S(6) / 2f;
        draw.AddCircleFilled(new Vector2(rowStart.X + dotR, rowStart.Y + rowH / 2f), dotR, Theme.U32(Theme.Good));

        var rate = LinkRate(model);
        float rateWidth = rate.Length > 0 ? Widgets.Measure(fonts.MonoSmall, rate).X + Layout.S(8) : 0;

        var devices = model.Devices.Where(d => Selectable(model, d)).ToList();
        int currentIndex = Math.Max(0, devices.FindIndex(d => model.Pump.Device.Is(d)));

        ImGui.SetCursorScreenPos(new Vector2(rowStart.X + dotR * 2 + Layout.S(8), rowStart.Y));
        ImGui.SetNextItemWidth(Math.Max(Layout.S(60), width - dotR * 2 - Layout.S(8) - rateWidth));
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0, 0, 0, 0));
        bool pushed = PushFont(fonts.Body);
        if (ImGui.BeginCombo("##device", model.Pump.Device.FriendlyName, ImGuiComboFlags.NoArrowButton))
        {
            for (int i = 0; i < devices.Count; i++)
                if (ImGui.Selectable(devices[i].FriendlyName, i == currentIndex) && i != currentIndex)
                    model.SwitchDevice(devices[i]);
            ImGui.EndCombo();
        }
        if (pushed) ImGui.PopFont();
        ImGui.PopStyleColor();

        if (rate.Length > 0)
        {
            var size = Widgets.Measure(fonts.MonoSmall, rate);
            DrawText(draw, fonts.MonoSmall, new Vector2(rowStart.X + width - size.X, rowStart.Y + (rowH - size.Y) / 2f),
                     Theme.Alpha(Theme.TextPrimary, 0.38f), rate);
        }

        ImGui.SetCursorScreenPos(rowStart);
        ImGui.Dummy(new Vector2(width, rowH));

        Widgets.Divider(4f);

        InfoRow(fonts, "adapter", model.Renderer.AdapterName);
        InfoRow(fonts, "driver", model.Renderer.DriverVersion);
        InfoRow(fonts, "bus", model.Active is { } a && !a.Device.IsVirtual && a.Link.Known
            ? a.Link.LinkClass : "-");
        InfoRow(fonts, "port", model.Pump.Device.ShortId);

        Widgets.EndCard(start, Theme.CardRow, 12f, 9f);
    }

    private static bool Selectable(AppModel model, CaptureDeviceInfo d) =>
        !model.Probed.TryGetValue(d.SymbolicLink, out var p)
        || p.Availability is not (DeviceAvailability.Busy or DeviceAvailability.Unusable);

    private static void InfoRow(Fonts fonts, string labelKey, string value)
    {
        var label = Strings.T(labelKey);
        if (Strings.Current != "de") label = label.ToLowerInvariant();

        Widgets.Text(fonts.Small, Theme.Alpha(Theme.TextPrimary, 0.4f), label);
        float w = Widgets.Measure(fonts.MonoSmall, value).X;
        ImGui.SameLine(Math.Max(0, Widgets.RightEdge() - w));
        Widgets.Text(fonts.MonoSmall, Theme.TextSecondary, value);
    }

    /// <summary>
    /// Link rate for the running mode, in Gbit/s: w × h × bpp × fps × 8 / 1e9, and a ninth
    /// of that when a codec is doing the carrying.
    /// </summary>
    private static string LinkRate(AppModel model)
    {
        var m = model.Pump.CurrentMode;
        if (m is null || model.Pump.CurrentFps <= 0) return "";

        double bytes = m.Format.BytesPerFrame(m.Width, m.Height) * model.Pump.CurrentFps;
        if (m.HasCodecInPath) bytes /= 9.0;
        return $"{bytes * 8 / 1e9:0.0} Gbit/s";
    }

    // ── capture mode ─────────────────────────────────────────────────────────────

    private static void CaptureMode(AppModel model, Fonts fonts)
    {
        var mode = model.Pump.CurrentMode;
        SectionLabel(fonts, "resolution");

        float height = ImGui.GetFrameHeight() + Layout.S(6);
        var start = ImGui.GetCursorScreenPos();
        float width = ImGui.GetContentRegionAvail().X;

        bool clicked = ImGui.InvisibleButton("##modebtn", new Vector2(width, height));
        bool hovered = ImGui.IsItemHovered();
        if (clicked) model.ModeTableOpen = !model.ModeTableOpen;

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + new Vector2(width, height),
            Theme.U32(hovered ? Theme.CardHover : Theme.CardRow), Layout.S(Theme.RadiusCard));

        if (mode is not null)
        {
            float pad = Layout.S(12);
            var res = $"{mode.Width}×{mode.Height}";
            var rate = $"{model.Pump.CurrentFps:0.##} fps";
            var resSize = Widgets.Measure(fonts.MonoMode, res);
            var rateSize = Widgets.Measure(fonts.Body, rate);
            float baseY = start.Y + (height - resSize.Y) / 2f;

            DrawText(draw, fonts.MonoMode, new Vector2(start.X + pad, baseY), Theme.TextPrimary, res);

            float x = start.X + pad + resSize.X + Layout.S(8);
            float sub = baseY + (resSize.Y - rateSize.Y);
            DrawText(draw, fonts.Body, new Vector2(x, sub), Theme.TextSecondary, rate);
            DrawText(draw, fonts.Small, new Vector2(x + rateSize.X + Layout.S(7), sub),
                     mode.Format.IsHighBitDepth ? Theme.Warn : Theme.TextMuted, mode.Format.Name);

            var (colour, alpha) = WireColour(mode);
            var tagSize = Widgets.Measure(fonts.MonoTiny, mode.WireTag);
            var tagPad = new Vector2(Layout.S(8), Layout.S(3));
            float caret = Layout.S(14);
            var tagPos = new Vector2(start.X + width - pad - caret - tagSize.X - tagPad.X * 2,
                                     start.Y + (height - tagSize.Y - tagPad.Y * 2) / 2f);
            draw.AddRectFilled(tagPos, tagPos + tagSize + tagPad * 2,
                               Theme.U32(Theme.Alpha(colour, alpha)), Theme.RadiusPill);
            DrawText(draw, fonts.MonoTiny, tagPos + tagPad, colour, mode.WireTag);

            Caret(draw, new Vector2(start.X + width - pad - caret / 2f, start.Y + height / 2f),
                  Layout.S(4), Theme.TextMuted);
        }

        if (model.ModeTableOpen) ModeTable(model, fonts);
    }

    private static void Caret(ImDrawListPtr draw, Vector2 centre, float size, Vector4 colour)
    {
        draw.AddTriangleFilled(
            centre + new Vector2(-size, -size * 0.5f),
            centre + new Vector2(size, -size * 0.5f),
            centre + new Vector2(0, size * 0.6f),
            Theme.U32(colour));
    }

    /// <summary>
    /// Red used to mean "claims to be uncompressed and is not", which was worth alarming
    /// about while such a mode sat next to an identical honest one. Those are hidden now,
    /// so every remaining codec mode buys a rate no raw mode reaches — a real choice, not a
    /// trap. Amber for "a codec is in the path", green for none, and nothing shouts.
    /// </summary>
    private static (Vector4, float) WireColour(CaptureMode m) =>
        m.Transport == WireTransport.Raw ? (Theme.Good, 0.14f) : (Theme.Warn, 0.14f);

    /// <summary>
    /// The mode table. Only NV12 and P010 are listed — nothing else can be displayed, and
    /// offering a mode that shows a blank window is not a choice.
    /// </summary>
    private static void ModeTable(AppModel model, Fonts fonts)
    {
        var probe = model.Active;
        if (probe is null) return;

        var modes = probe.Renderable.Where(m => m.IsOfferable)
            .OrderByDescending(m => m.Width * m.Height)
            .ThenByDescending(m => m.MaxFps)
            .ToList();

        float rowH = Layout.S(24);
        float listH = Math.Min(Layout.S(250), rowH * modes.Count + Layout.S(6));

        // Column starts. The design's `1fr 52px 44px 56px` gained a latency column: the
        // wire tag shrank from `mjpeg?` to `raw`/`zip` and paid for it.
        float total = ImGui.GetContentRegionAvail().X;
        float cFps = total - Layout.S(172), cFmt = total - Layout.S(134),
              cMs = total - Layout.S(92), cWire = total - Layout.S(34);

        HeaderRow(fonts, cFps, cFmt, cMs, cWire);

        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Popover);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, Layout.S(1)));
        if (ImGui.BeginChild("##modetable", new Vector2(0, listH), ImGuiChildFlags.None))
        {
            foreach (var m in modes)
            {
                bool current = model.Pump.CurrentMode?.TypeIndex == m.TypeIndex;
                var rowStart = ImGui.GetCursorScreenPos();
                float w = ImGui.GetContentRegionAvail().X;

                if (ImGui.InvisibleButton($"##m{m.TypeIndex}", new Vector2(w, rowH)) && !current)
                {
                    model.SelectMode(m, m.MaxFps);
                    model.ModeTableOpen = false;
                }
                bool hovered = ImGui.IsItemHovered();

                var draw = ImGui.GetWindowDrawList();
                if (current || hovered)
                    draw.AddRectFilled(rowStart, rowStart + new Vector2(w, rowH),
                        Theme.U32(current ? Theme.CardActive : Theme.CardHover), Layout.S(Theme.RadiusTableRow));

                var colour = current ? Theme.AccentText : Theme.TextSecondary;
                float ty = rowStart.Y + (rowH - Widgets.Measure(fonts.MonoSmall, "0").Y) / 2f;

                DrawText(draw, fonts.MonoSmall, new Vector2(rowStart.X + Layout.S(9), ty), colour, $"{m.Width}×{m.Height}");
                DrawText(draw, fonts.MonoSmall, new Vector2(rowStart.X + cFps, ty), colour, $"{m.MaxFps:0}");

                // 10-bit is the one choice here that quietly costs something: twice the
                // bytes, twice the wire time, and a delivery tail that falls apart. It is
                // marked on the format rather than on the time, because the time alone
                // does not say why.
                DrawText(draw, fonts.MonoSmall, new Vector2(rowStart.X + cFmt, ty),
                         m.Format.IsHighBitDepth ? Theme.Warn : Theme.TextMuted, m.Format.Name);

                // What this mode costs before anything of ours runs. Blank for a codec
                // mode: fewer bytes cross the wire, but a decode follows and the total is
                // not something we can state from the mode list.
                double ms = m.WireMs(probe.WireMiBs);
                DrawText(draw, fonts.MonoSmall, new Vector2(rowStart.X + cMs, ty),
                         m.Format.IsHighBitDepth ? Theme.Warn : Theme.TextSecondary,
                         ms > 0 ? $"{ms:0.0}" : "—");

                var (wireColour, _) = WireColour(m);
                DrawText(draw, fonts.MonoTiny, new Vector2(rowStart.X + cWire, ty), wireColour, m.WireTag);
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();

        var summary = $"{modes.Count} / {probe.Modes.Count}";
        Widgets.Text(fonts.MonoTiny, Theme.TextMuted, summary);
        if (probe.WireMiBs > 0 && !probe.Device.IsVirtual)
        {
            // The handoff's copy deck had "356 MiB/s" written into the string itself — this
            // machine's number, baked into all five languages. It is the divisor for the
            // column beside it, so it has to come from the device.
            var cap = string.Format(Strings.T("capNote"), probe.WireMiBs.ToString("0"));
            float w = Widgets.Measure(fonts.MonoTiny, cap).X;
            ImGui.SameLine(Math.Max(0, Widgets.RightEdge() - w));
            Widgets.Text(fonts.MonoTiny, Theme.TextMuted, cap);
        }

        // Said once under the table rather than on every 10-bit row. The rows are already
        // amber; repeating a sentence nine times is how a panel stops being read.
        if (modes.Any(m => m.Format.IsHighBitDepth))
            Widgets.Text(fonts.MonoTiny, Theme.Warn, Strings.T("tenBitCost"));
    }

    private static void HeaderRow(Fonts fonts, float cFps, float cFmt, float cMs, float cWire)
    {
        var start = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var colour = Theme.Alpha(Theme.TextPrimary, 0.36f);

        DrawText(draw, fonts.Micro, start, colour, Strings.T("thRes"));
        DrawText(draw, fonts.Micro, start + new Vector2(cFps, 0), colour, Strings.T("thFps"));
        DrawText(draw, fonts.Micro, start + new Vector2(cFmt, 0), colour, Strings.T("thFmt"));
        DrawText(draw, fonts.Micro, start + new Vector2(cMs, 0), colour, Strings.T("thDelay"));
        DrawText(draw, fonts.Micro, start + new Vector2(cWire, 0), colour, Strings.T("thWire"));

        ImGui.Dummy(new Vector2(0, Widgets.Measure(fonts.Micro, "0").Y));
    }

    // ── scaler ───────────────────────────────────────────────────────────────────

    private static void Scaler(AppModel model, Fonts fonts)
    {
        SectionLabel(fonts, "upscaler");

        int current = model.ScalerIndex;
        var proc = model.Renderer.Processor;
        bool driverOk = model.DriverPathAvailable && proc?.SuperResolutionSupported == true;

        string ScaleOn(int index) => index == current ? model.ScaleLabel : "";

        // Measured on this machine with --bench, not taken from a vendor sheet: against a
        // 0.19 ms baseline of our own blit, NIS came out at 0.19 and VSR at 4.29. NIS is
        // free to inside the noise, which the 0.9 ms figure that used to sit here was not
        // saying. They stay static rather than live because a number that moves while you
        // read it cannot be compared to the one above it.
        if (Widgets.RadioRow(fonts, Strings.T("pNone"), "", "—", current == 0,
                             true, ScaleOn(0), model.ScaleColour))
            model.SelectScaler(0);

        if (Widgets.RadioRow(fonts, Strings.T("pNis"), "", "< 0.1 ms", current == 1,
                             true, ScaleOn(1), model.ScaleColour))
            model.SelectScaler(1);

        var vsrLabel = proc?.Vendor == VideoProcessor.GpuVendor.Intel
            ? Strings.T("pVsrIntel") : Strings.T("pVsr");

        // A row only gets a second line when something is wrong.
        var vsrNote = !driverOk ? Strings.T("naVendor")
                    : model.ScalerInert && current == 2 ? Strings.T("vsrSmall")
                    : "";

        if (Widgets.RadioRow(fonts, vsrLabel, vsrNote, "+4.1 ms", current == 2, driverOk,
                             ScaleOn(2), model.ScaleColour) && driverOk)
            model.SelectScaler(2);
    }

    private static void Sharpness(AppModel model, Fonts fonts)
    {
        var nis = model.Renderer.Nis;
        if (nis is null) return;

        var start = Widgets.BeginCard(Theme.CardInset, 11f, 9f);
        float value = nis.Sharpness;

        // Above 0.5 the value itself turns amber. No sentence — the threshold is carried
        // by colour, which is the revision's rule everywhere.
        Widgets.LabelValue(fonts.Label, Theme.TextSecondary, Strings.T("sharpness"),
                           fonts.MonoSlider, value > 0.5f ? Theme.Warn : Theme.AccentText, $"{value:0.0}");

        if (ImGui.SliderFloat("##sharp", ref value, 0f, 1f, "")) { nis.Sharpness = value; model.Touch(); }
        Widgets.EndCard(start, Theme.CardInset, 11f, 9f);
    }

    // ── RTX HDR + overlay brightness ─────────────────────────────────────────────

    private static void HdrCard(AppModel model, Fonts fonts)
    {
        var (can, reason) = model.HdrReachable();
        bool on = model.HdrOn;

        var start = Widgets.BeginCard(Theme.CardInset, 11f, 8f);

        Widgets.Text(fonts.Heading, can ? Theme.TextPrimary : Theme.Alpha(Theme.TextPrimary, 0.55f),
                     Strings.T("hdr"));

        float toggleWidth = Layout.S(36);

        // The cost is blank while it is off — it costs nothing then.
        if (on)
        {
            const string cost = "+0.66";
            float costWidth = Widgets.Measure(fonts.MonoCost, cost).X;
            Widgets.RightAlign(toggleWidth + Layout.S(9) + costWidth);
            Widgets.Text(fonts.MonoCost, Theme.Warn, cost);
        }

        Widgets.RightAlign(toggleWidth);
        if (Widgets.Toggle("##hdr", ref on, can) && can) model.ToggleHdr();

        // When it is available there is no note at all.
        if (!can) Widgets.Text(fonts.Small, Theme.Alpha(Theme.Warn, 0.8f), reason);

        // Both levels live here, and both matter whenever the surface is PQ — not only
        // while the driver filter is on. They are two different pictures: the interface
        // must stay dim enough not to burn an OLED, the video should look like the console
        // intended, and one number cannot serve both.
        if (model.Renderer.SwapChainIsPq)
        {
            Widgets.Divider(4f);

            // Our shader only encodes the video while the driver filter is off; with RTX
            // HDR on the driver produces the PQ values and this does nothing.
            bool videoOurs = !model.HdrOn;
            ImGui.BeginDisabled(!videoOurs);
            float video = model.VideoNits;
            Widgets.LabelValue(fonts.Label, videoOurs ? Theme.TextSecondary : Theme.TextMuted,
                               Strings.T("videoBright"),
                               fonts.MonoSlider, videoOurs ? Theme.AccentText : Theme.TextMuted,
                               $"{video:0} nits");
            if (ImGui.SliderFloat("##videonits", ref video, 80f, 600f, "") && videoOurs)
            {
                model.VideoNits = MathF.Round(video / 10f) * 10f;
                model.Touch();
            }
            ImGui.EndDisabled();
            if (!videoOurs) Widgets.Text(fonts.Small, Theme.TextMuted, Strings.T("videoBrightDriver"));

            ImGui.Dummy(new Vector2(0, Layout.S(3)));

            float nits = model.OverlayNits;
            Widgets.LabelValue(fonts.Label, Theme.TextSecondary, Strings.T("hdrBright"),
                               fonts.MonoSlider, nits > 250f ? Theme.Warn : Theme.AccentText, $"{nits:0} nits");

            if (ImGui.SliderFloat("##nits", ref nits, 80f, 400f, ""))
            {
                model.OverlayNits = MathF.Round(nits / 10f) * 10f;
                model.Touch();
            }
        }

        Widgets.EndCard(start, Theme.CardInset, 11f, 8f);
    }

    // ── audio ────────────────────────────────────────────────────────────────────

    private static void AudioCard(AppModel model, Fonts fonts)
    {
        var start = Widgets.BeginCard(Theme.CardInset, 11f, 8f);

        bool on = model.AudioEnabled;
        Widgets.Text(fonts.Heading, Theme.TextPrimary, Strings.T("audio"));
        Widgets.RightAlign(Layout.S(36));
        if (Widgets.Toggle("##audio", ref on))
        {
            model.AudioEnabled = on;
            model.RestartAudio();
            model.Touch();
        }

        if (model.AudioEnabled)
        {
            if (model.AudioInputs.Count == 0)
            {
                Widgets.Text(fonts.Small, Theme.Alpha(Theme.Warn, 0.8f), Strings.T("audioNoInput"));
            }
            else
            {
                Endpoint(model, fonts, "audioIn", model.AudioInputs, model.AudioInputId,
                    id => { model.AudioInputId = id; model.RestartAudio(); model.Touch(); });

                Endpoint(model, fonts, "audioOut", model.AudioOutputs, model.AudioOutputId,
                    id => { model.AudioOutputId = id; model.RestartAudio(); model.Touch(); });

                if (!model.Audio.Running && model.Audio.LastError is { } err)
                    Widgets.Text(fonts.Small, Theme.Alpha(Theme.Bad, 0.85f), err);
            }
        }

        Widgets.EndCard(start, Theme.CardInset, 11f, 8f);
    }

    private static void Endpoint(AppModel model, Fonts fonts, string labelKey,
                                 List<Audio.AudioEndpointInfo> options, string? currentId,
                                 Action<string> pick)
    {
        var current = options.FirstOrDefault(o => o.Id == currentId)
                      ?? options.FirstOrDefault(o => o.IsDefault)
                      ?? options[0];

        SectionLabel(fonts, labelKey);
        var names = options.Select(o => o.Name).ToList();
        int index = options.IndexOf(current);

        if (Select(fonts, $"##{labelKey}", current.Name, Layout.S(34), names, index, out int chosen))
            pick(options[chosen].Id);
    }

    // ── window mode, language ────────────────────────────────────────────────────

    private static void WindowModeSelect(AppModel model, Fonts fonts)
    {
        SectionLabel(fonts, "winMode");

        string[] names = [Strings.T("w1"), Strings.T("w2"), Strings.T("w3")];
        int current = model.WindowKind switch
        {
            WindowKind.Windowed => 0,
            WindowKind.Fullscreen => 1,
            _ => 2
        };

        if (Select(fonts, "##winmode", names[current], Layout.S(36), names, current, out int chosen))
            model.SetWindowKind(chosen switch
            {
                0 => WindowKind.Windowed,
                1 => WindowKind.Fullscreen,
                _ => WindowKind.BorderlessFullscreen
            });
    }

    private static void LanguageSelect(AppModel model, Fonts fonts)
    {
        SectionLabel(fonts, "language");
        if (Select(fonts, "##lang", Strings.LanguageNames[Strings.CurrentIndex], Layout.S(36),
                   Strings.LanguageNames, Strings.CurrentIndex, out int chosen))
        {
            Strings.Use(chosen);
            model.Touch();
        }
    }

    // ── footer ───────────────────────────────────────────────────────────────────

    private static void Footer(AppModel model, Fonts fonts)
    {
        Widgets.Divider(4f);

        bool tele = model.ShowTelemetry;
        Widgets.Text(fonts.Heading, Theme.TextPrimary, Strings.T("telemetry"));
        ImGui.SameLine(0, Layout.S(7));
        Widgets.Text(fonts.MonoTiny, Theme.TextFaint, "T");
        Widgets.RightAlign(Layout.S(36));
        if (Widgets.Toggle("##tele", ref tele)) { model.ShowTelemetry = tele; model.Touch(); }

        ImGui.PushTextWrapPos(0);
        Widgets.Text(fonts.MonoTiny, Theme.TextFaint,
            $"Tab {Strings.T("kUi")} · T {Strings.T("kTele")} · M {Strings.T("kModes")} · " +
            $"F11 {Strings.T("kWin")} · 1/2/3 {Strings.T("kPath")}");
        ImGui.PopTextWrapPos();

        ExitButton(model, fonts);
    }

    /// <summary>
    /// Quit, full width at the bottom. The handoff has this return to the device picker;
    /// it asks and then closes instead, because that is what was asked for here and what
    /// the confirmation dialog already exists to guard.
    /// </summary>
    private static void ExitButton(AppModel model, Fonts fonts)
    {
        float width = ImGui.GetContentRegionAvail().X;
        float height = Layout.S(36);
        var start = ImGui.GetCursorScreenPos();

        bool clicked = ImGui.InvisibleButton("##quit", new Vector2(width, height));
        bool hovered = ImGui.IsItemHovered();

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + new Vector2(width, height),
            Theme.U32(hovered ? Theme.Alpha(Theme.Bad, 0.16f) : new Vector4(1, 1, 1, 0.06f)),
            Layout.S(Theme.RadiusCard));

        var label = Strings.T("exit");
        var size = Widgets.Measure(fonts.Body, label);
        DrawText(draw, fonts.Body, start + new Vector2((width - size.X) / 2f, (height - size.Y) / 2f),
                 hovered ? Theme.Bad : Theme.TextPrimary, label);

        if (clicked) model.ConfirmQuit = true;
    }
}
