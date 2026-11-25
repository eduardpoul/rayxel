using System.Diagnostics;
using System.Text;
using Rayxel.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Rayxel.Render;

/// <summary>What <see cref="Renderer.SaveClip"/> is to do with a clip, when not simply everything.</summary>
public sealed class ClipOptions
{
    /// <summary>The ways to run, by the names of their folders ("none", "vsr", "dlss-L", "fsr"); null is every one there is.</summary>
    public string[]? Ways { get; init; }

    /// <summary>
    /// The motion estimates to run each way that goes by motion with, one after another:
    /// "fast", "medium", "max", "zero" for none given at all, and "truth" for the vectors in
    /// <see cref="TruthFolder"/>. Null is the one in use.
    /// </summary>
    public string[]? Flow { get; init; }

    /// <summary>
    /// For "truth": a folder of NNNN.flow files, one for each frame but the first, with the
    /// clip's real motion in them - as <see cref="OpticalFlow.SavePerPixel"/> writes vectors.
    /// A frame with no file there has the estimate.
    /// </summary>
    public string? TruthFolder { get; init; }

    /// <summary>The vectors themselves written out for each estimate, frame by frame.</summary>
    public bool Vectors { get; init; }

    /// <summary>With <see cref="Vectors"/>: only every so many frames, a long clip's vectors being large.</summary>
    public int VectorsEvery { get; init; } = 1;

    /// <summary>What each estimate costs the GPU, measured and logged.</summary>
    public bool Timed { get; init; }
}

public sealed partial class Renderer
{
    /// <summary>
    /// Diagnostic: a clip read from a file in place of one kept from a source, for
    /// <see cref="SaveClip"/>. The same frames can then be put through anything, as often
    /// as wanted, with nothing connected - and they can be frames whose motion is known.
    /// <para>
    /// The file is YUV4MPEG2 (.y4m), 8-bit 4:2:0, which is what
    /// <c>ffmpeg -i X -pix_fmt yuv420p X.y4m</c> writes; it is taken as BT.709 in the
    /// limited range, like the picture of an SDR source.
    /// </para>
    /// </summary>
    public bool LoadClip(string path, int most = 600)
    {
        ClipRate = 30;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);

