using System.Text.Json.Serialization;

namespace Rayxel.Link.Control;

/// <summary>
/// The sender's settings, as kept in settings.json beside its keys and as read and written
/// over the control socket. Changes to the port take effect when the sender next starts;
/// everything else at once (encoding settings from the next stream).
/// </summary>
public sealed class SenderSettings
{
    /// <summary>What viewers see this sender as. Empty: the machine's own name.</summary>
    public string Name { get; set; } = "";

    public int Port { get; set; } = Wire.DefaultPort;

    /// <summary>Switched on when the program starts. Off, it answers nobody until switched on.</summary>
    public bool StartEnabled { get; set; } = true;

    /// <summary>Let a paired viewer play with its own gamepad, keyboard and mouse here.</summary>
    public bool AllowInput { get; set; } = true;

    /// <summary>How long "may be found" lasts once switched on, in minutes.</summary>
    public int DiscoverableMinutes { get; set; } = 5;

    /// <summary>
    /// Also take viewers that were never paired, speaking version 1 of the protocol in the
    /// clear - older Rayxel builds. Never with input. Off unless asked for.
    /// </summary>
    public bool AllowUnpaired { get; set; }

    /// <summary>The video source: "gamescope" on a Deck, a display's index on Windows. Empty: the platform's own.</summary>
    public string Source { get; set; } = "";

    /// <summary>How many frames' worth of bits the rate control may borrow.</summary>
    public double BufferFrames { get; set; } = 2;

    /// <summary>Cap on any one frame as a multiple of the average; 0 for none.</summary>
    public double MaxFrame { get; set; }

    /// <summary>A keyframe every N frames; 0 for only when the viewer asks.</summary>
    public int Gop { get; set; }

    public SenderSettings Clone() => (SenderSettings)MemberwiseClone();
}

/// <summary>Where the sender stands, in one word for a user interface to show.</summary>
public static class SenderStates
{
    public const string Off = "off";                 // switched off: answers nobody
    public const string Waiting = "waiting";         // on, waiting for a paired viewer
    public const string Discoverable = "discoverable"; // on, and may be found and paired with
    public const string Pairing = "pairing";         // a viewer is pairing: the PIN is showing
    public const string Streaming = "streaming";     // a viewer is watching
}

public sealed class PairingInfo
{
    public string ViewerName { get; set; } = "";

    /// <summary>The six digits to type into the viewer; null until the keys have been exchanged.</summary>
    public string? Pin { get; set; }

    /// <summary>When the attempt lapses, Unix milliseconds.</summary>
    public long ExpiresMs { get; set; }
}

public sealed class ViewerInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public bool Paired { get; set; }
}

public sealed class StreamInfo
{
    public string Codec { get; set; } = "";
    public string? AskedCodec { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public double Mbps { get; set; }
    public int BitrateKbps { get; set; }
    public double EncodeMs { get; set; }
    public double EncodeMaxMs { get; set; }
    public string Encoder { get; set; } = "";
    public bool InputActive { get; set; }
}

public sealed class PairedViewer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public long PairedMs { get; set; }
    public long LastSeenMs { get; set; }
}

public sealed class CodecInfo
{
    public string Name { get; set; } = "";
    public bool Available { get; set; }
    public string? Why { get; set; }
}

/// <summary>The whole state at once: what <c>status</c> answers and what a subscriber is sent on every change.</summary>
public sealed class SenderStatus
{
    public string Version { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Model { get; set; } = "";
    public int Port { get; set; }

    public bool Enabled { get; set; }
    public string State { get; set; } = SenderStates.Off;

    /// <summary>Until when it may be found, Unix milliseconds; null when it may not.</summary>
    public long? DiscoverableUntilMs { get; set; }

    public PairingInfo? Pairing { get; set; }
    public ViewerInfo? Viewer { get; set; }
    public StreamInfo? Stream { get; set; }
    public List<PairedViewer> Viewers { get; set; } = [];
    public List<CodecInfo> Codecs { get; set; } = [];

    public bool AllowInput { get; set; }

    /// <summary>What is captured when the settings name no source: "gamescope", or a display's index.</summary>
    public string DefaultSource { get; set; } = "";

    /// <summary>
    /// A capture is open and kept. Ending the program now closes it, which can take gamescope
    /// - and the game - down with it; switching off does not.
    /// </summary>
    public bool CaptureHeld { get; set; }

    /// <summary>Something wrong worth showing: the port taken, the source gone.</summary>
    public string? Problem { get; set; }
}

/// <summary>
/// One request on the control socket, one JSON object per line. <see cref="Cmd"/> is one of:
/// <c>status</c>, <c>subscribe</c> (then a <c>status</c> event on every change, until the
/// connection closes), <c>set_enabled</c> (<see cref="Value"/>), <c>set_discoverable</c>
/// (<see cref="Value"/>), <c>set_allow_input</c> (<see cref="Value"/>: the one setting a
/// panel switches by itself, without sending all the others back), <c>cancel_pairing</c>,
/// <c>forget</c> (<see cref="Id"/>), <c>get_settings</c>, <c>set_settings</c>
/// (<see cref="Settings"/>), <c>stop_stream</c>, <c>quit</c> (<see cref="Force"/> to end even
/// while a capture is held; answered before the program ends).
/// </summary>
public sealed class ControlRequest
{
    public string Cmd { get; set; } = "";
    public bool? Value { get; set; }
    public string? Id { get; set; }
    public SenderSettings? Settings { get; set; }
    public bool? Force { get; set; }
}

/// <summary>
/// One line back: an answer (<see cref="Ok"/> set), or an event (<see cref="Event"/> set,
/// for subscribers).
/// </summary>
public sealed class ControlResponse
{
    public bool? Ok { get; set; }
    public string? Error { get; set; }
    public string? Event { get; set; }
    public SenderStatus? Status { get; set; }
    public SenderSettings? Settings { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
                             DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                             WriteIndented = false)]
[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(ControlResponse))]
[JsonSerializable(typeof(SenderStatus))]
[JsonSerializable(typeof(SenderSettings))]
public sealed partial class ControlJson : JsonSerializerContext;

/// <summary>Where the control socket is: one per user, in a place only that user can reach.</summary>
public static class ControlPaths
{
    public static string SocketPath()
    {
        // On Windows in the user's own temporary folder, which only that user can reach.
        // Beside the settings in %LOCALAPPDATA% was the first choice, but connections to a
        // socket there were refused ("invalid argument") on Windows 11 build 26300, while
        // the same socket in Temp took them. The server puts the file back should a
        // clean-up take it away.
        if (OperatingSystem.IsWindows())
            return Path.Combine(Path.GetTempPath(), "rayxel-send.sock");
        string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return string.IsNullOrEmpty(runtime)
            ? Path.Combine(Path.GetTempPath(), $"rayxel-send-{Environment.UserName}.sock")
            : Path.Combine(runtime, "rayxel-send.sock");
    }

    /// <summary>Where the sender keeps its settings, its keys and the viewers it knows.</summary>
    public static string ConfigDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rayxel", "Send");
        string? config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(string.IsNullOrEmpty(config) ? Path.Combine(home, ".config") : config, "rayxel-send");
    }
}
