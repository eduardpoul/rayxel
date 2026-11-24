using System.Numerics;
using ImGuiNET;
using Rayxel.Capture;
using Rayxel.Native;

namespace Rayxel.Ui;

/// <summary>
/// What there is over the picture: nothing, until the pointer moves. Then two small plates
/// in the top corners - on the left the way back to the sources and what is being watched,
/// on the right telemetry, settings and full screen - which go away again three seconds
/// after it stops. Everything else is in the settings, which the gear opens beside the
/// picture, and on the Settings page.
/// <para>
/// It used to be a bar along the bottom edge with four flyouts. Someone is playing through
/// this window; the less of the window is controls, the better, and controls that only
/// change something now and then do not need to be on the picture at all.
/// </para>
/// <para>
/// All of it is drawn into the swapchain like the rest of the overlay. A real window over
/// the video would take it off its overlay plane - a whole frame of latency, for a button.
/// </para>
/// </summary>
internal static class ViewerOverlay
{
    private const ImGuiWindowFlags PlateFlags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
        | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
        | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing
        | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoCollapse;

    /// <summary>How tall a plate is, for whatever has to stay clear of it - the telemetry under it.</summary>
    public static float PlateHeight => Layout.S(36) + Layout.S(4) * 2;

    public static void Draw(AppModel model)
    {
        bool hovered = false;
        float fade = model.BarFade;

        if (fade > 0.001f)
        {
            // A few pixels of travel as they fade in, the way a Fluent surface enters.
            float slide = (1f - fade) * Layout.S(6);
            ImGui.PushStyleVar(ImGuiStyleVar.Alpha, fade);
            hovered |= Source(model, slide);
            hovered |= Controls(model, slide);
            ImGui.PopStyleVar();
        }

        if (model.Flyout == Flyout.Settings)
        {
            hovered |= SettingsUi.Panel(model);

            // Light dismiss, as every Fluent flyout does: a click on anything that is not
            // ours closes it. AllowWhenBlockedByActiveItem matters: the press that lands on
            // a row makes it the active item in this very frame, and without the flag no
            // window is reported hovered and the panel would close on its own controls.
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left)
                && !ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow
                                          | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem
                                          | ImGuiHoveredFlags.AllowWhenBlockedByPopup))
                model.Flyout = Flyout.None;
        }

        // Anything under the pointer, being dragged, or dropped open keeps the plates up.
        if (hovered || ImGui.IsAnyItemActive()
            || ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId | ImGuiPopupFlags.AnyPopupLevel))
            model.PanelActivity();
    }

    private static void BeginPlate(string id, Vector2 at, Vector2 pivot)
    {
        ImGui.SetNextWindowPos(at, ImGuiCond.Always, pivot);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Layout.S(4)));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Layout.S(2), 0));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Layout.S(Theme.RadiusOverlay));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, Vector2.One);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.Alpha(Theme.Layer, 0.86f));
        ImGui.PushStyleColor(ImGuiCol.Border, Theme.SurfaceStroke);
        ImGui.Begin(id, PlateFlags);
    }

    private static bool EndPlate()
    {
        bool hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        ImGui.End();
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(4);
        return hovered;
    }

    /// <summary>The way back to the sources, and what is on the screen: its name and, quietly, how it arrives.</summary>
    private static bool Source(AppModel model, float slide)
    {
        var f = Widgets.F;
        BeginPlate("##sourceplate", new Vector2(Layout.S(12), Layout.S(12) + slide), Vector2.Zero);

        if (Widgets.IconButton("##back", Icon.Back, false, Theme.TextPrimary, Strings.T("ttBack")))
        {
            model.Flyout = Flyout.None;
            model.Screen = Screen.Sources;
        }

        string name = NetDevice.DisplayName(model.Pump.Device, model.Devices);
        string state = StateLine(model);
        var nameSize = Widgets.Measure(f.BodyStrong, name);
        var stateSize = Widgets.Measure(f.Caption, state);
        float line = Layout.S(36), gapAfter = Layout.S(12);
        ImGui.SameLine(0, Layout.S(4));
        var at = ImGui.GetCursorScreenPos();
        float width = Math.Max(nameSize.X, stateSize.X) + gapAfter;
        ImGui.Dummy(new Vector2(width, line));

        var draw = ImGui.GetWindowDrawList();
        float top = at.Y + (line - nameSize.Y - stateSize.Y) / 2f;
        Widgets.DrawText(draw, f.BodyStrong, new Vector2(at.X, top), Theme.TextPrimary, name);
        Widgets.DrawText(draw, f.Caption, new Vector2(at.X, top + nameSize.Y), Theme.TextSecondary, state);

        return EndPlate();
    }

    /// <summary>How the picture arrives, in one short line: over the network with which codec, or the card's mode.</summary>
    private static string StateLine(AppModel model)
    {
        if (model.Status == AppStatus.NoSignal) return Strings.T("stNoSignal");
        var mode = model.Pump.CurrentMode;
        double fps = model.Pump.State == CaptureState.Streaming ? model.Shown.MeasuredFps : model.Pump.CurrentFps;
        string rate = $"{Strings.Num(fps, "0")} {Strings.T("hz")}";

        if (model.Pump.Device.Bus == DeviceBus.Network)
        {
            string codec = model.ShownNet?.Codec is { Length: > 0 } arriving ? arriving : NetDevice.CodecName(NetDevice.Codec);
            return $"{Strings.T("srcNetwork")} · {codec} · {rate}";
        }
        return mode is null ? rate : $"{mode.Width}×{mode.Height} · {rate}";
    }

    /// <summary>
    /// Telemetry, settings and full screen, and in front of them, when something quietly
    /// costs latency that can be fixed, a badge that says what - a click on it is the fix
    /// for a window Windows is composing.
    /// </summary>
    private static bool Controls(AppModel model, float slide)
    {
        // Beside the settings panel when it is open, not under it.
        float right = model.Renderer.Width - Layout.S(12) - (model.Flyout == Flyout.Settings ? SettingsUi.PanelWidth : 0);
        BeginPlate("##controlplate", new Vector2(right, Layout.S(12) + slide), new Vector2(1, 0));

        bool first = true;
        if (model.Warning is { } warning)
        {
            WarningBadge(model, warning);
            first = false;
        }

        if (!first) ImGui.SameLine();
        if (Widgets.IconButton("##tele", Icon.Pulse, model.ShowTelemetry,
                               model.ShowTelemetry ? Theme.AccentText : Theme.TextPrimary, Strings.T("ttTeleShort")))
            model.CycleTelemetry();

        ImGui.SameLine();
        bool open = model.Flyout == Flyout.Settings;
        if (Widgets.IconButton("##settings", Icon.Gear, open, open ? Theme.AccentText : Theme.TextPrimary, Strings.T("ttSettings")))
            model.ToggleFlyout(Flyout.Settings);

        ImGui.SameLine();
        bool full = model.WindowKind != WindowKind.Windowed;
        if (Widgets.IconButton("##full", full ? Icon.BackToWindow : Icon.FullScreen, false, Theme.TextPrimary,
                               Strings.T(full ? "ttWindow" : "ttFull")))
            model.SetWindowKind(full ? WindowKind.Windowed : WindowKind.BorderlessFullscreen);

        return EndPlate();
    }

    /// <summary>
    /// A red "!" while something is costing latency that the user can fix: the window being
    /// composed by Windows, or the processing not fitting in a frame. The explanation is its
    /// tooltip, and for a composed window a click is the fix.
    /// </summary>
    private static void WarningBadge(AppModel model, string warning)
    {
        bool composed = warning == "composed";
        bool canFix = composed && model.WindowKind == WindowKind.Windowed;

        var size = new Vector2(Layout.S(36));
        if (Widgets.SubtleButton("##warning", size, false, out var min) && canFix)
            model.SetWindowKind(WindowKind.BorderlessFullscreen);
        Icons.SeverityBadge(ImGui.GetWindowDrawList(), min + size / 2f, Layout.S(8), Theme.Alert, Theme.TextPrimary, exclamation: true);

        if (!ImGui.IsItemHovered(ImGuiHoveredFlags.ForTooltip)) return;
        string text = composed
            ? Strings.T("infoComposedTitle") + "\n" + string.Format(Strings.T("infoComposedBody"),
                  Strings.Num(model.BudgetMs > 0 ? model.BudgetMs : 8.33, "0.0"))
            : Strings.T("infoBudgetTitle") + "\n" + Strings.T("infoBudgetBody");
        if (canFix) text += "\n\n" + Strings.T("warnFix");
        Widgets.ShowTooltip(text);
    }
}
