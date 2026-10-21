using System.Numerics;
using ImGuiNET;
using Rayxel.Capture;

namespace Rayxel.Ui;

/// <summary>
/// The readout in the top-left corner, in two sizes: one line of the numbers that move,
/// or the full card with the budget split and the frame-time graph.
/// <para>
/// Off by default. It is the reason the app exists, but the user is playing a game most
/// of the time, and a permanent block of numbers over the picture is not that. The line
/// is what can stay on while playing; the card is for looking into something.
/// </para>
/// </summary>
public static class TelemetryOverlay
{
    private const ImGuiWindowFlags Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
        | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoNav
        | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
        | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoInputs;

    public static void Draw(AppModel model)
    {
        if (model.TelemetryDetailed) Card(model);
        else Line(model);
    }

    /// <summary>Anything the app cannot honestly measure right now says n/a rather than a guess.</summary>
    private static bool Live(AppModel model) => model.Pump.State == CaptureState.Streaming;

    private static (string Text, Vector4 Colour) Present(AppModel model)
    {
        // Overlay against Composed: the difference is a whole frame, more than every
        // filter in the app put together.
        var present = model.ForceComposed ? "Composed" : model.Composition;
        return (present, present == "Overlay" ? Theme.Success
                       : present == "Composed" ? Theme.Caution
                       : Theme.TextTertiary);
    }

    // ── one line ─────────────────────────────────────────────────────────────────

