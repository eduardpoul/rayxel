using System.Numerics;
using ImGuiNET;
using Microsoft.Win32;

namespace Rayxel.Ui;

/// <summary>
/// Windows 11's own dark theme (Fluent 2), as far as an immediate-mode renderer can carry it.
/// <para>
/// The previous palette was a navy base with pastel accents and glows, and it read as a
/// generated dashboard rather than as something that belongs on Windows. These are the
/// Fluent dark tokens instead: neutral greys, the user's own accent colour, 4px controls
/// and 8px surfaces, strokes rather than tinted fills.
/// </para>
/// <para>
/// What it cannot carry is Mica and acrylic. Both are a blur of what is behind the
/// surface, and that is a second pass over the frame inside an 8.33 ms budget. Surfaces
/// are opaque instead; over moving video that is also simply more readable.
/// </para>
/// </summary>
public static class Theme
{
    private static Vector4 Rgb(int hex, float a = 1f) => new(
        ((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, a);

    private static Vector4 White(float a) => new(1f, 1f, 1f, a);

    // Backgrounds, darkest first.
    public static readonly Vector4 Stage = Rgb(0x1c1c1c);      // behind the no-signal and picker screens
    public static readonly Vector4 Layer = Rgb(0x202020);      // dialogs' footer, telemetry
    public static readonly Vector4 Card = Rgb(0x2b2b2b);       // cards, dialogs
    public static readonly Vector4 Surface = Rgb(0x2c2c2c);    // the bar and its flyouts

    public static readonly Vector4 SurfaceStroke = Rgb(0x757575, 0.40f);
    public static readonly Vector4 CardStroke = Rgb(0x000000, 0.25f);
    public static readonly Vector4 Divider = White(0.08f);

    // Text. Tertiary is the quietest colour still allowed to carry information.
    public static readonly Vector4 TextPrimary = White(1f);
    public static readonly Vector4 TextSecondary = White(0.786f);
    public static readonly Vector4 TextTertiary = White(0.544f);
    public static readonly Vector4 TextDisabled = White(0.363f);
    public static readonly Vector4 TextOnAccent = Rgb(0x000000);

    // Control fills. "Subtle" is the transparent kind that only shows on hover.
    public static readonly Vector4 ControlFill = White(0.061f);
    public static readonly Vector4 ControlFillHover = White(0.084f);
    public static readonly Vector4 ControlFillPressed = White(0.033f);
    public static readonly Vector4 ControlStroke = White(0.07f);
    public static readonly Vector4 SubtleHover = White(0.061f);
    public static readonly Vector4 SubtlePressed = White(0.042f);
    public static readonly Vector4 ControlStrong = White(0.544f);   // unfilled slider track, switch outline

    // Semantic colours, used sparingly: a status, never decoration.
    public static readonly Vector4 Success = Rgb(0x6ccb5f);
    public static readonly Vector4 Caution = Rgb(0xfce100);
    public static readonly Vector4 Critical = Rgb(0xff99a4);
    public static readonly Vector4 CautionBackground = Rgb(0x433519);

    /// <summary>
    /// A solid red for the one badge that has to be seen at a glance. Fluent's own critical
    /// colour on dark is a pale pink, which at 16px reads as decoration rather than as "!".
    /// </summary>
    public static readonly Vector4 Alert = Rgb(0xe5484d);
    public static readonly Vector4 CriticalBackground = Rgb(0x442726);

    /// <summary>
    /// The user's accent, in the lighter variant Windows itself uses on dark surfaces.
    /// Read once at start-up; Windows' default blue if the palette is not there.
    /// </summary>
    public static readonly Vector4 Accent;

    /// <summary>The lightest accent variant, for accent-coloured text on dark.</summary>
    public static readonly Vector4 AccentText;

    public const float RadiusControl = 4f;
    public const float RadiusOverlay = 8f;

    static Theme()
    {
        (Accent, AccentText) = ReadAccent() ?? (Rgb(0x60cdff), Rgb(0x99ebff));
    }

    /// <summary>
    /// The accent palette Windows derives from the chosen colour: eight RGBA entries,
    /// lightest first. Light2 is what dark-theme controls fill with, Light3 what accent
    /// text uses — the base colour itself is too dark to read on a dark surface.
    /// </summary>
    private static (Vector4, Vector4)? ReadAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
            if (key?.GetValue("AccentPalette") is not byte[] { Length: >= 32 } palette) return null;

            static Vector4 At(byte[] p, int i) => new(p[i * 4] / 255f, p[i * 4 + 1] / 255f, p[i * 4 + 2] / 255f, 1f);
            return (At(palette, 1), At(palette, 0));
        }
        catch
        {
            return null;
        }
    }

