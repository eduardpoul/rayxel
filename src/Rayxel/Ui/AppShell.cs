using System.Numerics;
using ImGuiNET;
using Rayxel.Capture;

namespace Rayxel.Ui;

/// <summary>Routes the frame to whichever screen the app is on.</summary>
public static class AppShell
{
    public static void Draw(AppModel model, Fonts fonts)
    {
        switch (model.Screen)
        {
            case Screen.Splash:
                Screens.Splash(model, fonts);
                break;

            case Screen.Devices:
                Screens.DevicePicker(model, fonts);
                break;

            default:
                // The no-signal card replaces the video, but the panel stays reachable:
                // the usual fix is to pick a different device or mode, and taking the
                // controls away at exactly that moment would be perverse.
                if (model.ForceNoSignal || model.Pump.State is CaptureState.NoSignal or CaptureState.Lost)
                    Screens.NoSignal(model, fonts);

                if (model.ShowTelemetry) TelemetryOverlay.Draw(model, fonts);
                if (model.ShowSettings) SettingsPanel.Draw(model, fonts);
                else Gear(model);
                if (model.ConfirmQuit) QuitDialog(model, fonts);
                break;
        }
    }

    /// <summary>How long the panel stays up once the user stops touching it.</summary>
    private const double PanelIdleTimeoutMs = 5000;