    private static void Line(AppModel model)
    {
        var f = Widgets.F;
        var stats = model.Shown;   // held for a second, so the trailing digits settle
        var mode = model.Pump.CurrentMode;
        bool live = Live(model);

        ImGui.SetNextWindowPos(new Vector2(Layout.S(16)), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Layout.S(12), Layout.S(7)));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Layout.S(Theme.RadiusControl));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Layout.S(16), 0));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.Alpha(Theme.Layer, 0.92f));

        if (ImGui.Begin("##teleline", Flags))
        {
            string rate = live ? Strings.Num(stats.MeasuredFps, "0.0") : Strings.Num(model.Pump.CurrentFps, "0.##");
            Widgets.Text(f.Caption, Theme.TextPrimary,
                mode is null ? "—" : $"{mode.Width}×{mode.Height} · {rate} {Strings.T("hz")}");

            Item("tGpu", live ? Strings.Ms(model.ShownGpuMs) : "n/a", Theme.TextPrimary);
            Item("tHead", live ? Strings.Ms(model.HeadroomMs) : "n/a",
                 model.HeadroomMs < 0 && live ? Theme.Critical : Theme.TextPrimary);

            // Age in frames rather than milliseconds: comparing raw milliseconds between two
            // frame rates is meaningless, and people read it wrong when only the ms is shown.
            Item("tAge", live && stats.PipelineDelayValid && model.BudgetMs > 0
                    ? $"{Strings.Num(stats.PipelineDelayMs / model.BudgetMs, "0.00")} {Strings.T("tFrames")}"
                    : "n/a",
                 Theme.TextPrimary);

            // Steadiness, not delay. It has been read as latency more than once.
            Item("tJitter", live ? Strings.Ms(stats.IntervalJitterMs) : "n/a", Theme.TextPrimary);
            Item("tDropped", stats.FramesDropped.ToString(), stats.FramesDropped > 0 ? Theme.Critical : Theme.TextPrimary);

            var (present, colour) = Present(model);
            ImGui.SameLine();
            Widgets.Text(f.Caption, colour, present);
        }
        ImGui.End();

        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);

        static void Item(string labelKey, string value, Vector4 colour)
        {
            ImGui.SameLine();
            Widgets.Text(Widgets.F.Caption, Theme.TextTertiary, Strings.T(labelKey));
            ImGui.SameLine(0, Layout.S(4));
            Widgets.Text(Widgets.F.Caption, colour, value);
        }
    }

    // ── the card ─────────────────────────────────────────────────────────────────

    private static void Card(AppModel model)
    {
        var f = Widgets.F;
        var stats = model.Shown;
        var mode = model.Pump.CurrentMode;
        bool live = Live(model);

        ImGui.SetNextWindowPos(new Vector2(Layout.S(16)), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(Layout.S(360), 0), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Layout.S(16)));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Layout.S(Theme.RadiusOverlay));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Layout.S(8), Layout.S(4)));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.Alpha(Theme.Layer, 0.94f));

        if (ImGui.Begin("##telecard", Flags))
        {
            Header(model, mode);
            Features(model, mode);
            Widgets.Divider(10f);
            Budget(model);
            ImGui.Dummy(new Vector2(0, Layout.S(6)));
            Metrics(model, stats, live);
            ImGui.Dummy(new Vector2(0, Layout.S(6)));
            Widgets.FrameGraph(model.History.Samples, model.BudgetMs, 44f);
            ImGui.Dummy(new Vector2(0, Layout.S(4)));
            Counters(model, stats);
            model.TelemetryHeight = ImGui.GetWindowSize().Y;
        }
        ImGui.End();

        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);
    }

    private static void Header(AppModel model, CaptureMode? mode)
    {
        var f = Widgets.F;
        if (mode is null) { Widgets.Text(f.Body, Theme.TextTertiary, "—"); return; }

        Widgets.BaselineRow(
            (f.BodyStrong, Theme.TextPrimary, $"{mode.Width}×{mode.Height}", 0f),
            (f.Caption, Theme.TextTertiary,
             $"{Strings.Num(model.Pump.CurrentFps, "0.##")} {Strings.T("hz")} · {model.Pump.ActiveSubtype}", 8f));

        var tagColour = mode.Transport == WireTransport.Raw ? Theme.Success : Theme.Caution;
        float w = Widgets.Measure(f.Caption, mode.WireTag).X;
        Widgets.RightAlign(w);
        Widgets.Text(f.Caption, tagColour, mode.WireTag);
    }

    /// <summary>Only what is actually on, in one quiet line. An inventory of what is available is not news.</summary>
    private static void Features(AppModel model, CaptureMode? mode)
    {
        var parts = new List<string>();
        switch (model.ScalerIndex)
        {
            case 1: parts.Add($"NIS {Strings.Num(model.Renderer.Nis?.Sharpness ?? 0, "0.00")}"); break;
            case 2: parts.Add(Strings.T("pVsr")); break;
            default: parts.Add(Strings.T("pNone")); break;
        }
        if (model.HdrOn) parts.Add($"HDR {model.OverlayNits:0} {Strings.T("nits")}");
        if (mode?.HasCodecInPath == true) parts.Add($"{(mode.WireCodec ?? "codec").ToLowerInvariant()} decode");
        // Only while sound is actually flowing: this line says what is on, not what is configured.
        if (model.Audio.Running && model.Audio.Format is { } format) parts.Add(format.Split('·')[0].Trim());

        Widgets.Text(Widgets.F.Caption, Theme.TextSecondary, string.Join(" · ", parts));

        if (model.ScalerIndex == 2 && model.ScalerInert)
            Widgets.Text(Widgets.F.Caption, Theme.Caution, string.Format(Strings.T("scaleInert"), Strings.T("pVsr")));
    }

    private static void Budget(AppModel model)
    {
        var f = Widgets.F;
        Widgets.BaselineRow(
            (f.Caption, Theme.TextTertiary, Strings.T("budget"), 0f),
            (f.BodyStrong, Theme.TextPrimary, Strings.Ms(model.BudgetMs), 8f),
            (f.Caption, Theme.TextTertiary, $"{Strings.Num(model.Pump.CurrentFps, "0")} {Strings.T("hz")}", 6f));

        ImGui.Dummy(new Vector2(0, Layout.S(2)));
        Widgets.BudgetBar(model.ShownGpuMs, model.ShownCpuMs, model.BudgetMs);
        ImGui.Dummy(new Vector2(0, Layout.S(2)));

        // The legend, so the two shades of the bar are not left to be guessed.
        var draw = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        float square = Layout.S(8), gap = Layout.S(6), between = Layout.S(14), x = pos.X;
        float lineH = Widgets.Measure(f.Caption, "0").Y;

        Swatch(Theme.Accent, $"GPU {Strings.Num(model.ShownGpuMs, "0.00")}", filled: true);
        Swatch(new Vector4(0.54f, 0.54f, 0.54f, 1f), $"CPU {Strings.Num(model.ShownCpuMs, "0.00")}", filled: true);
        Swatch(Theme.TextTertiary, $"{Strings.T("tHead")} {Strings.Ms(model.HeadroomMs)}", filled: false);
        ImGui.Dummy(new Vector2(x - pos.X, lineH));

        void Swatch(Vector4 colour, string text, bool filled)
        {
            var at = new Vector2(x, pos.Y + (lineH - square) / 2f);
            if (filled) draw.AddRectFilled(at, at + new Vector2(square), Theme.U32(colour), Layout.S(2));
            else draw.AddRect(at, at + new Vector2(square), Theme.U32(colour), Layout.S(2));
            x += square + gap;
            Widgets.DrawText(draw, f.Caption, new Vector2(x, pos.Y), Theme.TextSecondary, text);
            x += Widgets.Measure(f.Caption, text).X + between;
        }
    }

    private static void Metrics(AppModel model, PumpStats stats, bool live)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(0, Layout.S(4)));
        if (!ImGui.BeginTable("##metrics", 3, ImGuiTableFlags.SizingStretchSame)) { ImGui.PopStyleVar(); return; }

        string Value(double v) => live ? Strings.Num(v, "0.00") : "n/a";
        string unit = live ? Strings.T("ms") : "";

        var age = live && stats.PipelineDelayValid && model.BudgetMs > 0
            ? (Strings.Num(stats.PipelineDelayMs, "0.0"), $"{Strings.Num(stats.PipelineDelayMs / model.BudgetMs, "0.00")} {Strings.T("tFrames")}")
            : ("n/a", "");

        Cell(Strings.T("age"), age.Item1, age.Item1 == "n/a" ? "" : Strings.T("ms"), age.Item2, Theme.TextPrimary);
        Cell(Strings.T("head"), Value(model.HeadroomMs), unit, "",
             model.HeadroomMs < 0 && live ? Theme.Critical : Theme.TextPrimary);
        Cell(Strings.T("gpu"), Value(model.ShownGpuMs), unit, "", Theme.TextPrimary);

        var cpuNote = model.Pump.CurrentMode?.HasCodecInPath == true ? "decode" : "";
        Cell(Strings.T("cpu"), Value(model.ShownCpuMs), unit, cpuNote, Theme.TextPrimary);
        // Labelled as steadiness. It has been read as latency more than once.
        Cell(Strings.T("jitter"), Value(stats.IntervalJitterMs), unit, "", Theme.TextPrimary);
        Cell(Strings.T("rate"), live ? Strings.Num(stats.MeasuredFps, "0.0") : "n/a", live ? Strings.T("hz") : "", "", Theme.TextPrimary);

        ImGui.EndTable();
        ImGui.PopStyleVar();

        static void Cell(string label, string value, string unit, string note, Vector4 colour)
        {
            var f = Widgets.F;
            ImGui.TableNextColumn();
            Widgets.Text(f.Caption, Theme.TextTertiary, label);
            Widgets.BaselineRow((f.Subtitle, colour, value, 0f), (f.Caption, Theme.TextTertiary, unit, 4f));
            if (note.Length > 0) Widgets.Text(f.Caption, Theme.TextTertiary, note);
        }
    }

    private static void Counters(AppModel model, PumpStats stats)
    {
        var f = Widgets.F;
        Widgets.Text(f.Caption, Theme.TextSecondary,
            $"{stats.FramesArrived} {Strings.T("framesC")} · {stats.FramesLate} {Strings.T("lateC")} · ");
        ImGui.SameLine(0, 0);
        Widgets.Text(f.Caption, stats.FramesDropped > 0 ? Theme.Critical : Theme.TextSecondary,
            $"{stats.FramesDropped} {Strings.T("droppedC")}");

        var (present, colour) = Present(model);
        Widgets.RightAlign(Widgets.Measure(f.Caption, present).X);
        Widgets.Text(f.Caption, colour, present);
    }
}