            string header = Line(file);
            if (!header.StartsWith("YUV4MPEG2 ", StringComparison.Ordinal))
            {
                Log.Error($"clip: {path} is not a YUV4MPEG2 file");
                return false;
            }
            int width = 0, height = 0;
            string sampling = "420";
            foreach (string token in header.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1))
            {
                if (token[0] == 'W') int.TryParse(token.AsSpan(1), out width);
                else if (token[0] == 'H') int.TryParse(token.AsSpan(1), out height);
                else if (token[0] == 'C') sampling = token[1..];
                else if (token[0] == 'F' && token[1..].Split(':') is [var over, var under]
                         && double.TryParse(over, out double n) && double.TryParse(under, out double d) && n > 0 && d > 0)
                    ClipRate = n / d;
            }
            if (width < 64 || height < 64 || (width & 1) != 0 || (height & 1) != 0
                || !sampling.StartsWith("420", StringComparison.Ordinal)
                // "420p10" and the like are deeper than 8 bits; "420jpeg", "420paldv" only say where the colour sits.
                || (sampling.Length > 4 && sampling[3] == 'p' && char.IsDigit(sampling[4])))
            {
                Log.Error($"clip: {path} is {width}x{height} C{sampling}; an even-sized 8-bit 4:2:0 picture is needed");
                return false;
            }

            byte[] luma = new byte[width * height];
            byte[] u = new byte[width / 2 * (height / 2)], v = new byte[u.Length];

            lock (_context)
            {
                if (TestPattern) return false;
                ConfigureVideoLocked(width, height, isTenBit: false);
                if (_videoTexture is null) return false;
                DropClip();

                var desc = _videoTexture.Description;
                var staged = desc;
                staged.Usage = ResourceUsage.Staging;
                staged.BindFlags = BindFlags.None;
                staged.CPUAccessFlags = CpuAccessFlags.Write;
                using var staging = _device.CreateTexture2D(staged);

                var frames = new List<ID3D11Texture2D>();
                try
                {
                    while (frames.Count < most)
                    {
                        string mark = Line(file);
                        if (mark.Length == 0) break;
                        if (!mark.StartsWith("FRAME", StringComparison.Ordinal)) throw new InvalidDataException("a frame was expected");
                        if (!Fill(file, luma) || !Fill(file, u) || !Fill(file, v)) break;

                        var map = _context.Map(staging, 0, MapMode.Write);
                        unsafe
                        {
                            byte* top = (byte*)map.DataPointer;
                            for (int y = 0; y < height; y++)
                                luma.AsSpan(y * width, width).CopyTo(new Span<byte>(top + y * (long)map.RowPitch, width));
                            // The two colour planes go in as one, side by side in each pair of
                            // bytes, right below the brightness and at its pitch.
                            byte* below = top + (long)map.RowPitch * height;
                            int half = width / 2;
                            for (int y = 0; y < height / 2; y++)
                            {
                                byte* line = below + y * (long)map.RowPitch;
                                for (int x = 0; x < half; x++)
                                {
                                    line[x * 2] = u[y * half + x];
                                    line[x * 2 + 1] = v[y * half + x];
                                }
                            }
                        }
                        _context.Unmap(staging, 0);

                        var frame = _device.CreateTexture2D(desc);
                        frames.Add(frame);
                        _context.CopyResource(frame, staging);
                    }
                }
                catch
                {
                    foreach (var t in frames) t.Dispose();
                    throw;
                }

                if (frames.Count == 0)
                {
                    Log.Error($"clip: {path} has no frames");
                    return false;
                }

                _clip = [.. frames];
                _clipCount = frames.Count;
                _context.CopyResource(_videoTexture, _clip[0]);
                if (_vpTexture is not null) _context.CopyResource(_vpTexture, _clip[0]);
                _uploadSerial++;
                Log.Info($"clip: {frames.Count} frames of {width}x{height} read from {path}");
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"clip: {path} could not be read: {ex.Message}");
            return false;
        }

        // One line of the file's text, without its end; empty at the end of the file.
        static string Line(Stream from)
        {
            var text = new StringBuilder();
            for (int b; (b = from.ReadByte()) >= 0 && b != '\n';) text.Append((char)b);
            return text.ToString();
        }

        static bool Fill(Stream from, byte[] plane)
        {
            int got = 0;
            while (got < plane.Length)
            {
                int n = from.Read(plane, got, plane.Length - got);
                if (n <= 0) return false;
                got += n;
            }
            return true;
        }
    }

    /// <summary>
    /// Diagnostic: the clip kept by <see cref="StartClip"/> written to a file of the kind
    /// <see cref="LoadClip"/> reads, the frames as they arrived. A stretch of a game played
    /// once can then be put through anything afterwards, as often as wanted and with
    /// nothing connected. An 8-bit picture only.
    /// </summary>
    public bool WriteClip(string path, double rate)
    {
        lock (_context)
        {
            if (_clip is not { } clip || _clipCount == 0)
            {
                Log.Warn("clip: no frames were kept");
                return false;
            }
            var desc = clip[0].Description;
            if (desc.Format != Format.NV12)
            {
                Log.Warn($"clip: a {desc.Format} picture cannot be written to a file, only an 8-bit one");
                return false;
            }

            int width = (int)desc.Width, height = (int)desc.Height, half = width / 2;
            try
            {
                var staged = desc;
                staged.Usage = ResourceUsage.Staging;
                staged.BindFlags = BindFlags.None;
                staged.CPUAccessFlags = CpuAccessFlags.Read;
                staged.MiscFlags = ResourceOptionFlags.None;
                using var staging = _device.CreateTexture2D(staged);

                if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
                using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
                int perSecond = Math.Max(1, (int)Math.Round(rate));
                file.Write(Encoding.ASCII.GetBytes($"YUV4MPEG2 W{width} H{height} F{perSecond}:1 Ip A1:1 C420mpeg2\n"));
                byte[] mark = Encoding.ASCII.GetBytes("FRAME\n");
                byte[] u = new byte[half * (height / 2)], v = new byte[u.Length];

                for (int i = 0; i < _clipCount; i++)
                {
                    _context.CopyResource(staging, clip[i]);
                    var map = _context.Map(staging, 0, MapMode.Read);
                    try
                    {
                        file.Write(mark);
                        unsafe
                        {
                            byte* top = (byte*)map.DataPointer;
                            for (int y = 0; y < height; y++)
                                file.Write(new ReadOnlySpan<byte>(top + y * (long)map.RowPitch, width));
                            // The colour lies below the brightness, its two planes side by
                            // side in each pair of bytes; the file has them one after the other.
                            byte* below = top + (long)map.RowPitch * height;
                            for (int y = 0; y < height / 2; y++)
                            {
                                byte* line = below + y * (long)map.RowPitch;
                                for (int x = 0; x < half; x++)
                                {
                                    u[y * half + x] = line[x * 2];
                                    v[y * half + x] = line[x * 2 + 1];
                                }
                            }
                        }
                        file.Write(u);
                        file.Write(v);
                    }
                    finally { _context.Unmap(staging, 0); }
                }
                Log.Info($"clip: {_clipCount} frames of {width}x{height} at {perSecond} a second written to {path}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"clip: {path} could not be written: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Frames a second of the clip <see cref="LoadClip"/> read, as its file says; 30 when it does not.</summary>
    public double ClipRate { get; private set; } = 30;

    /// <summary>
    /// Puts one frame of the clip on show as though it had just arrived, so that a clip from
    /// a file can be played through whatever is switched on, and watched.
    /// </summary>
    public void ShowClipFrame(int index)
    {
        lock (_context)
        {
            if (_clip is not { } clip || _clipCount == 0 || _videoTexture is null) return;
            var frame = clip[Math.Clamp(index, 0, _clipCount - 1)];
            _context.CopyResource(_videoTexture, frame);
            if (UseProcessor && _vpTexture is not null) _context.CopyResource(_vpTexture, frame);
            _uploadSerial++;
        }
    }

    /// <summary>The estimates of motion a clip is to be run with, by name; see <see cref="ClipOptions.Flow"/>.</summary>
    private static List<(string Name, FlowQuality Quality, bool Withheld)> ClipFlows(ClipOptions? options)
    {
        var flows = new List<(string, FlowQuality, bool)>();
        foreach (string name in options?.Flow ?? [])
        {
            switch (name.Trim().ToLowerInvariant())
            {
                case "fast": flows.Add(("fast", FlowQuality.Fast, false)); break;
                case "medium": flows.Add(("medium", FlowQuality.Medium, false)); break;
                case "max": flows.Add(("max", FlowQuality.Max, false)); break;
                case "zero": flows.Add(("zero", FlowQuality.Fast, true)); break;
                case "truth" when options?.TruthFolder is not null: flows.Add(("truth", FlowQuality.Fast, false)); break;
                default: Log.Warn($"clip: no estimate of motion is called '{name}'"); break;
            }
        }
        return flows;
    }

    /// <summary>
    /// With the context held: the clip's motion by one estimate, written out frame by frame
    /// as <see cref="OpticalFlow.SavePerPixel"/> writes it - NNNN.flow is the motion from
    /// frame NNNN-1 to frame NNNN, so there is none for the first.
    /// </summary>
    private void SaveClipVectors(string dir, ID3D11Texture2D[] clip, int frames, OpticalFlow flow, int every)
    {
        Directory.CreateDirectory(dir);
        int written = 0;
        // Twice the first frame, so that what the estimate saw last is the clip's own start.
        for (int i = -1; i < frames; i++)
        {
            _context.CopyResource(_videoTexture!, clip[Math.Max(i, 0)]);
            flow.Estimate(_context, _lumaView!, _videoWidth, _videoHeight, _sampler);
            if (i < 1 || (i - 1) % Math.Max(1, every) != 0) continue;
            if (flow.SavePerPixel(_context, _sampler, Path.Combine(dir, $"{i:D4}.flow"))) written++;
        }
        Log.Info($"clip: {written} frames' vectors ({flow.Quality}, {flow.FieldSize.Width}x{flow.FieldSize.Height}) written to {dir}");
    }

    /// <summary>
    /// With the context held: what one estimate of motion costs the GPU, in milliseconds a
    /// frame, with nothing else for it to do and no pause between frames. That is the
    /// card at its full clocks, which a viewer drawing sixty light frames a second is not
    /// always given, so a frame in use can cost more than this; it is the figure that
    /// compares one estimate with another.
    /// </summary>
    private double TimeFlow(ID3D11Texture2D[] clip, int frames, OpticalFlow flow, double seconds = 3.0)
    {
        // Straight from the clip's own textures: a copy for each frame would be timed too.
        int held = Math.Min(frames, 8);
        var views = new ID3D11ShaderResourceView[held];
        for (int i = 0; i < held; i++)
            views[i] = _device.CreateShaderResourceView(clip[i],
                new ShaderResourceViewDescription(clip[i], ShaderResourceViewDimension.Texture2D, Format.R8_UNorm));
        using var done = _device.CreateQuery(new QueryDescription(QueryType.Event));

        try
        {
            double Run(int rounds)
            {
                long started = Stopwatch.GetTimestamp();
                for (int i = 0; i < rounds; i++)
                {
                    flow.Estimate(_context, views[i % held], _videoWidth, _videoHeight, _sampler);
                    flow.PerPixel(_context, _sampler);
                    // Handed over a few at a time, so that the wait at the end is for the
                    // GPU and not for a queue of commands it has not been given yet.
                    if ((i & 7) == 7) _context.Flush();
                }
                _context.End(done);
                _context.Flush();
                var spin = new SpinWait();
                while (!(_context.GetData(done, out int reached) && reached != 0)) spin.SpinOnce(sleep1Threshold: -1);
                return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }

            // A first go to bring the card up and to learn how many rounds fill the time.
            double each = Math.Max(0.01, Run(200) / 200);
            int rounds = (int)Math.Clamp(seconds * 1000 / each, 200, 100_000);
            return Run(rounds) / rounds;
        }
        finally
        {
            foreach (var v in views) v.Dispose();
        }
    }
}
