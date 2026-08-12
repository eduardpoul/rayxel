using System.Numerics;
using ImGuiNET;

namespace Rayxel.Ui;

/// <summary>
/// The design tokens, verbatim from the handoff.
/// <para>
/// One deliberate departure: the design specifies <c>backdrop-filter: blur()</c> on both
/// panels, which an immediate-mode renderer cannot do without a second render pass over
/// the frame — and this overlay sits inside an 8.33 ms budget. The surfaces are instead
/// given slightly more alpha than specified, so they still read as glass rather than as
/// a film. Everything else is the specified value.
/// </para>
/// </summary>
public static class Theme
{
    private static Vector4 Rgb(int hex, float a = 1f) => new(
        ((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, a);

    public static readonly Vector4 Base = Rgb(0x080b11);
    public static readonly Vector4 TextPrimary = Rgb(0xe8ecf4);
    public static readonly Vector4 TextSecondary = Rgb(0xe8ecf4, 0.70f);
    public static readonly Vector4 TextMuted = Rgb(0xe8ecf4, 0.45f);
    public static readonly Vector4 TextFaint = Rgb(0xe8ecf4, 0.32f);

    public static readonly Vector4 TextDim = Rgb(0xe8ecf4, 0.35f);

    public static readonly Vector4 Accent = Rgb(0x8ab4ff);
    public static readonly Vector4 AccentText = Rgb(0xbcd3ff);

    public static readonly Vector4 Good = Rgb(0x7ddcb0);
    public static readonly Vector4 Warn = Rgb(0xe3bd80);
    public static readonly Vector4 Bad = Rgb(0xe08a7a);

    public static readonly Vector4 Divider = new(1f, 1f, 1f, 0.08f);
    public static readonly Vector4 CardInset = new(1f, 1f, 1f, 0.045f);
    public static readonly Vector4 CardRow = new(1f, 1f, 1f, 0.05f);
    public static readonly Vector4 CardHover = new(1f, 1f, 1f, 0.09f);
    public static readonly Vector4 CardActive = Rgb(0x8ab4ff, 0.13f);
    public static readonly Vector4 RowInactive = new(1f, 1f, 1f, 0.03f);

    /// <summary>
    /// The settings panel is opaque. The design asks for glass over the video, and over a
    /// still mock-up that reads beautifully — over a moving picture the text sits on top of
    /// whatever colour happens to be behind it that frame and becomes unreadable.
    /// </summary>
    public static readonly Vector4 PanelSolid = Rgb(0x0d1420, 0.985f);

    /// <summary>
    /// The telemetry block. The design asks for .30 over a 4px blur; without the blur that
    /// is unreadable over a bright frame, so the alpha carries what the blur would have.
    /// Blur is not coming back — it needs a second pass over the frame, inside an 8.33 ms
    /// budget, to make text prettier.
    /// </summary>
    public static readonly Vector4 TelemetryBg = Rgb(0x080b11, 0.93f);

    /// <summary>Mode table. Specified as .96 over blur 22; opaque instead, same reason.</summary>
    public static readonly Vector4 Popover = Rgb(0x101621, 1.0f);

    /// <summary>The "Параметры" chip that brings the panel back.</summary>
    public static readonly Vector4 ChipBg = Rgb(0x141b28, 0.94f);

    // The revision's radii, at zoom 1.
    public const float RadiusPill = 99f;
    public const float RadiusModal = 16f;      // no-signal / confirm card
    public const float RadiusBlock = 13f;      // telemetry block
    public const float RadiusPopover = 12f;    // mode table
    public const float RadiusCard = 10f;       // cards, rows, selects, buttons
    public const float RadiusRow = 10f;
    public const float RadiusTableRow = 8f;
    public const float RadiusSelect = 9f;      // audio selects, popover rows

    // Kept so existing call sites keep compiling; the design no longer distinguishes it.
    public const float RadiusOverlay = RadiusModal;

    public static uint U32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

    /// <summary>Same colour at a different alpha, for disabled and hover variants.</summary>
    public static Vector4 Alpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, a);

    /// <summary>
    /// Applies the parts of the design that ImGui can express as global style, so
    /// individual widgets do not each have to push the same values.
    /// </summary>
    public static void Apply()
    {
        var s = ImGui.GetStyle();

        // Every metric goes through the scale, so the panel keeps its proportions on a
        // 3440-wide display instead of turning into fine print.
        s.WindowRounding = Layout.S(RadiusModal);
        s.ChildRounding = Layout.S(RadiusCard);
        s.FrameRounding = Layout.S(RadiusCard);
        s.PopupRounding = Layout.S(RadiusPopover);
        s.GrabRounding = RadiusPill;
        s.ScrollbarRounding = RadiusPill;

        // The revision's spacing: panel padding 12/13, panel gap 8, card padding 8–11.
        s.WindowPadding = new Vector2(Layout.S(13), Layout.S(12));
        s.FramePadding = new Vector2(Layout.S(10), Layout.S(8));
        s.ItemSpacing = new Vector2(Layout.S(8), Layout.S(8));
        s.ItemInnerSpacing = new Vector2(Layout.S(5), Layout.S(3));
        s.ScrollbarSize = Layout.S(7);
        s.GrabMinSize = Layout.S(12);

        s.WindowBorderSize = 0;
        s.FrameBorderSize = 0;
        s.PopupBorderSize = 0;
        s.ChildBorderSize = 0;

        Set(ImGuiCol.Text, TextPrimary);
        Set(ImGuiCol.TextDisabled, TextMuted);
        Set(ImGuiCol.WindowBg, PanelSolid);
        Set(ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0));
        Set(ImGuiCol.PopupBg, Popover);
        Set(ImGuiCol.FrameBg, CardInset);
        Set(ImGuiCol.FrameBgHovered, CardHover);
        Set(ImGuiCol.FrameBgActive, CardActive);
        Set(ImGuiCol.Separator, Divider);
        Set(ImGuiCol.Border, Divider);
        Set(ImGuiCol.SliderGrab, Accent);
        Set(ImGuiCol.SliderGrabActive, AccentText);
        Set(ImGuiCol.CheckMark, Accent);
        Set(ImGuiCol.Button, CardInset);
        Set(ImGuiCol.ButtonHovered, CardHover);
        Set(ImGuiCol.ButtonActive, CardActive);
        Set(ImGuiCol.Header, CardActive);
        Set(ImGuiCol.HeaderHovered, CardHover);
        Set(ImGuiCol.HeaderActive, CardActive);
        Set(ImGuiCol.ScrollbarBg, new Vector4(0, 0, 0, 0));
        Set(ImGuiCol.ScrollbarGrab, new Vector4(1, 1, 1, 0.10f));
        Set(ImGuiCol.ScrollbarGrabHovered, new Vector4(1, 1, 1, 0.16f));
        Set(ImGuiCol.ScrollbarGrabActive, Alpha(Accent, 0.4f));
        Set(ImGuiCol.TitleBg, new Vector4(0, 0, 0, 0));
        Set(ImGuiCol.TitleBgActive, new Vector4(0, 0, 0, 0));
        Set(ImGuiCol.PlotLines, Accent);
        Set(ImGuiCol.PlotHistogram, Accent);

        static void Set(ImGuiCol which, Vector4 colour) => ImGui.GetStyle().Colors[(int)which] = colour;
    }
}
