using System.Text.Json;
using System.Text.Json.Serialization;
using Rayxel.Diagnostics;

namespace Rayxel.Settings;

/// <summary>
/// What survives a restart.
/// <para>
/// Stored under <c>%LOCALAPPDATA%\Rayxel</c> rather than roaming AppData on purpose:
/// almost everything here names a specific piece of this machine's hardware — a device
/// interface path, an audio endpoint id, a GPU filter — and carrying those to another
/// machine would restore settings that cannot possibly apply there.
/// </para>
/// </summary>
public sealed class AppSettings
{
    public string Language { get; set; } = "ru";

    /// <summary>The upscaler: 0 = none, 1 = DLSS, 2 = FSR.</summary>
    public int Upscaler { get; set; }

    /// <summary>Frame generation: 0 none, 1 DLSS Frame Generation.</summary>
    public int FrameGen { get; set; }

    /// <summary>Frames shown for each real one: "auto", "2", "3" or "4".</summary>
    public string FrameMultiplier { get; set; } = "2";

    /// <summary>
    /// What versions before this one kept instead, when there were five to choose from:
    /// 1 NIS, 2 RTX VSR, 3 DLSS, 4 FSR. Read once, turned into <see cref="Upscaler"/>, and
    /// not written again.
    /// </summary>
    [JsonPropertyName("Scaler"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ScalerBefore { get; set; }

    public bool Hdr { get; set; } = true;

    /// <summary>What the source is taken to be: 0 = what the device says, 1 = SDR, 2 = HDR10.</summary>
    public int SourceRange { get; set; }

    /// <summary>An HDR source shown as it is, untouched (true), or converted to SDR.</summary>
    public bool HdrSourceAsIs { get; set; } = true;

    /// <summary>
    /// Which network DLSS is asked for: 11 = K, 12 = L, 13 = M. L to begin with;
    /// <see cref="Render.DlssScaler.Model"/> says why.
    /// </summary>
    public int DlssModel { get; set; } = (int)Render.DlssModel.L;

    /// <summary>
    /// Which upscaler FSR is asked for, by the name AMD's library offers it under ("3.1.6").
    /// With neither this nor <see cref="FsrModel"/> set, it is the newest the card runs.
    /// </summary>
    public string? FsrVersion { get; set; }

    /// <summary>
    /// Which network of FSR 4 is pressed on AMD's library: 0 none in particular, 1 the
    /// full one, 2 the lighter one, 3 the full one as built for Radeon RX 6000.
    /// </summary>
    public int FsrModel { get; set; }
    public float OverlayNits { get; set; } = 230f;

    /// <summary>A Steam Deck's bitrate follows the link (true), or is <see cref="NetBitrateKbps"/>.</summary>
    public bool NetAdaptive { get; set; } = true;

    /// <summary>The fixed bitrate a Steam Deck is asked for, in kbit/s, when it does not follow the link.</summary>
    public int NetBitrateKbps { get; set; } = 20_000;

    /// <summary>
    /// Level the video is encoded at when our own shader converts it into a PQ swapchain.
    /// Separate from the overlay: the interface must stay dim enough not to burn an OLED,
    /// the picture should look like the console intended.
    /// </summary>
    public float VideoNits { get; set; } = 200f;

    public bool ShowTelemetry { get; set; }

    /// <summary>The full card rather than the one-line readout.</summary>
    public bool TelemetryDetailed { get; set; }
    public bool Borderless { get; set; }

    /// <summary>Device interface path. The only device identity that stays put.</summary>
    public string? DeviceLink { get; set; }

    public int ModeWidth { get; set; }
    public int ModeHeight { get; set; }
    public double ModeFps { get; set; }
    public string? ModeFormat { get; set; }

    public bool AudioEnabled { get; set; } = true;
    public string? AudioInputId { get; set; }
    public string? AudioOutputId { get; set; }

    [JsonIgnore]
    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rayxel");

    [JsonIgnore]
    public static string FilePath => Path.Combine(Directory, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    /// <summary>
    /// Reads the file, or returns defaults. A corrupt or half-written file is replaced by
    /// defaults rather than allowed to stop the app — settings are a convenience, and
    /// refusing to start over them would be absurd.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                Log.Info($"settings: none yet, using defaults ({FilePath})");
                return new AppSettings();
            }

            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
            if (loaded is null) throw new InvalidDataException("empty");

            // A file from before NIS and RTX VSR went: its numbers meant something else.
            if (loaded.ScalerBefore is { } before)
            {
                loaded.Upscaler = before switch { 3 => 1, 4 => 2, _ => 0 };
                loaded.ScalerBefore = null;
            }

            Log.Info($"settings loaded from {FilePath}");
            return loaded;
        }
        catch (Exception ex)
        {
            Log.Warn($"settings unreadable ({ex.Message}); starting from defaults");
            return new AppSettings();
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            // Written beside the target and moved into place, so an interrupted save
            // leaves the previous settings intact instead of a truncated file.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, ToJson());
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn($"settings not saved: {ex.Message}");
        }
    }
}