    /// <summary>
    /// Asks before closing. Esc is one key away from the keys that switch scaler paths,
    /// and losing the picture mid-game because of a misplaced finger is not acceptable.
    /// </summary>
    private static void QuitDialog(AppModel model, Fonts fonts)
    {
        var centre = new Vector2(model.Renderer.Width * 0.5f, model.Renderer.Height * 0.5f);
        float width = Layout.S(300);

        // Dim the whole frame, so it is obvious the app is waiting on an answer.
        ImGui.GetBackgroundDrawList().AddRectFilled(Vector2.Zero,
            new Vector2(model.Renderer.Width, model.Renderer.Height), Theme.U32(new Vector4(0, 0, 0, 0.45f)));

        ImGui.SetNextWindowPos(centre, ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(width, 0), ImGuiCond.Always);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.Popover);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Layout.S(18), Layout.S(16)));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Layout.S(Theme.RadiusBlock));

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.AlwaysAutoResize;

        if (ImGui.Begin("##quitdialog", flags))
        {
            Widgets.Text(fonts.Title, Theme.TextPrimary, Strings.T("quitTitle"));
            ImGui.Dummy(new Vector2(0, Layout.S(4)));
            Widgets.Text(fonts.Body, Theme.TextSecondary, Strings.T("quitBody"));
            ImGui.Dummy(new Vector2(0, Layout.S(12)));

            float half = (ImGui.GetContentRegionAvail().X - Layout.S(8)) / 2f;
            float height = ImGui.GetFrameHeight();

            if (Button(fonts, Strings.T("cancel"), new Vector2(half, height), Theme.TextPrimary,
                       new Vector4(1, 1, 1, 0.08f), new Vector4(1, 1, 1, 0.14f)))
                model.ConfirmQuit = false;

            ImGui.SameLine(0, Layout.S(8));

            if (Button(fonts, Strings.T("quit"), new Vector2(half, height), Theme.Bad,
                       Theme.Alpha(Theme.Bad, 0.16f), Theme.Alpha(Theme.Bad, 0.28f)))
            {
                model.ConfirmQuit = false;
                model.Quit();
            }
        }
        ImGui.End();

        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor();
    }

    private static bool Button(Fonts fonts, string label, Vector2 size, Vector4 text,
                               Vector4 background, Vector4 hover)
    {
        var start = ImGui.GetCursorScreenPos();
        bool clicked = ImGui.InvisibleButton($"##btn{label}", size);
        bool hovered = ImGui.IsItemHovered();

        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(start, start + size, Theme.U32(hovered ? hover : background),
                           Layout.S(Theme.RadiusRow));

        var measured = Widgets.Measure(fonts.Body, label);
        unsafe { if (fonts.Body.NativePtr != null) ImGui.PushFont(fonts.Body); }
        draw.AddText(start + (size - measured) * 0.5f, Theme.U32(text), label);
        unsafe { if (fonts.Body.NativePtr != null) ImGui.PopFont(); }

        return clicked;
    }

    /// <summary>
    /// The way back in once the panel has hidden itself. Almost invisible until the
    /// pointer is near it — the video is the point, and a permanent button over it is not.
    /// </summary>
    /// <summary>
    /// The way back in once the panel has hidden itself: a translucent cog in the top
    /// right, the corner the panel comes from and the one place it can never collide with
    /// the telemetry block opposite.
    /// </summary>
    private static void Gear(AppModel model)
    {
        float size = Layout.S(34);
        float inset = Layout.S(16);

        ImGui.SetNextWindowPos(new Vector2(model.Renderer.Width - size - inset, inset), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(size, size), ImGuiCond.Always);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0, 0, 0, 0));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
            | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoBackground;

        if (ImGui.Begin("##gear", flags))
        {
            var pos = ImGui.GetCursorScreenPos();
            bool clicked = ImGui.InvisibleButton("##gearbtn", new Vector2(size, size));
            bool hovered = ImGui.IsItemHovered();

            DrawCog(ImGui.GetWindowDrawList(), pos + new Vector2(size / 2f), size,
                    Theme.Alpha(Theme.TextPrimary, hovered ? 0.92f : 0.42f));

            if (clicked) { model.ShowSettings = true; model.PanelActivity(); }
        }
        ImGui.End();

        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
    }

    /// <summary>
    /// An ordinary cog: a ring with eight square teeth and a hollow centre. Drawn rather
    /// than set as a glyph because the bundled fonts carry no icon set, and the previous
    /// attempt — spokes radiating from a circle — read as a compass rather than a gear.
    /// </summary>
    private static void DrawCog(ImDrawListPtr draw, Vector2 centre, float size, Vector4 colour)
    {
        uint tint = Theme.U32(colour);
        float rim = size * 0.29f;              // radius of the ring, through the tooth roots
        float rimThickness = size * 0.115f;    // heavy enough to read at this size
        float toothLength = size * 0.10f;
        float toothWidth = size * 0.115f;

        draw.AddCircle(centre, rim, tint, 32, rimThickness);

        for (int i = 0; i < 8; i++)
        {
            float a = i * MathF.PI / 4f;
            var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
            var side = new Vector2(-dir.Y, dir.X) * (toothWidth / 2f);
            var innerEdge = centre + dir * (rim - rimThickness * 0.3f);
            var outerEdge = centre + dir * (rim + toothLength);

            draw.AddQuadFilled(innerEdge - side, outerEdge - side, outerEdge + side, innerEdge + side, tint);
        }

        // The hollow centre is what makes it a cog and not a flower.
        draw.AddCircle(centre, size * 0.115f, tint, 16, Math.Max(1f, size * 0.06f));
    }

    /// <summary>
    /// Advances the splash. It is a real wait — capture modes are being enumerated behind
    /// it — but it is also capped, so a fast machine is not held back for show.
    /// </summary>
    public static void Tick(AppModel model, double deltaMs, bool advance = true)
    {
        // The panel puts itself away when it is not being used. Someone is playing a game
        // through this window; a settings panel parked over it is in the way.
        if (model.Screen == Screen.App && model.ShowSettings)
        {
            model.PanelIdleMs += deltaMs;
            if (model.PanelIdleMs >= PanelIdleTimeoutMs) model.ShowSettings = false;
        }

        if (model.Screen != Screen.Splash) return;
        if (!advance) { model.SplashElapsedMs = Math.Min(model.SplashElapsedMs + deltaMs, Screens.SplashDurationMs); return; }

        model.SplashElapsedMs += deltaMs;
        if (model.SplashElapsedMs < Screens.SplashDurationMs) return;

        // Straight past the picker when there is nothing to pick between: a chooser with
        // one row is a dialog asking permission to do the only possible thing.
        int choices = model.Devices.Count(d => !d.IsVirtual);
        model.Screen = choices > 1 ? Screen.Devices : Screen.App;
    }
}
