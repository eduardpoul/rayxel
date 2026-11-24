namespace Rayxel.Link.Media;

/// <summary>
/// Pictures from a file instead of a capture: raw NV12 frames one after another, BT.709
/// limited range, played in a loop at 60 a second. For testing a viewer and the codecs
/// without going near gamescope - a capture opened and closed while a game runs is the one
/// thing that can take the game down.
/// </summary>
public sealed unsafe class FileFrameSource : ICaptureSource
{
    private readonly string _path;
    private readonly int _width, _height;
    private byte[]? _frames;
    private int _count;
    private Thread? _thread;
    private volatile bool _running;

    public FileFrameSource(string path, int width, int height)
    {
        _path = path;
        _width = width;
        _height = height;
    }

    public event Action<CaptureFormat>? FormatChanged;
    public event FrameHandler? Frame;
    /// <summary>A file in memory does not fail once it has been read.</summary>
    public event Action<string>? Failed { add { } remove { } }

    public bool Streaming => _running;

    public bool Start(int width, int height, out string why)
    {
        int frame = _width * _height * 3 / 2;
        try
        {
            // Read whole: the pictures are sent from memory, so reading never sets the pace.
            _frames = GC.AllocateUninitializedArray<byte>((int)Math.Min(new FileInfo(_path).Length, int.MaxValue), pinned: true);
            using (var file = File.OpenRead(_path)) file.ReadExactly(_frames);
            _count = _frames.Length / frame;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            why = $"cannot read {_path}: {ex.Message}";
            return false;
        }
        if (_count == 0)
        {
            why = $"{_path} holds no whole {_width}x{_height} NV12 picture";
            return false;
        }
        if (width > 0 && (width != _width || height != _height))
            LinkLog.Info($"frames   : the file's pictures are {_width}x{_height} and go out at that size");

        why = "";
        _running = true;
        _thread = new Thread(Play) { IsBackground = true, Name = "frames file" };
        _thread.Start();
        return true;
    }

    private void Play()
    {
        FormatChanged?.Invoke(new CaptureFormat(_width, _height, false, false,
                                                $"NV12 {_width}x{_height}, BT.709 limited, {_count} pictures from {_path}"));
        int size = _width * _height * 3 / 2;
        long period = 1_000_000_000 / 60, next = Clock.NowNs;
        int index = 0;
        fixed (byte* frames = _frames)
        {
            while (_running)
            {
                long now = Clock.NowNs;
                if (now < next)
                {
                    Thread.Sleep(TimeSpan.FromTicks(Math.Max(1, (next - now) / 100)));
                    continue;
                }
                next += period;
                var frame = new CapturedFrame(frames + (long)index * size, size, _width, _width, _height, Clock.NowNs);
                Frame?.Invoke(frame);
                index = (index + 1) % _count;
            }
        }
    }

    public void Pause() => _running = false;

    public void Dispose()
    {
        _running = false;
        _thread?.Join(1000);
        _frames = null;
    }
}
