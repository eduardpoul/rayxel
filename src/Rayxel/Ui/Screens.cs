using System.Numerics;
using ImGuiNET;
using Rayxel.Capture;

namespace Rayxel.Ui;

/// <summary>
/// The full-stage screens: start-up, the device choice, and the states where the card is
/// there but no picture is — the console asleep, or the card held by another application.
/// </summary>
public static class Screens
{
    public const double SplashDurationMs = 1700;

    private const ImGuiWindowFlags CardFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
        | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings
        | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoNav;

    /// <summary>
    /// The stage behind these screens: flat. The radial glow it used to have was the most
    /// decorative thing in the app and said nothing.
    /// </summary>
    private static void Stage(AppModel model) =>
        ImGui.GetBackgroundDrawList().AddRectFilled(Vector2.Zero,
            new Vector2(model.Renderer.Width, model.Renderer.Height), Theme.U32(Theme.Stage));

    /// <summary>A window centred on the screen, at a fixed width and whatever height it needs.</summary>
    private static bool BeginCentred(AppModel model, string id, float width, Vector2 padding, Vector4 background)
    {
        ImGui.SetNextWindowPos(new Vector2(model.Renderer.Width, model.Renderer.Height) * 0.5f,
                               ImGuiCond.Always, new Vector2(0.5f));
        ImGui.SetNextWindowSize(new Vector2(width, 0), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, padding);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Layout.S(8), Layout.S(4)));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, background);
        ImGui.PushStyleColor(ImGuiCol.Border, background.W > 0 ? Theme.SurfaceStroke : default);
        return ImGui.Begin(id, CardFlags);
    }

    private static void EndCentred()
    {
        ImGui.End();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
    }

    /// <summary>Text centred in the current window.</summary>
    private static void Centred(ImFontPtr font, Vector4 colour, string text)
    {
        float w = Widgets.Measure(font, text).X;
        ImGui.SetCursorPosX((ImGui.GetWindowWidth() - w) * 0.5f);
        Widgets.Text(font, colour, text);
    }

    public static void Splash(AppModel model)
    {
        var f = Widgets.F;
        Stage(model);

        if (BeginCentred(model, "##splash", Layout.S(320), Vector2.Zero, default))
        {
            float mark = Layout.S(56);
            var at = ImGui.GetCursorScreenPos() + new Vector2((ImGui.GetWindowWidth() - mark) * 0.5f, 0);
            Brand.Mark(ImGui.GetWindowDrawList(), at, mark);
            ImGui.Dummy(new Vector2(mark, mark + Layout.S(12)));

            Centred(f.Title, Theme.TextPrimary, "Rayxel");
            ImGui.Dummy(new Vector2(0, Layout.S(20)));

            // Fluent's progress bar: a hairline track with a 3px accent indicator on it.
            // The splash is a real wait — capture modes are being read behind it — and it
            // finishes when the app is ready to move on.
            float t = (float)Math.Clamp(model.SplashElapsedMs / 1600.0, 0, 1);
            float eased = 1f - MathF.Pow(1f - t, 3f);
            var pos = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X, bar = Layout.S(3);
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(new Vector2(pos.X, pos.Y + bar / 2f - 0.5f), new Vector2(pos.X + width, pos.Y + bar / 2f + 0.5f),
                               Theme.U32(Theme.ControlStrong));
            if (eased > 0.001f)
                draw.AddRectFilled(pos, pos + new Vector2(width * eased, bar), Theme.U32(Theme.Accent), bar / 2f);
            ImGui.Dummy(new Vector2(width, bar));

            ImGui.Dummy(new Vector2(0, Layout.S(8)));
            Centred(f.Caption, Theme.TextTertiary, Strings.T("splashStatus"));
        }
        EndCentred();
    }

    /// <summary>
    /// The card is there, the picture is not. Three causes, three cards: the source has
    /// gone quiet (it comes back by itself), another application holds the card (the user
    /// has to close it), or the card itself went away. The first is the common one and
    /// the card says so calmly; nothing here is an alarm.
    /// </summary>
    public static void NoSignal(AppModel model)
    {
        var f = Widgets.F;
        Stage(model);

        // A sender on the network that says why it will not stream gets a card for that
        // reason; the generic ones below speak of cables and other applications.
        if (model.Refusal is { } refusal && (model.ForceRefusal is not null || model.Pump.Device.Bus == DeviceBus.Network))
        {
            Refused(model, refusal);
            return;
        }

        bool busy = model.DeviceBusy;
        bool lost = !busy && model.Pump.State == CaptureState.Lost && !model.ForceNoSignal;
        bool network = model.Pump.Device.Bus == DeviceBus.Network;
        string name = model.Pump.Device.FriendlyName;

        var (icon, title, body) = busy ? (Icon.Lock, Strings.T("busyTitle"), string.Format(Strings.T("busyBody"), name))
                                : lost ? (Icon.MonitorOff, Strings.T("lostTitle"), string.Format(Strings.T(network ? "deckLostBody" : "lostBody"), name))
                                : (Icon.MonitorOff, Strings.T("nsTitle"), Strings.T(network ? "deckNsBody" : "nsBody"));

        if (BeginCentred(model, "##nosignal", Layout.S(440), new Vector2(Layout.S(24)), Theme.Card))
        {
            float glyph = Layout.S(24);
            Icons.Draw(ImGui.GetWindowDrawList(), icon, ImGui.GetCursorScreenPos(), glyph, Theme.TextSecondary);
            ImGui.Dummy(new Vector2(glyph, glyph + Layout.S(8)));

            Widgets.Text(f.Subtitle, Theme.TextPrimary, title);
            ImGui.Dummy(new Vector2(0, Layout.S(2)));
            Widgets.Wrapped(f.Body, Theme.TextSecondary, body);

            // A lost device is retried every three seconds by the frame loop; a quiet one
            // needs nothing and recovers when frames resume. The ring says something is
            // still being waited for, which is the reassuring part.
            if (!busy)
            {
                ImGui.Dummy(new Vector2(0, Layout.S(8)));
                var pos = ImGui.GetCursorScreenPos();
                float ring = Layout.S(16);
                ProgressRing(ImGui.GetWindowDrawList(), pos + new Vector2(ring / 2f), ring / 2f - 1f);
                var waitText = lost ? Strings.Sentence(Strings.T("retryIn")) : Strings.T("nsWait");
                ImGui.SetCursorScreenPos(pos + new Vector2(ring + Layout.S(8), 0));
                ImGui.PushTextWrapPos(0);
                Widgets.Text(f.Caption, Theme.TextTertiary, waitText);
                ImGui.PopTextWrapPos();
            }

            // The log's own sentence, for the case nobody predicted.
            if (lost && model.Pump.StateDetail is { } detail)
                Widgets.Wrapped(f.Caption, Theme.TextTertiary, detail);

            ImGui.Dummy(new Vector2(0, Layout.S(16)));
            float gap = Layout.S(8);
            float half = (ImGui.GetContentRegionAvail().X - gap) / 2f;
            var size = new Vector2(half, Layout.S(32));

            if (Widgets.Button(Strings.T(busy ? "retryNow" : "retry"), size, accent: true)
                && model.Active is { } active)
                model.SwitchDevice(active.Device);
            ImGui.SameLine(0, gap);
            if (Widgets.Button(Strings.T("otherDevice"), size))
                model.Screen = Screen.Sources;
        }
        EndCentred();
    }

    /// <summary>
    /// A Steam Deck that answers but will not stream, and why. It has forgotten this computer
    /// (or holds another key): asking again changes nothing, so the card offers pairing again
    /// and nothing is retried behind it. It shows its picture to another computer, or its
    /// sending is switched off: the session is asked for again by itself, and the picture
    /// appears the moment the Deck says yes.
    /// </summary>
    private static void Refused(AppModel model, Link.Wire.RefuseReason refusal)
    {
        var f = Widgets.F;
        string name = model.Pump.Device.FriendlyName;
        bool forgot = refusal is Link.Wire.RefuseReason.NotPaired or Link.Wire.RefuseReason.BadProof;

        var (icon, title, body) = refusal switch
        {
            Link.Wire.RefuseReason.Busy => (Icon.Lock, string.Format(Strings.T("deckBusyTitle"), name), Strings.T("deckBusyBody")),
            Link.Wire.RefuseReason.Disabled => (Icon.MonitorOff, string.Format(Strings.T("deckOffTitle"), name), Strings.T("deckOffBody")),
            _ => (Icon.Lock, string.Format(Strings.T("deckForgotTitle"), name), Strings.T("deckForgotBody"))
        };

        if (BeginCentred(model, "##refused", Layout.S(440), new Vector2(Layout.S(24)), Theme.Card))
        {
            float glyph = Layout.S(24);
            Icons.Draw(ImGui.GetWindowDrawList(), icon, ImGui.GetCursorScreenPos(), glyph, Theme.TextSecondary);
            ImGui.Dummy(new Vector2(glyph, glyph + Layout.S(8)));

            Widgets.Text(f.Subtitle, Theme.TextPrimary, title);
            ImGui.Dummy(new Vector2(0, Layout.S(2)));
            Widgets.Wrapped(f.Body, Theme.TextSecondary, body);

            if (!forgot)
            {
                ImGui.Dummy(new Vector2(0, Layout.S(8)));
                var pos = ImGui.GetCursorScreenPos();
                float ring = Layout.S(16);
                ProgressRing(ImGui.GetWindowDrawList(), pos + new Vector2(ring / 2f), ring / 2f - 1f);
                ImGui.SetCursorScreenPos(pos + new Vector2(ring + Layout.S(8), 0));
                ImGui.PushTextWrapPos(0);
                Widgets.Text(f.Caption, Theme.TextTertiary, Strings.T("deckAsking"));
                ImGui.PopTextWrapPos();
            }

            ImGui.Dummy(new Vector2(0, Layout.S(16)));
            float gap = Layout.S(8);
            float half = (ImGui.GetContentRegionAvail().X - gap) / 2f;
            var size = new Vector2(half, Layout.S(32));

            if (forgot)
            {
                if (Widgets.Button(Strings.T("pairAgain"), size, accent: true))
                    model.Pairing ??= new PairFlow();
            }
            else if (Widgets.Button(Strings.T("retryNow"), size, accent: true) && model.Active is { } active)
                model.SwitchDevice(active.Device);
            ImGui.SameLine(0, gap);
            if (Widgets.Button(Strings.T("otherDevice"), size))
                model.Screen = Screen.Sources;
        }
        EndCentred();
    }

    /// <summary>Fluent's indeterminate progress ring: a quarter arc going round once a second.</summary>
    private static void ProgressRing(ImDrawListPtr draw, Vector2 centre, float radius)
    {
        float t = (float)(ImGui.GetTime() % 1.0) * MathF.PI * 2f;
        float thickness = Math.Max(1.5f, Layout.S(2));
        draw.AddCircle(centre, radius, Theme.U32(Theme.Alpha(Theme.TextPrimary, 0.12f)), 0, thickness);
        draw.PathArcTo(centre, radius, t, t + MathF.PI * 0.6f);
        draw.PathStroke(Theme.U32(Theme.Accent), ImDrawFlags.None, thickness);
    }
}
