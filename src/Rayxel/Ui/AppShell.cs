using System.Numerics;
using ImGuiNET;
using Rayxel.Capture;

namespace Rayxel.Ui;

/// <summary>Routes the frame to whichever screen the app is on.</summary>
public static class AppShell
{
    /// <summary>How long the plates over the picture stay up once the pointer stops moving and nothing is open.</summary>
    private const double PanelIdleTimeoutMs = 3000;

    /// <summary>Fluent's duration for a surface fading in or out.</summary>
    private const double FadeMs = 167;

    public static void Draw(AppModel model, Fonts fonts)
    {
        Widgets.Use(fonts);

        switch (model.Screen)
        {
            case Screen.Splash:
                Screens.Splash(model);
                break;

            case Screen.Sources:
            case Screen.Transmit:
            case Screen.Settings:
                Pages.Draw(model);
                break;

            default:
                // The no-signal card replaces the video, but the plates stay reachable: the
                // usual fix is to pick a different source or mode, and taking the controls
                // away at exactly that moment would be perverse.
                if (model.ForceNoSignal || model.ForceBusy || model.ForceRefusal is not null
                    || model.Pump.State is CaptureState.NoSignal or CaptureState.Lost)
                    Screens.NoSignal(model);

                if (model.ShowTelemetry) TelemetryOverlay.Draw(model);
                ViewerOverlay.Draw(model);
                if (model.Pump.InputCaptured) CaptureBadge.Draw(model);
                break;
        }

        QuitDialog(model);
        PairDialog.Draw(model);
        PairDialog.DrawForget(model);
    }

    /// <summary>
    /// Asks before closing, as Fluent's content dialog: everything behind it dimmed, the
    /// question, and the two answers with the one that acts in the accent. Esc is one key
    /// away from the keys that switch scaler paths, and losing the picture mid-game because
    /// of a misplaced finger is not acceptable.
    /// </summary>
    private static void QuitDialog(AppModel model)
    {
        const string id = "##quit";
        if (model.ConfirmQuit && !ImGui.IsPopupOpen(id)) ImGui.OpenPopup(id);

        var f = Widgets.F;
        float width = Layout.S(400), pad = Layout.S(24);
        ImGui.SetNextWindowPos(new Vector2(model.Renderer.Width, model.Renderer.Height) * 0.5f,
                               ImGuiCond.Always, new Vector2(0.5f));
        ImGui.SetNextWindowSize(new Vector2(width, 0), ImGuiCond.Always);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Layout.S(8), Layout.S(8)));
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Theme.Card);

        bool open = true;
        if (ImGui.BeginPopupModal(id, ref open, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!model.ConfirmQuit)
            {
                // Esc answered it from the keyboard.
                ImGui.CloseCurrentPopup();
            }
            else
            {
                var draw = ImGui.GetWindowDrawList();
                draw.ChannelsSplit(2);
                draw.ChannelsSetCurrent(1);

                ImGui.Dummy(new Vector2(0, pad - Layout.S(8)));
                ImGui.Indent(pad);
                ImGui.PushTextWrapPos(width - pad);
                Widgets.Text(f.Subtitle, Theme.TextPrimary, Strings.T("quitTitle"));
                Widgets.Text(f.Body, Theme.TextSecondary, Strings.T("quitBody"));
                ImGui.PopTextWrapPos();
                ImGui.Unindent(pad);
                ImGui.Dummy(new Vector2(0, pad - Layout.S(8)));

                // The footer band, a shade darker, as the dialog's own.
                var footer = ImGui.GetCursorScreenPos();
                ImGui.Dummy(new Vector2(0, pad - Layout.S(8)));
                ImGui.Indent(pad);
                float gap = Layout.S(8);
                var size = new Vector2((width - pad * 2 - gap) / 2f, Layout.S(32));
                if (Widgets.Button(Strings.T("quit"), size, accent: true))
                {
                    model.ConfirmQuit = false;
                    model.Quit();
                }
                ImGui.SameLine(0, gap);
                if (Widgets.Button(Strings.T("cancel"), size)) model.ConfirmQuit = false;
                ImGui.Unindent(pad);
                ImGui.Dummy(new Vector2(0, pad - Layout.S(8)));

                draw.ChannelsSetCurrent(0);
                var bottom = ImGui.GetWindowPos() + ImGui.GetWindowSize();
                draw.AddRectFilled(footer, new Vector2(footer.X + width, bottom.Y), Theme.U32(Theme.Layer),
                                   Layout.S(Theme.RadiusOverlay), ImDrawFlags.RoundCornersBottom);
                draw.AddLine(footer, footer + new Vector2(width, 0), Theme.U32(Theme.Divider));
                draw.ChannelsMerge();
            }
            ImGui.EndPopup();
        }

        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }

    /// <summary>
    /// Advances the clock-driven parts of the interface: the bar's idle timeout and fade,
    /// and the splash. The splash is a real wait — capture modes are being enumerated
    /// behind it — but it is also capped, so a fast machine is not held back for show.
    /// </summary>
    public static void Tick(AppModel model, double deltaMs, bool advance = true)
    {
        if (model.Screen == Screen.App)
        {
            // Nothing hides while something is open or being asked: whoever opened it is
            // in the middle of using it, even if the mouse is still.
            if (model.Flyout != Flyout.None || model.ConfirmQuit || model.Pairing is not null || model.KeepPlates) model.PanelIdleMs = 0;
            else if (model.ShowBar)
            {
                model.PanelIdleMs += deltaMs;
                if (model.PanelIdleMs >= PanelIdleTimeoutMs) model.ShowBar = false;
            }
        }

        // The settings panel belongs to the picture: it does not follow the app to a page.
        if (model.Screen != Screen.App) model.Flyout = Flyout.None;

        float target = model.Screen == Screen.App && model.ShowBar ? 1f : 0f;
        float step = (float)(deltaMs / FadeMs);
        model.BarFade = target > model.BarFade
            ? Math.Min(target, model.BarFade + step)
            : Math.Max(target, model.BarFade - step);

        if (model.Screen != Screen.Splash) return;
        if (!advance) { model.SplashElapsedMs = Math.Min(model.SplashElapsedMs + deltaMs, Screens.SplashDurationMs); return; }

        model.SplashElapsedMs += deltaMs;
        if (model.SplashElapsedMs < Screens.SplashDurationMs) return;

        // Straight to the picture when there is nothing to choose between: a page of sources
        // with one row asks permission to do the only possible thing. The same goes for a
        // device named on the command line, however many others there are. Otherwise the
        // app opens where the choice is - the remembered source already starting behind it.
        int choices = model.Devices.Count(d => !d.IsVirtual);
        model.Screen = (choices > 1 || model.NoDevice) && !model.DeviceNamed ? Screen.Sources : Screen.App;

        // The plates come up with the first picture, so the first thing anyone sees is
        // where the controls are — and then, a few seconds later, that they go away.
        model.ShowBar = true;
        model.PanelIdleMs = 0;
    }
}