    public static uint U32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

    /// <summary>Same colour at a different alpha.</summary>
    public static Vector4 Alpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, a);

    /// <summary>
    /// What ImGui can take as global style. Most of the interface draws its own controls
    /// onto the draw list; this covers the parts ImGui still draws itself — combo popups,
    /// tooltips, scrollbars.
    /// </summary>
    public static void Apply()
    {
        var s = ImGui.GetStyle();

        s.WindowRounding = Layout.S(RadiusOverlay);
        s.ChildRounding = Layout.S(RadiusControl);
        s.FrameRounding = Layout.S(RadiusControl);
        s.PopupRounding = Layout.S(RadiusOverlay);
        s.GrabRounding = 99f;
        s.ScrollbarRounding = 99f;

        s.WindowPadding = new Vector2(Layout.S(12), Layout.S(12));
        s.FramePadding = new Vector2(Layout.S(11), Layout.S(6));
        s.ItemSpacing = new Vector2(Layout.S(8), Layout.S(8));
        s.ItemInnerSpacing = new Vector2(Layout.S(6), Layout.S(4));
        s.ScrollbarSize = Layout.S(6);
        s.GrabMinSize = Layout.S(12);
        s.SelectableTextAlign = new Vector2(0f, 0.5f);

        s.WindowBorderSize = 1f;
        s.PopupBorderSize = 1f;
        s.FrameBorderSize = 0;
        s.ChildBorderSize = 0;

        Set(ImGuiCol.Text, TextPrimary);
        Set(ImGuiCol.TextDisabled, TextDisabled);
        Set(ImGuiCol.WindowBg, Surface);
        Set(ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0));
        Set(ImGuiCol.PopupBg, Surface);
        Set(ImGuiCol.Border, SurfaceStroke);
        Set(ImGuiCol.FrameBg, ControlFill);
        Set(ImGuiCol.FrameBgHovered, ControlFillHover);
        Set(ImGuiCol.FrameBgActive, ControlFillPressed);
        Set(ImGuiCol.Separator, Divider);
        Set(ImGuiCol.Button, ControlFill);
        Set(ImGuiCol.ButtonHovered, ControlFillHover);
        Set(ImGuiCol.ButtonActive, ControlFillPressed);
        Set(ImGuiCol.Header, SubtleHover);
        Set(ImGuiCol.HeaderHovered, SubtleHover);
        Set(ImGuiCol.HeaderActive, SubtlePressed);
        Set(ImGuiCol.ScrollbarBg, new Vector4(0, 0, 0, 0));
        Set(ImGuiCol.ScrollbarGrab, White(0.25f));
        Set(ImGuiCol.ScrollbarGrabHovered, White(0.4f));
        Set(ImGuiCol.ScrollbarGrabActive, White(0.5f));
        Set(ImGuiCol.CheckMark, Accent);
        Set(ImGuiCol.SliderGrab, Accent);
        Set(ImGuiCol.SliderGrabActive, AccentText);
        Set(ImGuiCol.TitleBg, new Vector4(0, 0, 0, 0));
        Set(ImGuiCol.TitleBgActive, new Vector4(0, 0, 0, 0));
        Set(ImGuiCol.ModalWindowDimBg, new Vector4(0, 0, 0, 0.3f));

        static void Set(ImGuiCol which, Vector4 colour) => ImGui.GetStyle().Colors[(int)which] = colour;
    }
}
