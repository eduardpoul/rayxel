using System.Drawing;
using System.Drawing.Imaging;
using Rayxel.Diagnostics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Rayxel.Render;

/// <summary>
/// <c>--fg-probe=FILE.png</c>: puts a stretch of real frames through DLSS Frame Generation
/// and writes out the frames generated between two real ones, beside those two: FILE-a.png,
/// FILE-ab1.png (and -ab2, -ab3 with <c>--fg-probe-x=4</c>), FILE-b.png. How long
/// generating took goes in the log.
/// <para>
/// Made to check what frame generation does to a video before it was shown on screen, and
/// kept for checking it again: against <c>src/rayxel-send/test/flowtest-*</c>, where
/// everything moves by a known amount, each generated frame should be displaced by its
/// share of that amount.
/// </para>
/// </summary>
public sealed class FrameGenProbe(ID3D11Device device, string path, int factor = 2) : IDisposable
{
    /// <summary>Frames put through before any are written out: enough for what DLSS keeps to settle.</summary>
    private const int FramesBeforeSaving = 40;

    private DlssFrameGen? _generator;
    private ID3D11Texture2D? _previous;
    private ID3D11Query? _mark;
    private int _fed;
    private readonly List<double> _times = [];

    /// <summary>True once the files are written, or once it is clear they cannot be.</summary>
    public bool Done { get; private set; }

    /// <summary>One new real frame: its colour as RGBA8, and the motion back to the one before.</summary>
    public void Feed(ID3D11DeviceContext context, ID3D11Texture2D color, ID3D11Texture2D? motion, int width, int height)
    {
        if (Done || motion is null) return;

        if (_generator is null)
        {
            _generator = new DlssFrameGen(device);
            if (!_generator.Start())
            {
                Log.Warn($"fg-probe: no frame generation - {_generator.LastError}");
                Done = true;
                return;
            }
        }
        _mark ??= device.CreateQuery(new QueryDescription(QueryType.Event));
        int count = Math.Clamp(factor, 2, 4) - 1;

        // Waited for on both sides, so that what is timed is the generating alone - an
        // upper bound, since it includes handing the work over.
        WaitForGpu(context);
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!_generator.Generate(context, color, motion, width, height, count, reset: _fed == 0))
        {
            Log.Warn($"fg-probe: stopped after {_fed} frame(s) - {_generator.LastError ?? "a frame was refused"}");
            Done = true;
            return;
        }
        WaitForGpu(context);
        double took = System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        if (++_fed > 1) _times.Add(took);

        if (_fed == FramesBeforeSaving && _previous is not null)
        {
            string stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", Path.GetFileNameWithoutExtension(path));
            Save(context, _previous, stem + "-a.png");
            for (int i = 0; i < count; i++)
                if (_generator.Output(i) is { } view)
                    using (var resource = view.Resource)
                        Save(context, resource.QueryInterface<ID3D11Texture2D>(), $"{stem}-ab{i + 1}.png");
            Save(context, color, stem + "-b.png");

            var sorted = _times.OrderBy(t => t).ToArray();
            double At(double p) => sorted[(int)Math.Round(p * (sorted.Length - 1))];
            Log.Info($"fg-probe: {width}x{height}, x{count + 1}: the {count} generated frame(s) of one real frame took " +
                     $"p50 {At(0.5):0.00}  p95 {At(0.95):0.00}  max {sorted[^1]:0.00} ms (waited for, so an upper bound)");
            Log.Info($"fg-probe: written {stem}-a.png, -ab1.png{(count > 1 ? $" to -ab{count}.png" : "")} and -b.png");
            Done = true;
            return;
        }

        _previous ??= device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource
        });
        context.CopyResource(_previous, color);
    }

    private void WaitForGpu(ID3D11DeviceContext context)
    {
        context.End(_mark!);
        context.Flush();
        while (!context.GetData(_mark!, out int reached) || reached == 0) Thread.SpinWait(64);
    }

    /// <summary>Writes an RGBA8 texture out as it is.</summary>
    private unsafe void Save(ID3D11DeviceContext context, ID3D11Texture2D texture, string file)
    {
        var desc = texture.Description;
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        desc.MiscFlags = ResourceOptionFlags.None;
        using var staging = device.CreateTexture2D(desc);
        context.CopyResource(staging, texture);
        var map = context.Map(staging, 0, MapMode.Read);
        try
        {
            int width = (int)desc.Width, height = (int)desc.Height;
            using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var dst = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < height; y++)
            {
                byte* src = (byte*)map.DataPointer + y * (long)map.RowPitch;
                byte* row = (byte*)dst.Scan0 + y * (long)dst.Stride;
                for (int x = 0; x < width; x++)
                {
                    row[x * 4 + 0] = src[x * 4 + 2];
                    row[x * 4 + 1] = src[x * 4 + 1];
                    row[x * 4 + 2] = src[x * 4 + 0];
                    row[x * 4 + 3] = 255;
                }
            }
            bmp.UnlockBits(dst);
            bmp.Save(file, ImageFormat.Png);
        }
        finally { context.Unmap(staging, 0); }
    }

    public void Dispose()
    {
        _generator?.Dispose();
        _previous?.Dispose();
        _mark?.Dispose();
    }
}
