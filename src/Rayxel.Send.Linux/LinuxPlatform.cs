using Rayxel.Link;
using Rayxel.Link.Input;
using Rayxel.Link.Media;
using Rayxel.Link.Sender;

namespace Rayxel.Send.Linux;

/// <summary>
/// A sender on Linux: a Steam Deck in Game Mode, or any desktop whose compositor offers a
/// PipeWire video source. The pieces:
/// <list type="bullet">
/// <item><see cref="PipeWireCapture"/> - the picture, in NV12, from the node named in the settings (gamescope).</item>
/// <item><see cref="VaEncoder"/> - HEVC and AV1 on the GPU's own encoder; <see cref="PyroWaveEncoder"/> for PyroWave.</item>
/// <item><see cref="X11Cursor"/> - the pointer, which gamescope leaves out of the stream.</item>
/// <item><see cref="UInputPad"/>, <see cref="UInputDesk"/> - the viewer's controller, keyboard and mouse.</item>
/// </list>
/// </summary>
public sealed class LinuxPlatform : ISenderPlatform
{
    public string Name { get; } = Describe();

    public string Model { get; } = File.Exists("/sys/devices/virtual/dmi/id/board_vendor") &&
                                   File.ReadAllText("/sys/devices/virtual/dmi/id/board_vendor").Trim() == "Valve"
        ? "Steam Deck"
        : "Linux PC";

    public string DefaultSource => "gamescope";

    public string DefaultDeviceName => Model == "Steam Deck" ? "Steam Deck" : Environment.MachineName;

    public ICaptureSource CreateCapture(string source) => new PipeWireCapture(source);

    public ICursorSource? CreateCursor() => X11Cursor.TryOpen();

    public bool CanEncode(Wire.Codec codec, out string why) =>
        codec == Wire.Codec.PyroWave ? PyroWaveEncoder.Available(out why) : VaEncoder.Available(codec, out why);

    public IVideoEncoder? OpenEncoder(EncoderConfig config, out string why) =>
        config.Codec == Wire.Codec.PyroWave ? PyroWaveEncoder.Open(config, out why) : VaEncoder.Open(config, out why);

    public IPadSink? OpenPad(out string why) => UInputPad.Open(out why);

    public IDeskSink? OpenDesk(out string why) => UInputDesk.Open(out why);

    private static string Describe()
    {
        try
        {
            var release = File.ReadAllLines("/etc/os-release")
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1].Trim('"'));
            string name = release.GetValueOrDefault("NAME", "Linux") + " " + release.GetValueOrDefault("VERSION_ID", "");
            return Environment.GetEnvironmentVariable("GAMESCOPE_WAYLAND_DISPLAY") is not null ||
                   File.Exists("/usr/bin/gamescope")
                ? $"{name.Trim()} (gamescope)"
                : name.Trim();
        }
        catch (IOException)
        {
            return "Linux";
        }
    }
}
