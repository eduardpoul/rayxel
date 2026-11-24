using Rayxel.Link;
using Rayxel.Link.Input;
using Rayxel.Link.Media;
using Rayxel.Link.Sender;

namespace Rayxel.Send;

/// <summary>
/// The machine's own platform with its capture swapped for a file (--frames-file): the same
/// encoders and the same network, and no capture opened, so nothing on screen is touched.
/// </summary>
internal sealed class FileSourcePlatform(ISenderPlatform inner, string path, int width, int height) : ISenderPlatform
{
    public string Name => inner.Name + ", pictures from a file";
    public string Model => inner.Model;
    public string DefaultSource => path;
    public string DefaultDeviceName => inner.DefaultDeviceName;

    public ICaptureSource CreateCapture(string source) => new FileFrameSource(path, width, height);

    public ICursorSource? CreateCursor() => null;

    public bool CanEncode(Wire.Codec codec, out string why) => inner.CanEncode(codec, out why);

    public IVideoEncoder? OpenEncoder(EncoderConfig config, out string why) => inner.OpenEncoder(config, out why);

    public IPadSink? OpenPad(out string why) => inner.OpenPad(out why);

    public IDeskSink? OpenDesk(out string why) => inner.OpenDesk(out why);
}
