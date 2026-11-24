using Rayxel.Link.Input;
using Rayxel.Link.Media;

namespace Rayxel.Link.Sender;

/// <summary>
/// What a sender needs from the machine it runs on: a picture, encoders, the pointer, and a
/// way to play its controller, keyboard and mouse. Everything else - the network, pairing,
/// sessions, the control socket - is the same everywhere and lives in this library.
/// Rayxel.Send.Linux provides one for SteamOS and other Linux desktops (PipeWire, VA-API,
/// uinput), Rayxel.Send.Windows one for Windows (desktop duplication, Media Foundation,
/// SendInput).
/// </summary>
public interface ISenderPlatform
{
    /// <summary>For the log and the status: "SteamOS 3.9 (gamescope)", "Windows 11".</summary>
    string Name { get; }

    /// <summary>What a viewer is told this machine is, in ANNOUNCE: "Steam Deck", "PC".</summary>
    string Model { get; }

    /// <summary>The source asked for when the settings name none: "gamescope", or a display's index.</summary>
    string DefaultSource { get; }

    /// <summary>A name to start with: the machine's own, or "Steam Deck".</summary>
    string DefaultDeviceName { get; }

    /// <summary>A capture of <paramref name="source"/>, not yet started.</summary>
    ICaptureSource CreateCapture(string source);

    /// <summary>The pointer, or null where the capture already has it in the picture or it cannot be had.</summary>
    ICursorSource? CreateCursor();

    /// <summary>Whether this machine can encode <paramref name="codec"/> at all; when not, why. For the status and the log.</summary>
    bool CanEncode(Wire.Codec codec, out string why);

    /// <summary>An encoder, or null with the reason. PyroWave is <see cref="PyroWaveEncoder"/> everywhere.</summary>
    IVideoEncoder? OpenEncoder(EncoderConfig config, out string why);

    /// <summary>A virtual gamepad, made when the viewer's first controller state arrives; null with the reason.</summary>
    IPadSink? OpenPad(out string why);

    /// <summary>A virtual keyboard and mouse, made when the viewer's first key or mouse movement arrives.</summary>
    IDeskSink? OpenDesk(out string why);
}
