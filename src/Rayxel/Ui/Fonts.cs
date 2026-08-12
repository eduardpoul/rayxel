using System.Runtime.InteropServices;
using ImGuiNET;
using Rayxel.Diagnostics;

namespace Rayxel.Ui;

/// <summary>
/// The type scale, built once into the ImGui atlas.
/// <para>
/// The design calls for Space Grotesk and Roboto Mono. Those are OFL fonts that have to
/// be bundled rather than fetched, so if they are not sitting in <c>Assets/Fonts</c> the
/// app falls back to system faces that have the same job — proportional for copy,
/// monospace for measured values. It says which it used rather than quietly looking wrong.
/// </para>
/// </summary>
public sealed class Fonts : IDisposable
{
    // The revision's type scale, at zoom 1. Nothing below 10px, which is the one rule the
    // handoff states twice.
    public ImFontPtr Wordmark;   // 32  splash
    public ImFontPtr Title;      // 22  device picker
    public ImFontPtr Subtitle;   // 19  no-signal title
    public ImFontPtr Name;       // 14.5 device name, status word
    public ImFontPtr Heading;    // 13.5 control names
    public ImFontPtr Body;       // 12.5 selects, body copy
    public ImFontPtr Label;      // 11.5 labels
    public ImFontPtr Small;      // 11  section labels, warnings
    public ImFontPtr Tiny;       // 10.5
    public ImFontPtr Micro;      // 10  floor

    public ImFontPtr MonoLarge;  // 17  telemetry resolution
    public ImFontPtr MonoMode;   // 15  capture-mode row
    public ImFontPtr MonoValue;  // 14  metric values
    public ImFontPtr MonoSlider; // 13  slider values
    public ImFontPtr MonoCost;   // 12  costs
    public ImFontPtr MonoSmall;  // 11  link rate, device info
    public ImFontPtr MonoTiny;   // 10  pills, counters, hotkeys
    public ImFontPtr MonoMicro;  // 10  same size; kept so callers need no rewrite

    /// <summary>True when the designed typefaces were found and used.</summary>
    public bool UsingDesignFonts { get; private set; }

    private IntPtr _ranges;

    /// <summary>
    /// Every codepoint the five languages and the copy actually use. ImGui's built-in
    /// Cyrillic range stops short of the punctuation the copy leans on — the arrow in
    /// "SDR -> HDR", the middot separators, the em dashes and ellipses would all render
    /// as boxes without this.
    /// </summary>
    private static readonly ushort[] GlyphRanges =
    [
        0x0020, 0x00FF,   // Latin, Latin-1 supplement: umlauts, accents, and the x in 1920x1080
        0x0400, 0x045F,   // Cyrillic, including Ukrainian i / yi / ye
        0x0490, 0x0491,   // Ukrainian ghe with upturn
        0x2010, 0x203A,   // dashes, curly quotes, ellipsis
        0x2190, 0x2193,   // the arrow in "SDR -> HDR"
        0
    ];

    public void Build(ImGuiIOPtr io)
    {
        // The range array must outlive the call: ImGui keeps the pointer and reads it
        // when the atlas is actually rasterised, not here.
        _ranges = Marshal.AllocHGlobal(GlyphRanges.Length * sizeof(ushort));
        for (int i = 0; i < GlyphRanges.Length; i++)
            Marshal.WriteInt16(_ranges, i * sizeof(ushort), (short)GlyphRanges[i]);

        // Medium first, both here and in the fallbacks. The design asks for weight 500,
        // and at these sizes over moving video a 400 simply does not hold together.
        CreateConfig();

        var assets = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
        string? ui = FindFirst(assets, "SpaceGrotesk-Medium.ttf", "SpaceGrotesk-SemiBold.ttf",
                               "SpaceGrotesk-Regular.ttf", "SpaceGrotesk*.ttf");
        string? mono = FindFirst(assets, "RobotoMono-Medium.ttf", "RobotoMono-SemiBold.ttf",
                                 "RobotoMono-Regular.ttf", "RobotoMono*.ttf");

        UsingDesignFonts = ui is not null && mono is not null;

        // Semibold and Bold rather than the regular faces. Segoe UI Regular is drawn for
        // documents on a white page; over a bright, moving picture it disappears.
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        ui ??= FindFirst(system, "seguisb.ttf", "segoeui.ttf");
        mono ??= FindFirst(system, "consolab.ttf", "CascadiaMono.ttf", "consola.ttf");

        if (ui is null || mono is null)
        {
            // Nothing usable at all. ImGui's built-in font has no Cyrillic, so the UI would
            // be boxes - but boxes still beat refusing to start.
            io.Fonts.AddFontDefault();
            Log.Warn("no TTF font found; the UI will have no Cyrillic glyphs");
            return;
        }

        Log.Info(UsingDesignFonts
            ? $"fonts: design set from {assets}"
            : $"fonts: design set not found in {assets}, using {Path.GetFileName(ui)} / {Path.GetFileName(mono)}");

        Wordmark = Add(io, ui, 32);
        Title = Add(io, ui, 22);
        Subtitle = Add(io, ui, 19);
        Name = Add(io, ui, 14.5f);
        Heading = Add(io, ui, 13.5f);
        Body = Add(io, ui, 12.5f);
        Label = Add(io, ui, 11.5f);
        Small = Add(io, ui, 11);
        Tiny = Add(io, ui, 10.5f);
        Micro = Add(io, ui, 10);

        MonoLarge = Add(io, mono, 17);
        MonoMode = Add(io, mono, 15);
        MonoValue = Add(io, mono, 14);
        MonoSlider = Add(io, mono, 13);
        MonoCost = Add(io, mono, 12);
        MonoSmall = Add(io, mono, 11);
        MonoTiny = Add(io, mono, 10);
        MonoMicro = MonoTiny;

        // Body is what most of the panel uses, so it is the one to inherit by default.
        unsafe { io.NativePtr->FontDefault = Body.NativePtr; }
    }

    // Rasterised once at the largest size the display will ask for, then scaled down at
    // runtime. Building at the small size and scaling up is what makes an overlay look
    // like a blurry screenshot of itself.
    private ImFontConfigPtr _config;

    /// <summary>
    /// Rasteriser settings shared by every size.
    /// <para>
    /// <c>RasterizerMultiply</c> scales the coverage each glyph writes into the atlas — the
    /// supported way to thicken type without a heavier face. A little above 1 turns thin
    /// stems from grey into something that survives being drawn over a moving picture.
    /// </para>
    /// </summary>
    private unsafe void CreateConfig()
    {
        _config = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig())
        {
            RasterizerMultiply = 1.18f,
            OversampleH = 2,
            OversampleV = 1,
            PixelSnapH = true
        };
    }

    private ImFontPtr Add(ImGuiIOPtr io, string path, float size) =>
        io.Fonts.AddFontFromFileTTF(path, MathF.Round(Layout.T(size) / Layout.Factor * Layout.AtlasFactor),
                                    _config, _ranges);

    private static string? FindFirst(string directory, params string[] patterns)
    {
        if (!Directory.Exists(directory)) return null;
        foreach (var pattern in patterns)
        {
            var hit = Directory.EnumerateFiles(directory, pattern).FirstOrDefault();
            if (hit is not null) return hit;
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
