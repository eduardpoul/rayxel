using System.Numerics;
using ImGuiNET;
using Rayxel.Capture;

namespace Rayxel.Ui;

/// <summary>
/// The readout in the top-left, almost transparent, printed on the video.
/// <para>
/// Off by default. It is the reason the app exists, but the user is playing a game most
/// of the time, and a permanent block of numbers over the picture is not that.
/// </para>
/// </summary>
public static class TelemetryOverlay
{
    private const float Width = 366f;

    public static void Draw(AppModel model, Fonts fonts)
    {
        var pump = model.Pump;
        // The held sample, not the live one: at 120 fps the live figures repaint 120
        // times a second and the trailing digits never settle.
        var stats = model.Shown;
        var mode = pump.CurrentMode;
        bool live = pump.State == CaptureState.Streaming;

        ImGui.SetNextWindowPos(new Vector2(Layout.S(18), Layout.S(18)), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(Layout.S(Width), 0), ImGuiCond.Always);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.TelemetryBg);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Layout.S(Theme.RadiusBlock));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Layout.S(15), Layout.S(13)));

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize;

        if (ImGui.Begin("##telemetry", flags))
        {
            Header(model, fonts, mode);
            FeatureTags(model, fonts, mode);
            Widgets.Divider(5f);
            Budget(model, fonts);
            ImGui.Dummy(new Vector2(0, 4));
            MetricGrid(model, fonts, stats, live);
            ImGui.Dummy(new Vector2(0, 6));
            Widgets.FrameGraph(model.History.Samples, model.BudgetMs, 46f);
            ImGui.Dummy(new Vector2(0, 2));
            Counters(model, fonts, stats, live);
            model.TelemetryHeight = ImGui.GetWindowSize().Y;
        }
        ImGui.End();

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }

    private static void Header(AppModel model, Fonts fonts, CaptureMode? mode)
    {
        if (mode is null) { Widgets.Text(fonts.Body, Theme.TextMuted, "-"); return; }

        // One baseline for all three. With plain SameLine the smaller runs sit at the top
        // of the line and read as superscripts hanging off the resolution.
        Widgets.BaselineRow(
            (fonts.MonoLarge, Theme.TextPrimary, $"{mode.Width}×{mode.Height}", 0f),
            (fonts.Body, Theme.Alpha(Theme.TextPrimary, 0.6f), $"{model.Pump.CurrentFps:0.##} fps", 7f),
            (fonts.Small, Theme.Alpha(Theme.TextPrimary, 0.42f), model.Pump.ActiveSubtype, 7f));

        // The wire tag, right-aligned. `mjpeg?` in red is the app's most useful finding
        // and the design gives it a slot rather than a footnote.
        // Same two-colour vocabulary as the mode table: green when nothing is decoding,
        // amber when something is. See SettingsPanel.WireColour for why red went away.
        var (colour, alpha) = mode.Transport == WireTransport.Raw
            ? (Theme.Good, 0.14f)
            : (Theme.Warn, 0.14f);
        var tagWidth = Widgets.Measure(fonts.MonoTiny, mode.WireTag).X + Layout.S(16);
        Widgets.RightAlign(tagWidth);
        Widgets.Pill(fonts.MonoTiny, mode.WireTag, colour, alpha);
    }

    /// <summary>Only what is actually on. An inventory of everything available is not news.</summary>
    private static void FeatureTags(AppModel model, Fonts fonts, CaptureMode? mode)
    {
        var tags = new List<(string Text, Vector4 Colour)>();

        switch (model.ScalerIndex)
        {
            case 1:
                tags.Add(($"NIS {model.Renderer.Nis?.Sharpness ?? 0:0.0}", Theme.Accent));
                break;
            case 2:
                tags.Add((Strings.T("pVsr"), Theme.Accent));
                // An upscaler with nothing to upscale is worth saying out loud: the toggle
                // is on, the cost is being paid, and the picture is unchanged.
                if (model.ScalerInert) tags.Add(("inert", Theme.Bad));
                break;
            default:
                tags.Add((Strings.T("pNone"), Theme.TextMuted));
                break;
        }

        if (model.HdrOn) tags.Add(($"HDR · {model.OverlayNits:0} nits", Theme.Good));
        if (mode?.HasCodecInPath == true)
            tags.Add(($"{(mode.WireCodec ?? "codec").ToLowerInvariant()} decode", Theme.Warn));

        // Added in the revision. Only while sound is actually flowing — the tag says what
        // is on, not what is configured.
        if (model.Audio.Running && model.Audio.Format is { } format)
            tags.Add(($"audio {format.Split('·')[0].Trim()}", Theme.Alpha(Theme.TextPrimary, 0.55f)));

        ImGui.Dummy(new Vector2(0, 3));
        for (int i = 0; i < tags.Count; i++)
        {
            if (i > 0) ImGui.SameLine(0, 5);
            Widgets.Pill(fonts.MonoMicro, tags[i].Text, tags[i].Colour, 0.12f);
        }
    }

    private static void Budget(AppModel model, Fonts fonts)
    {
        Widgets.BaselineRow(
            (fonts.Small, Theme.Alpha(Theme.TextPrimary, 0.45f), Strings.T("budget"), 0f),
            (fonts.MonoValue, Theme.TextPrimary, $"{model.BudgetMs:0.00} ms", 7f),
            (fonts.Micro, Theme.TextMuted, $"@{model.Pump.CurrentFps:0}", 4f));

        ImGui.Dummy(new Vector2(0, 3));
        Widgets.BudgetBar(model.ShownGpuMs, model.ShownCpuMs, model.BudgetMs, 5f);
    }

    private static void MetricGrid(AppModel model, Fonts fonts, PumpStats stats, bool live)
    {
        // Anything the app cannot honestly measure right now says n/a. Estimating it
        // would make the readout worse than useless: confidently wrong.
        string Value(string text) => live ? text : "n/a";

        float column = (ImGui.GetContentRegionAvail().X - Layout.S(24)) / 3f;

        ImGui.Columns(3, "##metrics", false);
        for (int i = 0; i < 3; i++) ImGui.SetColumnWidth(i, column + Layout.S(12));

        // Age in frames as well as ms: comparing raw milliseconds between two frame rates
        // is meaningless, and people read it wrong when only the ms is shown.
        var age = live && stats.PipelineDelayValid && model.BudgetMs > 0
            ? ($"{stats.PipelineDelayMs:0.0}", $"ms · {stats.PipelineDelayMs / model.BudgetMs:0.00} {Strings.T("frames")}")
            : ("n/a", "");
        Widgets.Metric(fonts, Strings.T("age"), age.Item1, age.Item2);
        ImGui.NextColumn();

        double headroomPercent = model.BudgetMs > 0 ? model.HeadroomMs / model.BudgetMs * 100 : 0;
        Widgets.Metric(fonts, Strings.T("head"), Value($"{model.HeadroomMs:0.00}"),
            live ? $"ms · {headroomPercent:0}%" : "",
            model.HeadroomMs < 0 ? Theme.Bad : Theme.Good);
        ImGui.NextColumn();

        Widgets.Metric(fonts, Strings.T("gpu"), Value($"{model.ShownGpuMs:0.00}"), live ? "ms" : "",
            model.ShownGpuMs > model.BudgetMs * 0.5 ? Theme.Warn : Theme.TextPrimary);
        ImGui.NextColumn();

        var cpuUnit = model.Pump.CurrentMode?.HasCodecInPath == true ? "ms · decode" : "ms";
        Widgets.Metric(fonts, Strings.T("cpu"), Value($"{model.ShownCpuMs:0.00}"), live ? cpuUnit : "");
        ImGui.NextColumn();

        // Labelled as steadiness. It has been read as latency more than once, including
        // by the person who wrote the measurement.
        Widgets.Metric(fonts, Strings.T("jitter"), Value($"{stats.IntervalJitterMs:0.00}"), live ? "ms" : "");
        ImGui.NextColumn();

        Widgets.Metric(fonts, Strings.T("rate"), Value($"{stats.MeasuredFps:0.0}"), live ? "fps" : "");
        ImGui.Columns(1);

    }

    /// <summary>
    /// Kept, unused. The revision removed every explanatory line from the readout, and this
    /// was one — but jitter has been read as latency more than once, including by whoever
    /// wrote the measurement, so the wording is left here rather than deleted.
    /// </summary>
    private static string JitterNote()
    {
        var note = Strings.T("jitter");
        return Strings.Current switch
        {
            "ru" => "дрожание — ровность, не задержка",
            "uk" => "тремтіння — рівність, не затримка",
            "de" => "Jitter — Gleichmäßigkeit, keine Verzögerung",
            "es" => "jitter — regularidad, no retardo",
            _ => "jitter — steadiness, not delay"
        };
    }

    private static void Counters(AppModel model, Fonts fonts, PumpStats stats, bool live)
    {
        Widgets.Text(fonts.MonoMicro, Theme.TextSecondary, $"{stats.FramesArrived} {Strings.T("framesC")}");
        Widgets.TextSameLine(fonts.MonoMicro, Theme.TextSecondary, $"{stats.FramesLate} {Strings.T("lateC")}", 8f);
        ImGui.SameLine(0, 8);
        Widgets.Text(fonts.MonoMicro, stats.FramesDropped > 0 ? Theme.Bad : Theme.TextMuted,
            $"{stats.FramesDropped} {Strings.T("droppedC")}");

        // Overlay against Composed: the difference is a whole frame, more than every
        // filter in the app put together.
        var present = model.Composition;
        var presentColour = present == "Overlay" ? Theme.Good
                          : present == "Composed" ? Theme.Warn
                          : Theme.TextMuted;
        float w = Widgets.Measure(fonts.MonoMicro, present).X;
        Widgets.RightAlign(w);
        Widgets.Text(fonts.MonoMicro, presentColour, present);
    }
}
