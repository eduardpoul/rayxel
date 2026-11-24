using System.Numerics;
using ImGuiNET;
using Rayxel.Capture;

namespace Rayxel.Ui;

/// <summary>
/// The readout in the top-left corner, in two sizes: one line of what someone playing
/// needs, or the full card with what those figures are made of and the frame-time graph.
/// <para>
/// Off by default. It is the reason the app exists, but the user is playing a game most
/// of the time, and a permanent block of numbers over the picture is not that. The line
/// is what can stay on while playing; the card is for looking into something.
/// </para>
/// <para>
/// The line used to carry ten figures, and with a source on the network that was most of
/// the width of the window. It is three now: the picture's rate, its delay as one figure,
/// and what our own work takes out of a frame. What is wrong appears beside them when it
/// is wrong, and is not there to be read when it is not. Every number has one place: the
/// card shows the parts of those three and does not repeat any of them.
/// </para>
/// </summary>
public static class TelemetryOverlay
{
    private const ImGuiWindowFlags Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
        | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoNav
        | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
        | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoInputs;

    private static readonly Vector4 Grey = new(0.54f, 0.54f, 0.54f, 1f);

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

    /// <summary>
    /// The colour a stage of the delay is drawn in, the same in the bar and in its legend:
    /// lightest at the source, the accent on the way here, grey and white for this machine.
    /// </summary>
    private static Vector4 PartColour(string labelKey) => labelKey switch
    {
        "tEnc" => Theme.AccentText,
        "tNet" or "tCapture" => Theme.Accent,
        "tDec" => Grey,
        _ => Theme.TextSecondary
    };

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
            // The rate measured, not the one the mode promises: a source that has slowed
            // down shows here first.
            string rate = live ? Strings.Num(stats.MeasuredFps, "0.0") : Strings.Num(model.Pump.CurrentFps, "0.##");
            Widgets.Text(f.Caption, Theme.TextPrimary,
                mode is null ? "—" : $"{mode.Width}×{mode.Height} · {rate} {Strings.T("hz")}");

            // One figure for everything between the source and our present that can be
            // measured. What it is made of is the card's business.
            Item("tDelay", double.IsNaN(model.DelayMs) ? "n/a" : Strings.Ms(model.DelayMs, "0.0"), Theme.TextPrimary);

            // What our own work takes out of the frame, as the bottom bar used to show it:
            // the figure, what it is out of, and a short bar of the same.
            double spent = model.ShownGpuMs + model.ShownCpuMs, budget = model.BudgetMs;
            bool budgeted = live && budget > 0;
            bool over = budgeted && spent > budget;
            Item("tWork", budgeted
                    ? string.Format(Strings.T("budgetOf"), Strings.Num(spent, "0.0"), Strings.Num(budget, "0.0"))
                    : "n/a",
                 over ? Theme.Critical : Theme.TextPrimary);
            if (budgeted) InlineBar(spent / budget, over);

            // The two things that are only worth a place while they are wrong.
            if (model.FramesLost > 0) Item("tDropped", model.FramesLost.ToString(), Theme.Critical);

            var (present, colour) = Present(model);
            if (present == "Composed")
            {
                ImGui.SameLine();
                Widgets.Text(f.Caption, colour, present);
            }
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

