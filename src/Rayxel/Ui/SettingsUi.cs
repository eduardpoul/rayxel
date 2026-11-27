using System.Numerics;
using ImGuiNET;
using Rayxel.Capture;
using Rayxel.Native;
using Rayxel.Render;

namespace Rayxel.Ui;

/// <summary>
/// Every setting, in rows: what it is called on the left, an ordinary control on the right,
/// and under a row what there is to act on. The same rows make two surfaces - the panel that
/// slides over the picture, with what is changed while watching, and the Settings page with
/// all of it - so a setting looks and behaves the same wherever it is met.
/// <para>
/// They used to be four flyouts above a bar along the bottom edge. The picture now carries
/// nothing but the few buttons that come up with the pointer; this is where the rest went.
/// </para>
/// </summary>
internal static class SettingsUi
{
    private const float RowInset = 12f, RowHeight = 32f, RowGap = 6f;

    // The label column, and how wide a control may get: narrow in the panel, wider on the
    // page, where a control stays its own width at the right edge as in Windows' Settings.
    private static float _labelWidth = 140f, _controlMax = 240f;

    private const ImGuiWindowFlags PanelFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
        | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoCollapse
        | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
        | ImGuiWindowFlags.NoFocusOnAppearing;

    public static float PanelWidth => Layout.S(400);

    // ── the panel over the picture ───────────────────────────────────────────────

