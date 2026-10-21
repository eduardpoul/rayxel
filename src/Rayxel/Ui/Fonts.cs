using System.Runtime.InteropServices;
using ImGuiNET;
using Rayxel.Diagnostics;

namespace Rayxel.Ui;

/// <summary>
/// The type ramp, built once into the ImGui atlas.
/// <para>
/// Windows' own: Segoe UI for copy, Segoe UI Semibold for anything that needs weight, at
/// the Fluent ramp's sizes. Both ship with every Windows since 7 and carry the Cyrillic the
/// interface needs, so nothing has to be bundled. Segoe UI's figures are tabular, which is
/// what lets measured values repaint without the digits shuffling sideways — the reason a
/// monospace face was used for them before.
/// </para>
/// </summary>
public sealed class Fonts : IDisposable
{
    public ImFontPtr Caption;     // 13  secondary lines, units, captions — Fluent says 12, which read as fine print over video
    public ImFontPtr Body;        // 14  almost everything
    public ImFontPtr BodyStrong;  // 14  semibold: headers inside a flyout, values that lead
    public ImFontPtr Subtitle;    // 20  semibold: card and dialog titles
    public ImFontPtr Title;       // 28  semibold: the splash wordmark

    private IntPtr _ranges;

    /// <summary>
    /// Every codepoint the five languages and the copy actually use. ImGui's built-in
    /// Cyrillic range stops short of the punctuation the copy leans on — the arrow in
    /// "1080 → 1440", the middots, the dashes and ellipses would all render as boxes.
    /// </summary>
    private static readonly ushort[] GlyphRanges =
    [
        0x0020, 0x00FF,   // Latin, Latin-1 supplement: umlauts, accents, and the × in 1920×1080
        0x0400, 0x045F,   // Cyrillic, including Ukrainian i / yi / ye
        0x0490, 0x0491,   // Ukrainian ghe with upturn
        0x2010, 0x203A,   // dashes, curly quotes, ellipsis
        0x2190, 0x2193,   // arrows
        0
    ];

    public void Build(ImGuiIOPtr io)
    {
        // The range array must outlive the call: ImGui keeps the pointer and reads it
        // when the atlas is actually rasterised, not here.
        _ranges = Marshal.AllocHGlobal(GlyphRanges.Length * sizeof(ushort));
        for (int i = 0; i < GlyphRanges.Length; i++)
            Marshal.WriteInt16(_ranges, i * sizeof(ushort), (short)GlyphRanges[i]);

        CreateConfig();

        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        string? regular = FindFirst(system, "segoeui.ttf");
        string? strong = FindFirst(system, "seguisb.ttf", "segoeuib.ttf") ?? regular;

        if (regular is null || strong is null)
        {
            // Nothing usable at all. ImGui's built-in font has no Cyrillic, so the UI would
            // be boxes - but boxes still beat refusing to start.
            io.Fonts.AddFontDefault();
            Log.Warn("Segoe UI not found; the UI will have no Cyrillic glyphs");
            return;
        }

        Log.Info($"fonts: {Path.GetFileName(regular)} / {Path.GetFileName(strong)}");

        Caption = Add(io, regular, 13);
        Body = Add(io, regular, 14);
        BodyStrong = Add(io, strong, 14);
        Subtitle = Add(io, strong, 20);
        Title = Add(io, strong, 28);

        unsafe { io.NativePtr->FontDefault = Body.NativePtr; }
    }

    // Rasterised once at the largest size the display will ask for, then scaled down at
    // runtime. Building at the small size and scaling up is what makes an overlay look
    // like a blurry screenshot of itself.
    private ImFontConfigPtr _config;

    /// <summary>
    /// Rasteriser settings shared by every size.
    /// <para>
    /// <c>RasterizerMultiply</c> scales the coverage each glyph writes into the atlas.
    /// stb_truetype renders without hinting or ClearType, so the same face comes out a
    /// shade lighter than DirectWrite draws it elsewhere on Windows; a little above 1 puts
    /// that weight back.
    /// </para>
    /// </summary>
    private unsafe void CreateConfig()
    {
        _config = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig())
        {
            RasterizerMultiply = 1.12f,
            OversampleH = 2,
            OversampleV = 1,
            PixelSnapH = true
        };
    }

    private ImFontPtr Add(ImGuiIOPtr io, string path, float size) =>
        io.Fonts.AddFontFromFileTTF(path, MathF.Round(Layout.T(size) / Layout.Factor * Layout.AtlasFactor),
                                    _config, _ranges);

    private static string? FindFirst(string directory, params string[] names)
    {
        if (!Directory.Exists(directory)) return null;
        foreach (var name in names)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public void Dispose()
    {
        if (_ranges != IntPtr.Zero) { Marshal.FreeHGlobal(_ranges); _ranges = IntPtr.Zero; }
        unsafe
        {
            if (_config.NativePtr != null) { _config.Destroy(); _config = new ImFontConfigPtr(); }
        }
    }
}
