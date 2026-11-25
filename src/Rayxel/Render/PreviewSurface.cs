using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Rayxel.Render;

/// <summary>
/// A source's picture as a small live tile on the page that lists sources: the frame as it
/// came, NV12 or P010, and an RGB copy of it with its smaller sizes made, which the
/// interface draws at whatever size the tile is. Filled by <see cref="Renderer.UploadPreview"/>
/// from whichever thread reads the source, turned into RGB on the renderer's thread.
/// </summary>
public sealed class PreviewSurface
{
    // Wider than any tile is drawn, so a tile always has a smaller size to take its picture from.
    private const int MaxWidth = 1280;

    internal ID3D11Texture2D? Video;
    internal ID3D11ShaderResourceView? LumaView, ChromaView;
    internal int VideoWidth, VideoHeight;
    private bool _videoTenBit;

    private ID3D11Texture2D? _rgb;
    internal ID3D11RenderTargetView? RgbTarget;
    internal ID3D11ShaderResourceView? RgbView;
    internal int RgbWidth, RgbHeight;

    // Frames put in, and the one last turned into RGB.
    internal long Serial, ConvertedSerial;
    internal bool Hdr, Released;

    /// <summary>The picture for ImGui, or 0 until there is one.</summary>
    public nint TextureId => Ready && RgbView is { } view ? view.NativePointer : 0;

    /// <summary>True once a frame has been turned into the picture.</summary>
    public bool Ready { get; internal set; }

    /// <summary>The picture's own size, for its proportions.</summary>
    public int Width => RgbWidth;
    public int Height => RgbHeight;

    /// <summary><see cref="Environment.TickCount64"/> when the last frame came in; 0 before the first.</summary>
    public long LastFrameMs { get; internal set; }

    /// <summary>A surface the frame fits, made again when its size or depth changes.</summary>
    internal bool EnsureVideo(ID3D11Device device, int width, int height, bool tenBit)
    {
        if (width <= 0 || height <= 0) return false;
        if (Video is not null && VideoWidth == width && VideoHeight == height && _videoTenBit == tenBit) return true;

        ReleaseVideo();
        var format = tenBit ? Format.P010 : Format.NV12;
        Video = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource
        });
        LumaView = device.CreateShaderResourceView(Video,
            new ShaderResourceViewDescription(Video, ShaderResourceViewDimension.Texture2D, tenBit ? Format.R16_UNorm : Format.R8_UNorm));
        ChromaView = device.CreateShaderResourceView(Video,
            new ShaderResourceViewDescription(Video, ShaderResourceViewDimension.Texture2D, tenBit ? Format.R16G16_UNorm : Format.R8G8_UNorm));
        VideoWidth = width;
        VideoHeight = height;
        _videoTenBit = tenBit;
        return true;
    }

    /// <summary>The RGB picture for a frame of this size, at no more than <see cref="MaxWidth"/> across.</summary>
    internal bool EnsureRgb(ID3D11Device device, int width, int height)
    {
        if (width <= 0 || height <= 0) return false;
        int w = Math.Min(width, MaxWidth);
        int h = Math.Max(2, (int)Math.Round(height * (w / (double)width)));
        if (_rgb is not null && RgbWidth == w && RgbHeight == h) return true;

        ReleaseRgb();
        // Every smaller size, made afresh with each frame, so the picture drawn small is
        // an average of what is there rather than every third pixel of it.
        _rgb = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w,
            Height = (uint)h,
            MipLevels = 0,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            MiscFlags = ResourceOptionFlags.GenerateMips
        });
        // A view with no description is of the largest size, which is the one drawn into.
        RgbTarget = device.CreateRenderTargetView(_rgb);
        RgbView = device.CreateShaderResourceView(_rgb);
        RgbWidth = w;
        RgbHeight = h;
        Ready = false;
        return true;
    }

    private void ReleaseVideo()
    {
        LumaView?.Dispose(); LumaView = null;
        ChromaView?.Dispose(); ChromaView = null;
        Video?.Dispose(); Video = null;
        VideoWidth = VideoHeight = 0;
    }

    private void ReleaseRgb()
    {
        Ready = false;
        RgbView?.Dispose(); RgbView = null;
        RgbTarget?.Dispose(); RgbTarget = null;
        _rgb?.Dispose(); _rgb = null;
        RgbWidth = RgbHeight = 0;
    }

    /// <summary>Lets go of everything. Called with the renderer's context held.</summary>
    internal void Release()
    {
        Released = true;
        ReleaseVideo();
        ReleaseRgb();
    }
}