    /// <summary>
    /// The settings panel at the right edge, over the picture, which goes on underneath and
    /// shows every change at once: the picture, receiving over the network for a device on
    /// it, sound, window and telemetry. "All settings" opens the page with the rest.
    /// Returns whether the pointer is over it.
    /// </summary>
    public static bool Panel(AppModel model)
    {
        var f = Widgets.F;
        float width = PanelWidth, height = model.Renderer.Height;
        ImGui.SetNextWindowPos(new Vector2(model.Renderer.Width - width, 0), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(width, height), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.Surface);
        ImGui.PushStyleColor(ImGuiCol.Border, Theme.SurfaceStroke);

        bool hovered = false;
        if (ImGui.Begin("##settingspanel", PanelFlags))
        {
            float pad = Layout.S(20), header = Layout.S(56), footer = Layout.S(64);

            // The title and the way out.
            var draw = ImGui.GetWindowDrawList();
            var origin = ImGui.GetWindowPos();
            var title = Widgets.Measure(f.Subtitle, Strings.T("settingsTitle"));
            Widgets.DrawText(draw, f.Subtitle, origin + new Vector2(pad, (header - title.Y) / 2f), Theme.TextPrimary, Strings.T("settingsTitle"));
            ImGui.SetCursorPos(new Vector2(width - Layout.S(12) - Layout.S(36), (header - Layout.S(36)) / 2f));
            if (Widgets.IconButton("##closesettings", Icon.Close, false, Theme.TextPrimary, Strings.T("closeM")))
                model.Flyout = Flyout.None;

            // The rows, scrolling between the title and the footer.
            ImGui.SetCursorPos(new Vector2(0, header));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Layout.S(8), 0));
            if (ImGui.BeginChild("##settingsrows", new Vector2(width, height - header - footer), ImGuiChildFlags.AlwaysUseWindowPadding))
            {
                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Layout.S(8), 0));
                _labelWidth = 140f;
                _controlMax = 236f;

                SectionTitle(Strings.T("secPicture"));
                ModeRow(model);
                TechniqueRows(model, "##up", model.Upscalers, model.Upscaler, ScaleInfo(model), t => model.SelectScaler(t.Index));
                if (model.ScalerInert && model.Upscaler.Index != 0)
                    Note(string.Format(Strings.T("scaleInert"), model.Upscaler.Short()), Theme.Caution);
                TechniqueRows(model, "##fg", model.FrameGenerators, model.FrameGenerator, "", t => model.SelectFrameGen(t.Index));
                MotionRow(model, "##flow");
                HdrRow(model);
                if (model.Renderer.SwapChainIsPq) BrightnessRows(model);

                if (model.Pump.Device.Bus == DeviceBus.Network)
                {
                    SectionGap();
                    SectionTitle(Strings.T("secNetwork"));
                    NetworkRows(model);
                }

                SectionGap();
                SectionTitle(Strings.T("secSound"));
                SoundRows(model);

                SectionGap();
                SectionTitle(Strings.T("secWindow"));
                WindowRow(model);
                TelemetryRow(model);
                ImGui.Dummy(new Vector2(0, Layout.S(16)));
                ImGui.PopStyleVar();
            }
            ImGui.EndChild();
            ImGui.PopStyleVar();

            // The footer: the rest is on the page.
            var footerAt = origin + new Vector2(0, height - footer);
            draw.AddRectFilled(footerAt, origin + new Vector2(width, height), Theme.U32(Theme.Layer));
            draw.AddLine(footerAt, footerAt + new Vector2(width, 0), Theme.U32(Theme.Divider));
            string all = Strings.T("allSettings");
            var buttonSize = new Vector2(Widgets.Measure(f.Body, all).X + Layout.S(32), Layout.S(32));
            ImGui.SetCursorPos(new Vector2(width - pad - buttonSize.X, height - footer + (footer - buttonSize.Y) / 2f));
            if (Widgets.Button(all, buttonSize))
            {
                model.Flyout = Flyout.None;
                model.Screen = Screen.Settings;
            }
            var more = Strings.T("settingsMore");
            ImGui.PushTextWrapPos(width - pad - buttonSize.X - Layout.S(12));
            ImGui.SetCursorPos(new Vector2(pad, height - footer + (footer - Widgets.Measure(f.Caption, more).Y * 2f) / 2f));
            Widgets.Wrapped(f.Caption, Theme.TextTertiary, more);
            ImGui.PopTextWrapPos();

            hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows
                                            | ImGuiHoveredFlags.AllowWhenBlockedByPopup
                                            | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        }
        ImGui.End();

        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
        return hovered;
    }

    // ── the page ─────────────────────────────────────────────────────────────────

    /// <summary>Everything, in sections of cards: what the page shows under its title.</summary>
    public static void Page(AppModel model)
    {
        _labelWidth = 260f;
        _controlMax = 320f;

        Card(Strings.T("secDevices"), () => DevicesRows(model));
        Card(Strings.T("secPicture"), () =>
        {
            if (model.Active is not null && !model.NoDevice) ModeRow(model);
            TechniqueRows(model, "##pup", model.Upscalers, model.Upscaler, ScaleInfo(model), t => model.SelectScaler(t.Index));
            if (model.ScalerInert && model.Upscaler.Index != 0)
                Note(string.Format(Strings.T("scaleInert"), model.Upscaler.Short()), Theme.Caution);
            TechniqueRows(model, "##pfg", model.FrameGenerators, model.FrameGenerator, "", t => model.SelectFrameGen(t.Index));
            MotionRow(model, "##pflow");
            HdrRow(model);
            if (model.Renderer.SwapChainIsPq) BrightnessRows(model);
        });
        Card(Strings.T("secNetwork"), () => NetworkRows(model));
        Card(Strings.T("secSound"), () => SoundRows(model));
        Card(Strings.T("secWindow"), () =>
        {
            WindowRow(model);
            LanguageRow(model);
            TelemetryRow(model);
        });
        Card(Strings.T("secKeys"), () =>
        {
            ImGui.Dummy(new Vector2(0, Layout.S(RowGap)));
            ImGui.Indent(Layout.S(RowInset));
            ImGui.PushTextWrapPos(Widgets.ContentRight() - Layout.S(RowInset));
            Widgets.Wrapped(Widgets.F.Caption, Theme.TextSecondary, Strings.T("keysList"));
            ImGui.PopTextWrapPos();
            ImGui.Unindent(Layout.S(RowInset));
        });

        // The way out of the app, asked first: closing it mid-game loses the picture.
        var f = Widgets.F;
        string exit = Strings.T("exit");
        var size = new Vector2(Math.Max(Layout.S(110), Widgets.Measure(f.Body, exit).X + Layout.S(32)), Layout.S(32));
        ImGui.Dummy(new Vector2(0, Layout.S(4)));
        if (Widgets.Button(exit, size)) model.ConfirmQuit = true;
        ImGui.Dummy(new Vector2(0, Layout.S(24)));
    }

    /// <summary>A section: its title, then its rows on one card, as Windows' Settings groups them.</summary>
    private static void Card(string title, Action rows)
    {
        var f = Widgets.F;
        Widgets.Text(f.BodyStrong, Theme.TextPrimary, title);
        ImGui.Dummy(new Vector2(0, Layout.S(6)));

        ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Card);
        ImGui.PushStyleColor(ImGuiCol.Border, Theme.CardStroke);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Layout.S(Theme.RadiusOverlay));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Layout.S(8), Layout.S(8)));
        if (ImGui.BeginChild("##card" + title, Vector2.Zero,
                             ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysUseWindowPadding | ImGuiChildFlags.Borders))
        {
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Layout.S(8), 0));
            rows();
            ImGui.Dummy(new Vector2(0, Layout.S(RowGap)));
            ImGui.PopStyleVar();
        }
        ImGui.EndChild();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
        ImGui.Dummy(new Vector2(0, Layout.S(20)));
    }

    private static void SectionTitle(string title)
    {
        var f = Widgets.F;
        ImGui.Dummy(new Vector2(0, Layout.S(8)));
        ImGui.Indent(Layout.S(RowInset));
        Widgets.Text(f.BodyStrong, Theme.TextPrimary, title);
        ImGui.Unindent(Layout.S(RowInset));
    }

    private static void SectionGap()
    {
        ImGui.Dummy(new Vector2(0, Layout.S(10)));
        var pos = ImGui.GetCursorScreenPos();
        float w = ImGui.GetContentRegionAvail().X;
        ImGui.GetWindowDrawList().AddLine(pos + new Vector2(Layout.S(RowInset), 0), pos + new Vector2(w - Layout.S(RowInset), 0),
                                          Theme.U32(Theme.Divider));
        ImGui.Dummy(new Vector2(w, Layout.S(2)));
    }

    // ── rows ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a row: writes its label, with an info button after it when there is something
    /// to say, and leaves the cursor where the control goes, at the right edge. Returns the
    /// width the control has. A row is one control tall; a control that is not must claim
    /// that height itself.
    /// </summary>
    private static float BeginRow(string label, bool enabled = true, string? info = null,
                                  bool warning = false, bool shown = false)
    {
        var f = Widgets.F;
        ImGui.Dummy(new Vector2(0, Layout.S(RowGap)));

        var pos = ImGui.GetCursorScreenPos();
        var size = Widgets.Measure(f.Body, label);
        float inset = Layout.S(RowInset), row = Layout.S(RowHeight);
        // A label longer than its column pushes the control along rather than running under it.
        float labelWidth = Math.Max(Layout.S(_labelWidth), size.X + (info is { Length: > 0 } ? Layout.S(28) : 0) + Layout.S(16));
        float avail = ImGui.GetContentRegionAvail().X;
        float control = Math.Max(Layout.S(80), Math.Min(Layout.S(_controlMax), avail - inset * 2 - labelWidth));

        Widgets.DrawText(ImGui.GetWindowDrawList(), f.Body, new Vector2(pos.X + inset, pos.Y + (row - size.Y) / 2f),
                         enabled ? Theme.TextPrimary : Theme.TextDisabled, label);

        if (info is { Length: > 0 })
        {
            ImGui.SetCursorScreenPos(new Vector2(pos.X + inset + size.X + Layout.S(4), pos.Y + (row - Layout.S(20)) / 2f));
            Widgets.InfoButton("##info" + label, info, warning, shown);
        }

        ImGui.SetCursorScreenPos(new Vector2(pos.X + avail - inset - control, pos.Y));
        return control;
    }

    /// <summary>A sentence under the rows, across the width.</summary>
    private static void Note(string text, Vector4 colour)
    {
        float inset = Layout.S(RowInset);
        ImGui.Dummy(new Vector2(0, Layout.S(6)));
        ImGui.Indent(inset);
        ImGui.PushTextWrapPos(Widgets.ContentRight() - inset);
        Widgets.Text(Widgets.F.Caption, colour, text);
        ImGui.PopTextWrapPos();
        ImGui.Unindent(inset);
    }

    /// <summary>A switch at the right end of a row, its state in a word before it.</summary>
    private static bool SwitchRow(string label, string id, ref bool value, bool enabled = true, string? info = null)
    {
        var f = Widgets.F;
        float control = BeginRow(label, enabled, info);
        var start = ImGui.GetCursorScreenPos();
        var sw = Widgets.SwitchSize;
        string word = Strings.T(value ? "navOn" : "navOff");
        var wordSize = Widgets.Measure(f.Body, word);
        float row = Layout.S(RowHeight);
        Widgets.DrawText(ImGui.GetWindowDrawList(), f.Body,
                         new Vector2(start.X + control - sw.X - Layout.S(10) - wordSize.X, start.Y + (row - wordSize.Y) / 2f),
                         enabled ? Theme.TextSecondary : Theme.TextDisabled, Strings.Sentence(word));
        ImGui.SetCursorScreenPos(new Vector2(start.X + control - sw.X, start.Y + (row - sw.Y) / 2f));
        bool changed = Widgets.ToggleSwitch(id, ref value, enabled);
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, start.Y + row));
        ImGui.Dummy(Vector2.Zero);
        return changed;
    }

    /// <summary>
    /// The capture mode as a combo box: every offerable mode, largest first, each with the
    /// word for its wire - raw, or the codec carrying it.
    /// </summary>
    private static void ModeRow(AppModel model)
    {
        if (model.Active is not { } probe) return;
        var modes = probe.Renderable.Where(m => m.IsOfferable)
            .OrderBy(m => m.Transport == WireTransport.Raw ? 0 : 1)
            .ThenByDescending(m => m.Width * m.Height)
            .ThenByDescending(m => m.MaxFps)
            .ToList();
        if (modes.Count == 0) return;

        var items = new Widgets.ComboItem[modes.Count];
        int current = 0;
        for (int i = 0; i < modes.Count; i++)
        {
            var m = modes[i];
            items[i] = new($"{m.Width}×{m.Height} · {Strings.Num(m.MaxFps, "0.##")} {Strings.T("hz")}", m.WireTag);
            if (model.Pump.CurrentMode?.TypeIndex == m.TypeIndex) current = i;
        }

        float width = BeginRow(Strings.T("sMode"));
        if (Widgets.Combo("##mode", items[current].Text, items, current, out int chosen, width) && chosen != current)
            model.SelectMode(modes[chosen], modes[chosen].MaxFps);
    }

    /// <summary>
    /// How much work goes into the estimate of motion. It is there while something goes by
    /// that estimate - DLSS, FSR, frame generation - and not otherwise: with nothing using
    /// it, it is not worked out at all.
    /// </summary>
    private static void MotionRow(AppModel model, string id)
    {
        if (model.Renderer.Flow is not { } flow) return;
        if (model.Upscaler.Index is not (1 or 2) && model.FrameGenerator.Index == 0) return;

        var items = new Widgets.ComboItem[]
        {
            new(Strings.T("flowFast"), "", true, Strings.T("flowFastInfo")),
            new(Strings.T("flowMedium"), "", true, Strings.T("flowMediumInfo")),
            new(Strings.T("flowMax"), "", true, Strings.T("flowMaxInfo"))
        };
        int current = Math.Clamp((int)flow.Quality, 0, items.Length - 1);
        float width = BeginRow(Strings.T("flowTitle"));
        if (Widgets.Combo(id, items[current].Text, items, current, out int chosen, width) && chosen != current)
        {
            flow.Quality = (Render.FlowQuality)chosen;
            model.Touch();
        }
    }

    /// <summary>How much enlarging there is to do at all, which decides whether an upscaler matters.</summary>
    private static string ScaleInfo(AppModel model) =>
        model.Pump.CurrentMode is { Height: > 0 } mode && model.ScaleFactor > 0 && !model.ScalerInert
            ? string.Format(Strings.T("scaleUp"), model.ScaleLabel, mode.Height, (int)Math.Round(mode.Height * model.ScaleFactor))
            : "";

    /// <summary>
    /// The rows every technique has: which one, and the variant it is set to. What is really
    /// running has no row of its own: the variant's box shows it. When the choice has been
    /// overridden the box cannot be set, and the reason and the way out are written under it.
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

        // The row is named for what it chooses: the upscaler, or frame generation.
        string label = ReferenceEquals(techniques, model.Upscalers) ? Strings.T("scalerTitle") : Strings.T("fgTitle");
        float width = BeginRow(label, true, TechniqueInfo(techniques, extraInfo),
                               warning: !anyOtherUsable, shown: model.ShowTechniqueInfo);
        if (Widgets.Combo(id, items[index].Text, items, index, out int chosen, width, true, openAt))
            select(techniques[chosen]);

        if (current.Variants.Count == 0) return;

        string? asked = current.Variant();
        var running = current.Running();
        string? overridden = running?.Overridden;

        // Greyed when there is nothing for it to set: the technique cannot be used, the
        // variants are only on show beside something that is off, or something else has
        // overridden the choice - and then nothing chosen here would be obeyed.
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
            variants[i] = new(v.Text, i == runs && runs != variant ? Strings.T("tvRunning").ToLowerInvariant() : "",
                              true, current.VariantInfo(v.Id));
        }

        string preview = runs < 0 || runs == variant ? variants[variant].Text
            : overridden is null ? $"{variants[variant].Text} · {variants[runs].Text}"
            : variants[runs].Text;
        int shown = overridden is not null && runs >= 0 ? runs : variant;

        width = BeginRow(Strings.T(current.VariantsLabel), settable || overridden is not null,
                         settable ? null : current.VariantInfo(current.Variants[shown].Id),
                         shown: model.ShowVariantInfo);
        int variantAt = settable ? Math.Min(model.OpenVariantAt, variants.Length - 1) : -1;
        model.OpenVariantAt = -1;
        if (Widgets.Combo(id + "variant", preview, variants, variant, out int picked, width, settable, variantAt,
                          valueStands: overridden is not null))
            current.SetVariant(current.Variants[picked].Id);

        // Why it cannot be set and what to do about it, written out: this is something to
        // act on, not something to look up.
        if (overridden is not null) Note(overridden, Theme.Caution);
    }

    private static string ItemInfo(Technique technique, string? why) =>
        $"{technique.Name()}\n{technique.Description()}\n" +
        (why ?? string.Format(Strings.T("costPerFrame"), technique.CostText));

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

    /// <summary>RTX Video HDR, greyed with the reason beside it when it cannot be had.</summary>
    private static void HdrRow(AppModel model)
    {
        var (can, reason) = model.HdrReachable();
        bool on = model.HdrOn && can;
        if (SwitchRow("HDR", "##hdr", ref on, can, can ? null : Strings.Sentence(reason)))
            model.ToggleHdr();
    }

    /// <summary>
    /// Both levels matter whenever the surface is PQ, and they are two different pictures:
    /// the interface must stay dim enough not to burn an OLED, the video should look like
    /// the source intended, and one number cannot serve both.
    /// </summary>
    private static void BrightnessRows(AppModel model)
    {
        string nits = Strings.T("nits");

        float overlay = model.OverlayNits;
        float width = BeginRow($"{Strings.T("uiBright")} · {overlay:0} {nits}");
        ImGui.SetCursorScreenPos(ImGui.GetCursorScreenPos() + new Vector2(0, (Layout.S(RowHeight) - Layout.S(20)) / 2f));
        if (Widgets.Slider("##uinits", ref overlay, 80f, 400f, true, width))
        {
            model.OverlayNits = MathF.Round(overlay / 10f) * 10f;
            model.Touch();
        }

        if (!model.HdrOn)
        {
            float video = model.VideoNits;
            width = BeginRow($"{Strings.T("videoBright")} · {video:0} {nits}");
            ImGui.SetCursorScreenPos(ImGui.GetCursorScreenPos() + new Vector2(0, (Layout.S(RowHeight) - Layout.S(20)) / 2f));
            if (Widgets.Slider("##videonits", ref video, 80f, 600f, true, width))
            {
                model.VideoNits = MathF.Round(video / 10f) * 10f;
                model.Touch();
            }
        }

        // What the source is taken to be. A capture card does not always say that it is
        // passing HDR on, so what it says can be overruled either way.
        string[] ranges = [Strings.T("srcAuto"), "SDR", "HDR"];
        int range = Math.Clamp(model.Renderer.SourceRange, 0, 2);
        width = BeginRow($"{Strings.T("srcRange")} · {(model.Renderer.SourceIsHdr ? "HDR" : "SDR")}");
        if (Widgets.Combo("##srcrange", ranges[range], ranges, range, out int picked, width) && picked != range)
        {
            model.Renderer.SourceRange = picked;
            model.Touch();
        }
    }

    /// <summary>
    /// What a device on the network is asked for: the codec, and what it may spend. Under
    /// them what stands in the way of the choice, when something does.
    /// </summary>
    private static void NetworkRows(AppModel model)
    {
        NetWire.Codec[] codecs = [NetWire.Codec.Hevc, NetWire.Codec.Av1, NetWire.Codec.PyroWave];
        var names = codecs.Select(NetDevice.CodecName).ToList();
        int current = Math.Max(0, Array.IndexOf(codecs, NetDevice.Codec));
        float width = BeginRow(Strings.T("netCodec"));
        if (Widgets.Combo("##netcodec", names[current], names, current, out int chosen, width))
        {
            NetDevice.Codec = codecs[chosen];
            model.Touch();
        }

        if (NetDevice.Codec == NetWire.Codec.PyroWave && PyroWaveDecoder.Problem is not null)
            Note(Strings.T("netPyroMissing"), Theme.Caution);
        else if (model.ShownNet is { CodecRefused: true, Codec: var arriving, AskedCodec: var asked } &&
                 asked == NetDevice.CodecName(NetDevice.Codec))
            Note(string.Format(Strings.T("netFallback"), arriving, asked), Theme.Caution);

        // PyroWave has figures of its own, all fixed: it is for links with room to spare.
        if (NetDevice.Codec == NetWire.Codec.PyroWave && PyroWaveDecoder.Problem is null)
        {
            var rates = NetDevice.PyroWaveChoicesKbps;
            var labels = rates.Select(k => string.Format(Strings.T("mbitps"), Strings.Num(k / 1000.0, "0"))).ToList();
            int at = Array.IndexOf(rates, NetDevice.PyroWaveKbps);
            string label = at >= 0 ? labels[at]
                : string.Format(Strings.T("mbitps"), Strings.Num(NetDevice.PyroWaveKbps / 1000.0, "0.#"));
            width = BeginRow(Strings.T("netBitrate"));
            if (Widgets.Combo("##pyrowavebitrate", label, labels, Math.Max(0, at), out int picked, width))
            {
                NetDevice.PyroWaveKbps = rates[picked];
                model.Touch();
            }
            return;
        }

        var rows = new List<string> { string.Format(Strings.T("netAdaptive"), Strings.Num(NetDevice.AdaptiveCeilingKbps / 1000.0, "0")) };
        foreach (int kbps in NetDevice.FixedKbps) rows.Add(string.Format(Strings.T("mbitps"), Strings.Num(kbps / 1000.0, "0")));
        int rate = NetDevice.Adaptive ? 0 : Array.IndexOf(NetDevice.FixedKbps, NetDevice.BitrateKbps) + 1;
        string shown = rate > 0 || NetDevice.Adaptive ? rows[Math.Max(0, rate)]
            : string.Format(Strings.T("mbitps"), Strings.Num(NetDevice.BitrateKbps / 1000.0, "0.#"));
        width = BeginRow(Strings.T("netBitrate"));
        if (Widgets.Combo("##netbitrate", shown, rows, Math.Max(0, rate), out int choice, width))
        {
            NetDevice.Adaptive = choice == 0;
            if (choice > 0) NetDevice.BitrateKbps = NetDevice.FixedKbps[choice - 1];
            model.Touch();
        }
        if (NetDevice.Adaptive && model.ShownNet is { AskedKbps: > 0 } net)
            Note(string.Format(Strings.T("netNow"), Strings.Num(net.AskedKbps / 1000.0, "0.#")),
                 net.AskedKbps < NetDevice.AdaptiveCeilingKbps ? Theme.Caution : Theme.TextTertiary);
    }

    private static void SoundRows(AppModel model)
    {
        bool on = model.AudioEnabled;
        if (SwitchRow(Strings.T("audioSend"), "##audiosend", ref on))
        {
            model.AudioEnabled = on;
            model.RestartAudio();
            model.Touch();
        }
        if (!model.AudioEnabled) return;

        if (model.AudioInputs.Count == 0)
        {
            Note(Strings.Sentence(Strings.T("audioNoInput")), Theme.Caution);
            return;
        }
        Endpoint("audioIn", model.AudioInputs, model.AudioInputId,
            id => { model.AudioInputId = id; model.RestartAudio(); model.Touch(); });
        Endpoint("audioOut", model.AudioOutputs, model.AudioOutputId,
            id => { model.AudioOutputId = id; model.RestartAudio(); model.Touch(); });

        if (!model.Audio.Running && model.Audio.LastError is { } err)
            Note(err, Theme.Critical);
    }

    private static void Endpoint(string labelKey, List<Audio.AudioEndpointInfo> options, string? currentId, Action<string> pick)
    {
        if (options.Count == 0) return;
        var current = options.FirstOrDefault(o => o.Id == currentId)
                      ?? options.FirstOrDefault(o => o.IsDefault)
                      ?? options[0];
        float width = BeginRow(Strings.Sentence(Strings.T(labelKey)));
        var names = options.Select(o => o.Name).ToList();
        if (Widgets.Combo($"##{labelKey}", current.Name, names, options.IndexOf(current), out int chosen, width))
            pick(options[chosen].Id);
    }

    /// <summary>Two window modes, not three: a topmost borderless window was the same swapchain under another name.</summary>
    private static void WindowRow(AppModel model)
    {
        string[] kinds = [Strings.T("wWindowed"), Strings.T("wBorderless")];
        int kind = model.WindowKind == WindowKind.Windowed ? 0 : 1;
        float width = BeginRow(Strings.T("window"));
        if (Widgets.Combo("##winmode", kinds[kind], kinds, kind, out int chosen, width))
            model.SetWindowKind(chosen == 0 ? WindowKind.Windowed : WindowKind.BorderlessFullscreen);
    }

    private static void LanguageRow(AppModel model)
    {
        float width = BeginRow(Strings.T("language"));
        if (Widgets.Combo("##lang", Strings.LanguageNames[Strings.CurrentIndex], Strings.LanguageNames,
                          Strings.CurrentIndex, out int chosen, width))
        {
            Strings.Use(chosen);
            model.Touch();
        }
    }

    /// <summary>Off, the one line of icons and figures, or the card with what they are made of. T walks them too.</summary>
    private static void TelemetryRow(AppModel model)
    {
        string[] levels = [Strings.T("teleOff"), Strings.T("teleLine"), Strings.T("teleFull")];
        int level = !model.ShowTelemetry ? 0 : model.TelemetryDetailed ? 2 : 1;
        float width = BeginRow(Strings.T("sTelemetry"));
        if (Widgets.Combo("##telemetry", levels[level], levels, level, out int chosen, width) && chosen != level)
        {
            model.ShowTelemetry = chosen > 0;
            model.TelemetryDetailed = chosen == 2;
            model.Touch();
        }
    }

    /// <summary>
    /// The devices this computer is paired with, each with a way to forget it, and the way
    /// to pair another. Capture cards need no pairing and are not listed here.
    /// </summary>
    private static void DevicesRows(AppModel model)
    {
        var f = Widgets.F;
        var linked = model.Devices.Where(d => NetDevice.PeerOf(d) is not null).ToList();
        if (linked.Count == 0) Note(Strings.T("linkedNone"), Theme.TextTertiary);

        foreach (var device in linked)
        {
            var peer = NetDevice.PeerOf(device)!;
            ImGui.Dummy(new Vector2(0, Layout.S(RowGap)));
            var pos = ImGui.GetCursorScreenPos();
            float avail = ImGui.GetContentRegionAvail().X, inset = Layout.S(RowInset), row = Layout.S(44);
            var draw = ImGui.GetWindowDrawList();
            float icon = Layout.S(18);
            Icons.Draw(draw, peer.Model == "Steam Deck" ? Icon.Handheld : Icon.Monitor,
                       new Vector2(pos.X + inset, pos.Y + (row - icon) / 2f), icon, Theme.TextSecondary);
            string name = NetDevice.DisplayName(device, model.Devices);
            string caption = peer.Model.Length > 0 && peer.Model != name ? peer.Model : "";
            if (peer.LastAddress.Length > 0) caption += (caption.Length > 0 ? " · " : "") + peer.LastAddress.Split(':')[0];
            var nameSize = Widgets.Measure(f.Body, name);
            var capSize = Widgets.Measure(f.Caption, caption);
            float x = pos.X + inset + icon + Layout.S(12);
            float top = pos.Y + (row - nameSize.Y - capSize.Y) / 2f;
            Widgets.DrawText(draw, f.Body, new Vector2(x, top), Theme.TextPrimary, name);
            Widgets.DrawText(draw, f.Caption, new Vector2(x, top + nameSize.Y), Theme.TextTertiary, caption);

            string forget = Strings.T("forgetShort");
            var size = new Vector2(Widgets.Measure(f.Body, forget).X + Layout.S(32), Layout.S(32));
            ImGui.SetCursorScreenPos(new Vector2(pos.X + avail - inset - size.X, pos.Y + (row - size.Y) / 2f));
            // The button's label is drawn as it is, so its identity comes from the scope.
            ImGui.PushID(peer.Id);
            if (Widgets.Button(forget, size)) model.ConfirmForget = peer;
            ImGui.PopID();
            ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + row));
            ImGui.Dummy(Vector2.Zero);
        }

        ImGui.Dummy(new Vector2(0, Layout.S(RowGap + 4)));
        ImGui.Indent(Layout.S(RowInset));
        string pair = Strings.T("pairDevice");
        if (Widgets.Button(pair, new Vector2(Widgets.Measure(f.Body, pair).X + Layout.S(32), Layout.S(32))))
            model.Pairing ??= new PairFlow();
        ImGui.Unindent(Layout.S(RowInset));
    }
}
