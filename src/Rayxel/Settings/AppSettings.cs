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

    /// <summary>0 = own shader, 1 = NIS, 2 = the driver filter.</summary>
    public int Scaler { get; set; }

    public bool Hdr { get; set; } = true;
    public float NisSharpness { get; set; } = 0.4f;
    public float OverlayNits { get; set; } = 230f;

    /// <summary>
    /// Level the video is encoded at when our own shader converts it into a PQ swapchain.
    /// Separate from the overlay: the interface must stay dim enough not to burn an OLED,
    /// the picture should look like the console intended.
    /// </summary>
    public float VideoNits { get; set; } = 200f;

    public bool ShowTelemetry { get; set; }
    public bool ShowSettings { get; set; } = true;
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
