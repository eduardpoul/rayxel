using System.Numerics;
using ImGuiNET;
using Rayxel.Capture;

namespace Rayxel.Ui;

/// <summary>
/// Says, on the picture itself, that the keyboard and mouse are going to the Deck and how
/// to get them back.
/// <para>
/// While they are captured this window ignores them, its pointer is hidden and its bar is
/// away, so without this there is nothing on screen to tell that state from a frozen app —
/// least of all in full screen, where the title bar that also says it cannot be seen. It
/// sits where the bar would be, which is the one place certain to be free.
/// </para>
/// </summary>
public static class CaptureBadge
{
    private const ImGuiWindowFlags Flags =
        ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings |
        ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoInputs;

    public static void Draw(AppModel model)
    {
        var f = Widgets.F;

        ImGui.SetNextWindowPos(new Vector2(model.Renderer.Width * 0.5f, model.Renderer.Height - Layout.S(24)),
                               ImGuiCond.Always, new Vector2(0.5f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Layout.S(14), Layout.S(8)));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Layout.S(Theme.RadiusControl));
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Layout.S(8), 0));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.Alpha(Theme.Layer, 0.92f));

        if (ImGui.Begin("##capture", Flags))
        {
            // The dot is the accent: this is a state the user chose, not a warning.
            float dot = Layout.S(8);
            var at = ImGui.GetCursorScreenPos();
            float line = ImGui.GetTextLineHeight();
            ImGui.GetWindowDrawList().AddCircleFilled(at + new Vector2(dot / 2f, line / 2f), dot / 2f, Theme.U32(Theme.Accent));
            ImGui.Dummy(new Vector2(dot, line));

            ImGui.SameLine();
            Widgets.Text(f.Caption, Theme.TextPrimary, Strings.T("captureOn"));
            ImGui.SameLine();
            Widgets.Text(f.Caption, Theme.TextTertiary, "·");
            ImGui.SameLine();
            Widgets.Text(f.Caption, Theme.TextSecondary, string.Format(Strings.T("captureBack"), DeskForwarder.Key.Name));
        }
        ImGui.End();

        ImGui.PopStyleColor();
        ImGui.PopStyleVar(3);
    }
}