        // A track the height of a rule, sitting on the middle of the text line.
        static void InlineBar(double fraction, bool over)
        {
            ImGui.SameLine(0, Layout.S(8));
            float width = Layout.S(48), track = Layout.S(4);
            float lineH = Widgets.Measure(Widgets.F.Caption, "0").Y;
            var pos = ImGui.GetCursorScreenPos() + new Vector2(0, MathF.Round((lineH - track) / 2f) + 1f);
            var draw = ImGui.GetWindowDrawList();

            draw.AddRectFilled(pos, pos + new Vector2(width, track), Theme.U32(Theme.Alpha(Theme.TextPrimary, 0.14f)), track / 2f);
            float filled = Math.Max(track, (float)Math.Clamp(fraction, 0, 1) * width);
            draw.AddRectFilled(pos, pos + new Vector2(filled, track), Theme.U32(over ? Theme.Critical : Theme.Accent), track / 2f);
            ImGui.Dummy(new Vector2(width, lineH));
        }
    }

    // ── the card ─────────────────────────────────────────────────────────────────

    private static void Card(AppModel model)
    {
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
            Delay(model);
            ImGui.Dummy(new Vector2(0, Layout.S(8)));
            Budget(model, live);
            ImGui.Dummy(new Vector2(0, Layout.S(8)));
            Widgets.FrameGraph(model.History.Samples, model.BudgetMs, 44f);
            Steadiness(stats, live);
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
        var upscaler = model.Upscaler;
        parts.Add(upscaler.Index switch
        {
            0 => upscaler.Name(),
            // With the variant that is really running, when the technique reports one.
            _ => upscaler.Running() is { } running ? $"{upscaler.Short()} {running.Text}" : upscaler.Short()
        });
        if (model.FrameGenIndex != 0)
            parts.Add(model.Renderer.UseFrameGen
                ? $"{model.FrameGenerator.Short()} ×{model.Renderer.FrameGenFactor}"
                : model.FrameGenerator.Short());
        if (model.HdrOn) parts.Add($"HDR {model.OverlayNits:0} {Strings.T("nits")}");
        if (mode?.HasCodecInPath == true) parts.Add($"{(mode.WireCodec ?? "codec").ToLowerInvariant()} decode");
        // Only while sound is actually flowing: this line says what is on, not what is configured.
        if (model.Audio.Running && model.Audio.Format is { } format) parts.Add(format.Split('·')[0].Trim());

        Widgets.Text(Widgets.F.Caption, Theme.TextSecondary, string.Join(" · ", parts));
    }

    /// <summary>
    /// The delay and what it is made of, stage by stage in the order a frame meets them.
    /// The total is the line's figure; this is the only place its parts are shown. In
    /// frames as well as milliseconds, because milliseconds alone cannot be compared
    /// between two frame rates, and people do compare them.
    /// </summary>
    private static void Delay(AppModel model)
    {
        var f = Widgets.F;
        var parts = model.DelayParts;

        if (parts.Length == 0 || double.IsNaN(model.DelayMs))
        {
            Widgets.BaselineRow(
                (f.Caption, Theme.TextTertiary, Strings.Sentence(Strings.T("tDelay")), 0f),
                (f.BodyStrong, Theme.TextPrimary, "n/a", 8f));
        }
        else
        {
            string frames = model.BudgetMs > 0
                ? $"{Strings.Num(model.DelayMs / model.BudgetMs, "0.00")} {Strings.T("tFrames")}" : "";
            Widgets.BaselineRow(
                (f.Caption, Theme.TextTertiary, Strings.Sentence(Strings.T("tDelay")), 0f),
                (f.BodyStrong, Theme.TextPrimary, Strings.Ms(model.DelayMs, "0.0"), 8f),
                (f.Caption, Theme.TextTertiary, frames, 6f));

            ImGui.Dummy(new Vector2(0, Layout.S(2)));
            Span<double> values = stackalloc double[parts.Length];
            Span<Vector4> colours = stackalloc Vector4[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                values[i] = parts[i].Ms;
                colours[i] = PartColour(parts[i].LabelKey);
            }
            Widgets.StackedBar(values, colours, model.DelayMs);
            ImGui.Dummy(new Vector2(0, Layout.S(2)));

            var legend = new Legend();
            for (int i = 0; i < parts.Length; i++)
                legend.Add(colours[i], $"{Strings.T(parts[i].LabelKey)} {Strings.Num(parts[i].Ms, "0.0")}");
            legend.End();
        }

        // The link itself, which explains the network's share when that share moves.
        if (model.ShownNet is { } net)
            Widgets.Text(f.Caption, Theme.TextTertiary,
                $"{Strings.Num(net.Mbps, "0.0")} {Strings.T("mbit")} · RTT {Strings.Ms(net.RoundTripMs, "0.0")}");
    }

    /// <summary>Our own work against the frame: what the GPU and the CPU take, and what is left.</summary>
    private static void Budget(AppModel model, bool live)
    {
        var f = Widgets.F;
        Widgets.BaselineRow(
            (f.Caption, Theme.TextTertiary, Strings.T("budget"), 0f),
            (f.BodyStrong, Theme.TextPrimary, Strings.Ms(model.BudgetMs), 8f));

        ImGui.Dummy(new Vector2(0, Layout.S(2)));
        Widgets.BudgetBar(model.ShownGpuMs, model.ShownCpuMs, model.BudgetMs);
        ImGui.Dummy(new Vector2(0, Layout.S(2)));

        // The legend, so the two shades of the bar are not left to be guessed.
        var legend = new Legend();
        legend.Add(Theme.Accent, $"GPU {Strings.Num(model.ShownGpuMs, "0.00")}");
        legend.Add(Grey, $"CPU {Strings.Num(model.ShownCpuMs, "0.00")}");
        legend.Add(live && model.HeadroomMs < 0 ? Theme.Critical : Theme.TextTertiary,
                   $"{Strings.T("tHead")} {Strings.Ms(model.HeadroomMs)}", filled: false);
        legend.End();
    }

    /// <summary>
    /// Under the graph, what the graph is about: the rate frames really arrive at and how
    /// evenly. Jitter is steadiness, not delay — it has been read as latency more than
    /// once, which is why it sits here beside the trace and nowhere near the delay.
    /// </summary>
    private static void Steadiness(PumpStats stats, bool live)
    {
        string rate = live ? $"{Strings.Num(stats.MeasuredFps, "0.0")} {Strings.T("hz")}" : "n/a";
        string jitter = live ? Strings.Ms(stats.IntervalJitterMs) : "n/a";
        Widgets.Text(Widgets.F.Caption, Theme.TextTertiary, $"{rate} · {Strings.T("tJitter")} {jitter}");
    }

    private static void Counters(AppModel model, PumpStats stats)
    {
        var f = Widgets.F;
        Widgets.Text(f.Caption, Theme.TextSecondary, $"{Strings.T("tLate")} {stats.FramesLate} · ");
        ImGui.SameLine(0, 0);
        Widgets.Text(f.Caption, model.FramesLost > 0 ? Theme.Critical : Theme.TextSecondary,
            $"{Strings.T("tDropped")} {model.FramesLost}");

        var (present, colour) = Present(model);
        Widgets.RightAlign(Widgets.Measure(f.Caption, present).X);
        Widgets.Text(f.Caption, colour, present);
    }

    /// <summary>
    /// Swatches with their labels on one line, going on to a second when the card is
    /// narrower than the row — which a language with longer words makes it.
    /// </summary>
    private struct Legend
    {
        private readonly ImDrawListPtr _draw = ImGui.GetWindowDrawList();
        private readonly Vector2 _origin = ImGui.GetCursorScreenPos();
        private readonly float _width = ImGui.GetContentRegionAvail().X;
        private readonly float _lineH = Widgets.Measure(Widgets.F.Caption, "0").Y;
        private float _x, _y;

        public Legend() { }

        public void Add(Vector4 colour, string text, bool filled = true)
        {
            float square = Layout.S(8), gap = Layout.S(6), between = Layout.S(12);
            float item = square + gap + Widgets.Measure(Widgets.F.Caption, text).X;
            if (_x > 0 && _x + item > _width)
            {
                _x = 0;
                _y += _lineH + Layout.S(2);
            }

            var at = _origin + new Vector2(_x, _y + (_lineH - square) / 2f);
            if (filled) _draw.AddRectFilled(at, at + new Vector2(square), Theme.U32(colour), Layout.S(2));
            else _draw.AddRect(at, at + new Vector2(square), Theme.U32(colour), Layout.S(2));
            Widgets.DrawText(_draw, Widgets.F.Caption, _origin + new Vector2(_x + square + gap, _y), Theme.TextSecondary, text);
            _x += item + between;
        }

        public readonly void End() => ImGui.Dummy(new Vector2(_width, _y + _lineH));
    }
}
