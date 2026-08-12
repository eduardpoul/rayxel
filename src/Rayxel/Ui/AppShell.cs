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
                // Nothing is drawn over the video when the panel is away. The way back in
                // is Esc, or the window's own system menu — see Program's WM_SYSCOMMAND.
                if (model.ShowSettings) SettingsPanel.Draw(model, fonts);
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
