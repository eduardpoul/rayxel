using Rayxel.Diagnostics;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Rayxel.Render;

/// <summary>What of the estimated motion is drawn over the picture.</summary>
public enum FlowView
{
    /// <summary>Nothing is estimated and nothing is drawn.</summary>
    Off,

    /// <summary>The vectors drawn over the video.</summary>
    Overlay,

    /// <summary>The vectors alone, on black.</summary>
    Only
}

/// <summary>How much work goes into the estimate of motion; see <see cref="OpticalFlow"/>.</summary>
public enum FlowQuality
{
    /// <summary>One vector for every 4x4 block of the picture, worked out on reduced copies of it.</summary>
    Fast,

    /// <summary>One for every 2x2 block, the last correction made on the picture itself.</summary>
    Medium,

    /// <summary>One for every pixel.</summary>
    Max
}

/// <summary>
/// Motion between one frame and the next, estimated from the pictures themselves.
/// <para>
/// A game knows how everything in a frame moved, because it moved it. A video carries no
/// such thing, and everything that would add frames or rebuild detail across them needs
/// it, so it is recovered here by the only means available: finding, for each small patch
/// of the new frame, where the patch that looks most like it was in the previous one. That
/// offset is the motion vector.
/// </para>
/// <para>
/// It is done coarse to fine. A patch can match in many places, and searching a wide area
/// for the best of them is both slow and how wrong answers get chosen, so both frames are
/// first reduced several times over. In the smallest copy a long movement is a pixel or
/// two and is found by trying the neighbours; each larger copy then starts from that answer
/// doubled and only corrects it by a pixel or two more. Each level also measures the
/// fraction of a pixel left over, from how brightness slopes across the patch, because in
/// the small copies most movement is less than a pixel and whole-pixel matching would
/// call it none; the last such correction is made against a copy twice as detailed as the
/// vectors. The result is one vector for every 4x4 block of the picture.
/// </para>
/// <para>
/// This is a guess, and where it has nothing to go on it says so rather than invent. Each
/// level also asks whether the patch moved at all, because a still thing seen together
/// with what moves around it - a game's interface over its world - would otherwise be
/// handed the movement. And each vector ends up marked with how surely its patch moved
/// and which halves of it, sideways and up-and-down, were measured. The halves that were
/// not - a flat patch, an edge sliding along itself, a patch with two things in it - are
/// settled from the measured ones around them, and a stray among its neighbours is
/// removed.
/// </para>
/// <para>
/// It still guesses wrong in known places: where something came out from behind something
/// else and has nothing to match, on repeating patterns, on a flat thing cut off by the
/// edge of the picture, and by a fraction of a pixel wherever the movement is not a whole
/// number of the quarter-size copy's pixels. <c>--shot</c> writes the vectors out as
/// numbers beside its picture, and <c>src/rayxel-send/test/flowtest-*</c> is a picture
/// with known movement to check them against.
/// </para>
/// <para>
/// That is the fast estimate. <see cref="FlowQuality.Medium"/> does the same on copies
/// twice the size - one vector for every 2x2 block, the last corrections made on the
/// picture itself - offers each patch what its neighbours a level up found as well as
/// their blend, and lets a flat patch with a knowing neighbour on one side only go with
/// it. <see cref="FlowQuality.Max"/> then has every pixel choose for itself among the
/// vectors of the blocks around it. <c>tools/flow</c> renders clips whose motion is known
/// exactly and measures all three against them, and what an upscaler makes of each.
/// </para>
/// </summary>
public sealed class OpticalFlow : IDisposable
{
    // The reduced copy the finest vectors are computed on is this mip of the pyramid, whose
    // level 0 is already half the picture: so a quarter of it each way.
    private const int FinestMip = 1;

    // Stop reducing when a copy would get smaller than this on its short side.
    private const int SmallestSide = 12;

    private readonly ID3D11Device _device;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _reducePs, _matchPs, _refinePs, _finerPs, _resolvePs, _medianPs, _showPs, _spreadPs;
    private ID3D11PixelShader? _densePs, _denseMedianPs, _polishPs;
    private ID3D11Buffer? _constants;
    private ID3D11SamplerState? _point;
    private bool _compiled, _failed;
    private FlowQuality _compiledFor;

    // Two luma pyramids, swapped every frame: the newest and the one before it.
    private readonly ID3D11Texture2D?[] _pyramid = new ID3D11Texture2D?[2];
    private readonly ID3D11RenderTargetView?[] _pyramidTop = new ID3D11RenderTargetView?[2];
    private readonly ID3D11ShaderResourceView?[] _pyramidView = new ID3D11ShaderResourceView?[2];
    private int _current;
    private bool _havePrevious;

    // One vector field per pyramid level searched, coarsest last; the finest refined, and
    // refined again on a finer copy of the picture; that with every vector settled; and that
    // with the strays removed, which is the result.
    private ID3D11Texture2D?[] _field = [];
    private ID3D11RenderTargetView?[] _fieldTarget = [];
    private ID3D11ShaderResourceView?[] _fieldView = [];
    private (int Width, int Height)[] _levelSize = [];
    private ID3D11Texture2D? _refined, _finer, _settled, _result;
    private ID3D11RenderTargetView? _refinedTarget, _finerTarget, _settledTarget, _resultTarget;
    private ID3D11ShaderResourceView? _refinedView, _finerView, _settledView, _resultView;

    // The result again, one vector per pixel of the picture: see PerPixel.
    private ID3D11Texture2D? _perPixel;
    private ID3D11RenderTargetView? _perPixelTarget;

    // At the highest quality every pixel chooses its own vector, here, before the strays
    // among them are removed into _perPixel.
    private ID3D11Texture2D? _dense;
    private ID3D11RenderTargetView? _denseTarget;
    private ID3D11ShaderResourceView? _denseView;

    private int _videoWidth, _videoHeight;
    private int _topWidth, _topHeight;
    private FlowQuality _quality = FlowQuality.Fast;

    public FlowView View { get; set; } = FlowView.Off;

    /// <summary>
    /// How much work goes into it. Changing it starts the estimate afresh: the next frame
    /// has nothing before it.
    /// </summary>
    public FlowQuality Quality
    {
        get => _quality;
        set
        {
            if (value == _quality) return;
            _quality = value;
            // Everything is sized by it, so it is all built again at the next frame.
            _videoWidth = _videoHeight = 0;
            Ready = false;
            _failed = false;
        }
    }

    /// <summary>
    /// Diagnostic: the vectors are worked out as ever and "nothing moved" handed out in
    /// their place by <see cref="PerPixel"/> - what an upscaler makes of a picture with no
    /// motion given, to set beside what it makes of it with.
    /// </summary>
    public bool Withheld { get; set; }

    // Vectors read from a file, handed out by PerPixel in place of the estimate; see Supply.
    private ID3D11Texture2D? _supplied;
    private bool _suppliedNow;

    /// <summary>
    /// Diagnostic: vectors from a file, as <see cref="SavePerPixel"/> writes them, handed
    /// out by <see cref="PerPixel"/> in place of the estimate until the next call - or, with
    /// null, the estimate again. With a clip whose motion is known this is the truth itself,
    /// which shows what an upscaler makes of a picture when its vectors could not be better:
    /// the most there is to be had from improving the estimate.
    /// </summary>
    public bool Supply(ID3D11DeviceContext context, string? path)
    {
        _suppliedNow = false;
        if (path is null || _videoWidth <= 0) return false;

        using var file = new BinaryReader(File.OpenRead(path));
        if (!file.ReadBytes(4).AsSpan().SequenceEqual("RXFP"u8)) return false;
        int width = file.ReadInt32(), height = file.ReadInt32();
        if (width != _videoWidth || height != _videoHeight) return false;
        byte[] vectors = file.ReadBytes(width * height * 4);
        if (vectors.Length != width * height * 4) return false;

        if (_supplied is null || _supplied.Description.Width != width || _supplied.Description.Height != height)
        {
            _supplied?.Dispose();
            _supplied = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R16G16_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource
            });
        }
        unsafe
        {
            fixed (byte* data = vectors)
                context.UpdateSubresource(_supplied, 0, null, (IntPtr)data, (uint)(width * 4), 0);
        }
        _suppliedNow = true;
        return true;
    }

    // The pyramid's largest copy is the picture halved this many times.
    private int TopShift => _quality == FlowQuality.Fast ? 1 : 0;

    /// <summary>True once there is a vector field to show: two frames have been seen.</summary>
    public bool Ready { get; private set; }

    /// <summary>Why it is not running, when it is not.</summary>
    public string? LastError { get; private set; }

    /// <summary>Vectors per frame, as columns by rows.</summary>
    public (int Width, int Height) FieldSize =>
        _levelSize.Length == 0 ? (0, 0) : _quality == FlowQuality.Max ? (_videoWidth, _videoHeight) : _levelSize[0];

    public OpticalFlow(ID3D11Device device) => _device = device;

    private bool Compile()
    {
        if (_compiled && _compiledFor == _quality) return true;
        if (_failed) return false;

        try
        {
            // The passes differ with the quality, so they are built for it, and again when it changes.
            ReleaseShaders();
            _compiled = false;
            _vs = _device.CreateVertexShader(Build("VS", "vs_5_0"));
            _reducePs = _device.CreatePixelShader(Build("ReducePS", "ps_5_0"));
            _matchPs = _device.CreatePixelShader(Build("MatchPS", "ps_5_0"));
            _refinePs = _device.CreatePixelShader(Build("RefinePS", "ps_5_0"));
            _finerPs = _device.CreatePixelShader(Build("FinerPS", "ps_5_0"));
            _resolvePs = _device.CreatePixelShader(Build("ResolvePS", "ps_5_0"));
            _medianPs = _device.CreatePixelShader(Build("MedianPS", "ps_5_0"));
            _showPs = _device.CreatePixelShader(Build("ShowPS", "ps_5_0"));
            _spreadPs = _device.CreatePixelShader(Build("SpreadPS", "ps_5_0"));
            if (_quality != FlowQuality.Fast) _polishPs = _device.CreatePixelShader(Build("PolishPS", "ps_5_0"));
            if (_quality == FlowQuality.Max)
            {
                _densePs = _device.CreatePixelShader(Build("DensePS", "ps_5_0"));
                _denseMedianPs = _device.CreatePixelShader(Build("DenseMedianPS", "ps_5_0"));
            }

            _constants ??= _device.CreateBuffer(new BufferDescription(80, BindFlags.ConstantBuffer,
                ResourceUsage.Dynamic, CpuAccessFlags.Write));
            _point ??= _device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipPoint,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                MaxLOD = float.MaxValue
            });

            _compiled = true;
            _compiledFor = _quality;
            Log.Info($"optical flow: shaders ready ({_quality})");
            return true;
        }
        catch (Exception ex)
        {
            _failed = true;
            LastError = ex.Message;
            Log.Warn($"optical flow unavailable: {ex.Message}");
            return false;
        }

        ReadOnlySpan<byte> Build(string entry, string profile)
        {
            Compiler.Compile(ShaderSource, [new ShaderMacro("QUALITY", ((int)_quality).ToString())], null, entry, "flow.hlsl",
                             profile, out var blob, out var errors);
            if (blob is null) throw new InvalidOperationException($"{entry} did not compile: {errors?.AsString()}");
            // Copied out because the blob is released here and the span must outlive it.
            byte[] code = blob.AsSpan().ToArray();
            blob.Dispose();
            errors?.Dispose();
            return code;
        }
    }

    private void Allocate(int videoWidth, int videoHeight)
    {
        Release();
        _videoWidth = videoWidth;
        _videoHeight = videoHeight;

        int topWidth = Math.Max(1, videoWidth >> TopShift), topHeight = Math.Max(1, videoHeight >> TopShift);
        _topWidth = topWidth;
        _topHeight = topHeight;

        for (int i = 0; i < 2; i++)
        {
            _pyramid[i] = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)topWidth,
                Height = (uint)topHeight,
                MipLevels = 0, // the whole chain
                ArraySize = 1,
                Format = Format.R8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                MiscFlags = ResourceOptionFlags.GenerateMips
            });
            _pyramidTop[i] = _device.CreateRenderTargetView(_pyramid[i]!,
                new RenderTargetViewDescription(_pyramid[i]!, RenderTargetViewDimension.Texture2D, Format.R8_UNorm, 0));
            _pyramidView[i] = _device.CreateShaderResourceView(_pyramid[i]!);
        }

        // The levels searched: from FinestMip down to the last one still worth looking at.
        var sizes = new List<(int, int)>();
        for (int mip = FinestMip; ; mip++)
        {
            int w = Math.Max(1, topWidth >> mip), h = Math.Max(1, topHeight >> mip);
            if (sizes.Count > 0 && Math.Min(w, h) < SmallestSide) break;
            sizes.Add((w, h));
        }
        _levelSize = [.. sizes];

        _field = new ID3D11Texture2D?[_levelSize.Length];
        _fieldTarget = new ID3D11RenderTargetView?[_levelSize.Length];
        _fieldView = new ID3D11ShaderResourceView?[_levelSize.Length];
        for (int i = 0; i < _levelSize.Length; i++)
            (_field[i], _fieldTarget[i], _fieldView[i]) = Field(_levelSize[i].Width, _levelSize[i].Height);
        (_refined, _refinedTarget, _refinedView) = Field(_levelSize[0].Width, _levelSize[0].Height);
        (_finer, _finerTarget, _finerView) = Field(_levelSize[0].Width, _levelSize[0].Height);
        (_settled, _settledTarget, _settledView) = Field(_levelSize[0].Width, _levelSize[0].Height);
        (_result, _resultTarget, _resultView) = Field(_levelSize[0].Width, _levelSize[0].Height);

        _perPixel = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)videoWidth,
            Height = (uint)videoHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
        });
        _perPixelTarget = _device.CreateRenderTargetView(_perPixel);

        if (_quality == FlowQuality.Max)
        {
            _dense = _device.CreateTexture2D(_perPixel.Description);
            _denseTarget = _device.CreateRenderTargetView(_dense);
            _denseView = _device.CreateShaderResourceView(_dense);
        }

        _havePrevious = false;
        Ready = false;

        int reach = 0;
        for (int i = 0; i < _levelSize.Length; i++) reach += SearchRadius << (FinestMip + TopShift + i);
        Log.Info($"optical flow ({_quality}): {_levelSize[0].Width}x{_levelSize[0].Height} vectors for {videoWidth}x{videoHeight}, " +
                 $"{_levelSize.Length} levels, movement of up to about {reach} px a frame");
    }

    // How far each level looks either side of where the level above said to look.
    private const int SearchRadius = 2;

    private (ID3D11Texture2D, ID3D11RenderTargetView, ID3D11ShaderResourceView) Field(int width, int height)
    {
        // xy is the vector in this level's own pixels, z how surely the patch moved at all
        // (+1) or stood still (-1), w how much there was to match by - or, in the finest
        // two, which halves of the vector are measurements. The shader says more.
        var texture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
        });
        return (texture, _device.CreateRenderTargetView(texture), _device.CreateShaderResourceView(texture));
    }

    /// <summary>
    /// Takes in a new frame and estimates how the picture moved since the one before.
    /// Leaves the pipeline's render target and shaders changed; the caller sets its own
    /// again, which the renderer does at the start of every frame anyway.
    /// </summary>
    public void Estimate(ID3D11DeviceContext context, ID3D11ShaderResourceView luma, int videoWidth, int videoHeight,
                         ID3D11SamplerState linear)
    {
        if (videoWidth < 64 || videoHeight < 64 || !Compile()) return;
        if (videoWidth != _videoWidth || videoHeight != _videoHeight) Allocate(videoWidth, videoHeight);

        _current ^= 1;
        int previous = _current ^ 1;

        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        context.VSSetShader(_vs);
        context.VSSetConstantBuffer(0, _constants);
        context.PSSetConstantBuffer(0, _constants);
        context.PSSetSampler(0, linear);
        context.PSSetSampler(1, _point);
        context.OMSetBlendState(null);

        // The new frame's brightness, at half size or its own, and from that every smaller copy.
        Unbind(context);
        context.OMSetRenderTargets(_pyramidTop[_current]!);
        context.RSSetViewport(0, 0, _topWidth, _topHeight);
        SetConstants(context, 0, 0, 0, hasParent: false);
        context.PSSetShader(_reducePs);
        context.PSSetShaderResource(0, luma);
        context.Draw(4, 0);
        context.OMSetRenderTargets((ID3D11RenderTargetView?)null);
        context.GenerateMips(_pyramidView[_current]!);

        if (!_havePrevious)
        {
            // Nothing to compare the first frame with.
            _havePrevious = true;
            return;
        }

        // Coarsest level first; each finer one starts from the answer of the one before.
        context.PSSetShader(_matchPs);
        for (int i = _levelSize.Length - 1; i >= 0; i--)
        {
            bool hasParent = i < _levelSize.Length - 1;
            Unbind(context);
            context.OMSetRenderTargets(_fieldTarget[i]!);
            context.RSSetViewport(0, 0, _levelSize[i].Width, _levelSize[i].Height);
            // Above the fast estimate the finest level, where most of the vectors are, looks
            // one pixel either side and no further: it is also offered what its neighbours
            // above found, which is where an answer far from its own would come from.
            int radius = _quality != FlowQuality.Fast && i == 0 && hasParent ? 1 : SearchRadius;
            var above = hasParent ? _levelSize[i + 1] : (0, 0);
            SetConstants(context, _levelSize[i].Width, _levelSize[i].Height, FinestMip + i, hasParent, above, radius);
            context.PSSetShaderResource(0, _pyramidView[_current]!);
            context.PSSetShaderResource(1, _pyramidView[previous]!);
            if (hasParent) context.PSSetShaderResource(2, _fieldView[i + 1]!);
            context.Draw(4, 0);
        }

        // One more round of the sub-pixel correction on the finest level.
        Unbind(context);
        context.OMSetRenderTargets(_refinedTarget!);
        context.RSSetViewport(0, 0, _levelSize[0].Width, _levelSize[0].Height);
        SetConstants(context, _levelSize[0].Width, _levelSize[0].Height, FinestMip, hasParent: true);
        context.PSSetShader(_refinePs);
        context.PSSetShaderResource(0, _pyramidView[_current]!);
        context.PSSetShaderResource(1, _pyramidView[previous]!);
        context.PSSetShaderResource(2, _fieldView[0]!);
        context.Draw(4, 0);

        // And once more against the copy of the picture one step finer than the vectors.
        int finerMip = FinestMip - 1;
        var refined = _refinedView!;
        if (_polishPs is not null)
        {
            // Above the fast estimate that copy is the picture itself, and the correction
            // is made on it twice: first over a wider patch, which steadies it. The field
            // it is written into is free until the vectors are settled.
            Unbind(context);
            context.OMSetRenderTargets(_settledTarget!);
            SetConstants(context, _topWidth >> finerMip, _topHeight >> finerMip, finerMip, hasParent: true);
            context.PSSetShader(_polishPs);
            context.PSSetShaderResource(0, _pyramidView[_current]!);
            context.PSSetShaderResource(1, _pyramidView[previous]!);
            context.PSSetShaderResource(2, _refinedView!);
            context.Draw(4, 0);
            refined = _settledView!;
        }
        Unbind(context);
        context.OMSetRenderTargets(_finerTarget!);
        SetConstants(context, _topWidth >> finerMip, _topHeight >> finerMip, finerMip, hasParent: true);
        context.PSSetShader(_finerPs);
        context.PSSetShaderResource(0, _pyramidView[_current]!);
        context.PSSetShaderResource(1, _pyramidView[previous]!);
        context.PSSetShaderResource(2, refined);
        context.Draw(4, 0);

        // The vectors that are not measurements settled from those that are.
        Unbind(context);
        context.OMSetRenderTargets(_settledTarget!);
        SetConstants(context, _levelSize[0].Width, _levelSize[0].Height, FinestMip, hasParent: true);
        context.PSSetShader(_resolvePs);
        context.PSSetShaderResource(0, _pyramidView[_current]!);
        context.PSSetShaderResource(2, _finerView!);
        context.Draw(4, 0);

        // And the strays among them removed.
        Unbind(context);
        context.OMSetRenderTargets(_resultTarget!);
        context.PSSetShader(_medianPs);
        context.PSSetShaderResource(2, _settledView!);
        context.Draw(4, 0);

        if (_quality == FlowQuality.Max && _denseTarget is not null)
        {
            // Every pixel of the picture then chooses for itself among the vectors of the
            // blocks around it, by which of them its own surroundings were found at.
            Unbind(context);
            context.OMSetRenderTargets(_denseTarget);
            context.RSSetViewport(0, 0, _videoWidth, _videoHeight);
            SetConstants(context, _topWidth, _topHeight, 0, hasParent: true, _levelSize[0], 0);
            context.PSSetShader(_densePs);
            context.PSSetShaderResource(0, _pyramidView[_current]!);
            context.PSSetShaderResource(1, _pyramidView[previous]!);
            context.PSSetShaderResource(2, _resultView!);
            context.Draw(4, 0);

            Unbind(context);
            context.OMSetRenderTargets(_perPixelTarget!);
            context.PSSetShader(_denseMedianPs);
            context.PSSetShaderResource(2, _denseView!);
            context.Draw(4, 0);
        }

        Unbind(context);
        context.OMSetRenderTargets((ID3D11RenderTargetView?)null);
        Ready = true;
    }

    /// <summary>
    /// Draws the vectors into the rectangle the video occupies: a tint whose colour is the
    /// direction and whose strength is the speed, and a needle every few pixels pointing
    /// where that part of the picture is going.
    /// </summary>
    /// <param name="x">Left edge of the video rectangle in NDC, and the rest likewise.</param>
    /// <param name="pixelWidth">Size of that rectangle in window pixels.</param>
    public void Draw(ID3D11DeviceContext context, float x, float y, float w, float h, int pixelWidth, int pixelHeight,
                     bool pq, float nits, ID3D11SamplerState linear, ID3D11BlendState alphaBlend)
    {
        if (!Ready || View == FlowView.Off || _resultView is null) return;

        var mapped = context.Map(_constants!, 0, MapMode.WriteDiscard);
        unsafe
        {
            var p = (float*)mapped.DataPointer;
            p[0] = x; p[1] = y; p[2] = w; p[3] = h;
            p[4] = _levelSize[0].Width; p[5] = _levelSize[0].Height;
            p[6] = 0; p[7] = 0;
            p[8] = pixelWidth; p[9] = pixelHeight;
            // One vector unit is one pixel of the quarter-size picture; this many window pixels.
            p[10] = pixelWidth / (float)_levelSize[0].Width;
            p[11] = View == FlowView.Only ? 1f : 0f;
            p[12] = pq ? 1f : 0f;
            p[13] = nits;
            for (int i = 14; i < 20; i++) p[i] = 0;
        }
        context.Unmap(_constants!, 0);

        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        context.VSSetShader(_vs);
        context.PSSetShader(_showPs);
        context.VSSetConstantBuffer(0, _constants);
        context.PSSetConstantBuffer(0, _constants);
        context.PSSetSampler(0, linear);
        context.PSSetSampler(1, _point);
        context.PSSetShaderResource(2, _resultView);
        context.OMSetBlendState(alphaBlend);
        context.Draw(4, 0);
        context.PSSetShaderResource(2, null!);
    }

    /// <summary>
    /// The vectors in the form a temporal upscaler takes them: one for every pixel of the
    /// picture, in the picture's own pixels, each saying where that pixel was in the frame
    /// before relative to where it is now. Null until there is a field.
    /// <para>
    /// There is still only one measurement per 4x4 block. Between blocks the vectors are
    /// blended, which is right where movement changes gradually - a floor running off into
    /// the distance - and wrong for a couple of pixels either side of the edge of something
    /// moving over something else, where the true answer jumps.
    /// </para>
    /// <para>
    /// Leaves the pipeline's render target and shaders changed, as <see cref="Estimate"/> does.
    /// </para>
    /// </summary>
    public ID3D11Texture2D? PerPixel(ID3D11DeviceContext context, ID3D11SamplerState linear)
    {
        if (!Ready || _resultView is null || _perPixelTarget is null) return null;
        if (_suppliedNow && _supplied is not null) return _supplied;
        // At the highest quality it is already there: Estimate left it.
        if (_quality == FlowQuality.Max && _dense is not null)
        {
            if (Withheld) context.ClearRenderTargetView(_perPixelTarget, new Vortice.Mathematics.Color4(0f, 0f, 0f, 0f));
            return _perPixel;
        }

        var mapped = context.Map(_constants!, 0, MapMode.WriteDiscard);
        unsafe
        {
            var p = (float*)mapped.DataPointer;
            p[0] = -1f; p[1] = 1f; p[2] = 2f; p[3] = -2f;
            for (int i = 4; i < 20; i++) p[i] = 0;
            // One vector unit is one pixel of the quarter-size picture; this many of the picture's own.
            p[10] = _videoWidth / (float)_levelSize[0].Width;
        }
        context.Unmap(_constants!, 0);

        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        context.VSSetShader(_vs);
        context.PSSetShader(_spreadPs);
        context.VSSetConstantBuffer(0, _constants);
        context.PSSetConstantBuffer(0, _constants);
        context.PSSetSampler(0, linear);
        context.PSSetSampler(1, _point);
        context.OMSetBlendState(null);
        Unbind(context);
        context.OMSetRenderTargets(_perPixelTarget);
        context.RSSetViewport(0, 0, _videoWidth, _videoHeight);
        context.PSSetShaderResource(2, _resultView);
        context.Draw(4, 0);
        Unbind(context);
        context.OMSetRenderTargets((ID3D11RenderTargetView?)null);
        if (Withheld) context.ClearRenderTargetView(_perPixelTarget, new Vortice.Mathematics.Color4(0f, 0f, 0f, 0f));
        return _perPixel;
    }

    /// <summary>
    /// Writes out what <see cref="PerPixel"/> gives - the vectors as an upscaler is handed
    /// them - so that every quality can be checked against a picture with known motion on
    /// the same terms, whatever its own grid. Diagnostic only: it waits for the GPU.
    /// <para>
    /// The file is "RXFP", then as int32 the picture's width and height, then two float16
    /// per pixel, row by row: x and y in picture pixels, where that pixel was in the frame
    /// before relative to where it is now.
    /// </para>
    /// </summary>
    public bool SavePerPixel(ID3D11DeviceContext context, ID3D11SamplerState linear, string path)
    {
        if (PerPixel(context, linear) is not { } vectors) return false;

        using var staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_videoWidth,
            Height = (uint)_videoHeight,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read
        });
        context.CopyResource(staging, vectors);

        var map = context.Map(staging, 0, MapMode.Read);
        try
        {
            using var file = new BinaryWriter(File.Create(path));
            file.Write("RXFP"u8);
            file.Write(_videoWidth); file.Write(_videoHeight);
            unsafe
            {
                for (int y = 0; y < _videoHeight; y++)
                    file.Write(new ReadOnlySpan<byte>((byte*)map.DataPointer + y * (long)map.RowPitch, _videoWidth * 4));
            }
        }
        finally { context.Unmap(staging, 0); }
        return true;
    }

    /// <summary>
    /// Writes the vectors out as numbers, so that they can be checked against a picture whose
    /// motion is known - which the coloured view cannot be, once the display's own tone curve
    /// has been through it. Diagnostic only: it waits for the GPU.
    /// <para>
    /// The file is "RXFL", then as int32 the field's width and height and the picture's, then
    /// four float32 per vector, row by row: x and y in picture pixels (where that patch was
    /// in the frame before, relative to where it is now), how surely it moved (+1) or stood
    /// still (-1), and which halves of the vector were measured rather than settled from
    /// its neighbours (1 sideways, 2 up-and-down, 3 both).
    /// </para>
    /// </summary>
    public bool Save(ID3D11DeviceContext context, string path)
    {
        if (!Ready || _result is null) return false;

        var (width, height) = _levelSize[0];
        using var staging = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read
        });
        context.CopyResource(staging, _result);

        // A vector is in pixels of its own level; the picture's are this many times smaller.
        float toPicture = _videoWidth / (float)width;
        var map = context.Map(staging, 0, MapMode.Read);
        try
        {
            using var file = new BinaryWriter(File.Create(path));
            file.Write("RXFL"u8);
            file.Write(width); file.Write(height); file.Write(_videoWidth); file.Write(_videoHeight);
            unsafe
            {
                for (int y = 0; y < height; y++)
                {
                    var row = (Half*)((byte*)map.DataPointer + y * (long)map.RowPitch);
                    for (int x = 0; x < width; x++)
                    {
                        file.Write((float)row[x * 4 + 0] * toPicture);
                        file.Write((float)row[x * 4 + 1] * toPicture);
                        file.Write((float)row[x * 4 + 2]);
                        file.Write((float)row[x * 4 + 3]);
                    }
                }
            }
        }
        finally { context.Unmap(staging, 0); }
        return true;
    }

    private void SetConstants(ID3D11DeviceContext context, int width, int height, int mip, bool hasParent,
                              (int Width, int Height) above = default, int radius = SearchRadius)
    {
        var mapped = context.Map(_constants!, 0, MapMode.WriteDiscard);
        unsafe
        {
            var p = (float*)mapped.DataPointer;
            // A full-target quad: top-left corner and size in NDC.
            p[0] = -1f; p[1] = 1f; p[2] = 2f; p[3] = -2f;
            p[4] = width; p[5] = height;
            p[6] = mip;
            p[7] = hasParent ? 1f : 0f;
            for (int i = 8; i < 20; i++) p[i] = 0;
            p[16] = above.Width; p[17] = above.Height;
            p[18] = radius;
        }
        context.Unmap(_constants!, 0);
    }

    /// <summary>
    /// Clears the texture slots these passes use. A field written by one pass is read by
    /// the next, and Direct3D drops a binding rather than allow a texture to be read and
    /// written at once, so nothing may be left bound from the pass before.
    /// </summary>
    private static void Unbind(ID3D11DeviceContext context)
    {
        context.PSSetShaderResource(0, null!);
        context.PSSetShaderResource(1, null!);
        context.PSSetShaderResource(2, null!);
    }

    private void Release()
    {
        for (int i = 0; i < 2; i++)
        {
            _pyramidView[i]?.Dispose(); _pyramidView[i] = null;
            _pyramidTop[i]?.Dispose(); _pyramidTop[i] = null;
            _pyramid[i]?.Dispose(); _pyramid[i] = null;
        }
        for (int i = 0; i < _field.Length; i++)
        {
            _fieldView[i]?.Dispose();
            _fieldTarget[i]?.Dispose();
            _field[i]?.Dispose();
        }
        _field = []; _fieldTarget = []; _fieldView = []; _levelSize = [];
        _finerView?.Dispose(); _finerView = null;
        _finerTarget?.Dispose(); _finerTarget = null;
        _finer?.Dispose(); _finer = null;
        _settledView?.Dispose(); _settledView = null;
        _settledTarget?.Dispose(); _settledTarget = null;
        _settled?.Dispose(); _settled = null;
        _refinedView?.Dispose(); _refinedView = null;
        _refinedTarget?.Dispose(); _refinedTarget = null;
        _refined?.Dispose(); _refined = null;
        _resultView?.Dispose(); _resultView = null;
        _resultTarget?.Dispose(); _resultTarget = null;
        _result?.Dispose(); _result = null;
        _perPixelTarget?.Dispose(); _perPixelTarget = null;
        _perPixel?.Dispose(); _perPixel = null;
        _denseView?.Dispose(); _denseView = null;
        _denseTarget?.Dispose(); _denseTarget = null;
        _dense?.Dispose(); _dense = null;
        _videoWidth = _videoHeight = 0;
        _topWidth = _topHeight = 0;
        Ready = false;
    }

    private void ReleaseShaders()
    {
        _spreadPs?.Dispose(); _showPs?.Dispose(); _medianPs?.Dispose(); _resolvePs?.Dispose(); _finerPs?.Dispose();
        _refinePs?.Dispose(); _matchPs?.Dispose(); _reducePs?.Dispose(); _vs?.Dispose();
        _spreadPs = _showPs = _medianPs = _resolvePs = _finerPs = _refinePs = _matchPs = _reducePs = null;
        _denseMedianPs?.Dispose(); _densePs?.Dispose(); _polishPs?.Dispose();
        _denseMedianPs = _densePs = _polishPs = null;
        _vs = null;
    }

    public void Dispose()
    {
        Release();
        _point?.Dispose(); _constants?.Dispose();
        _supplied?.Dispose();
        ReleaseShaders();
    }

    // NOTE: keep this shader pure ASCII. Non-ASCII bytes truncate the source.
    private const string ShaderSource = """
        cbuffer Params : register(b0)
        {
            float4 Rect;       // xy = top-left in NDC, zw = size in NDC
            float2 LevelSize;  // pixels of the pyramid level being worked on
            float  Mip;        // which mip of the pyramid that level is
            float  HasParent;  // 1 when a coarser field gives a starting point
            float2 OutSize;    // show: the video rectangle in window pixels
            float  UnitToOut;  // show: window pixels per vector unit; spread: the picture's
            float  Alone;      // show: 1 = vectors on black, 0 = over the video
            float  IsPq;       // show: 1 when the swapchain is PQ
            float  Nits;       // show: level to encode at for PQ
            float2 _pad;
            float2 AboveSize;  // match: the field one level up; dense: the field being spread
            float  Radius;     // match: how far either side of its start a level looks
            float  _pad2;
        };

        Texture2D<float>  Current  : register(t0);
        Texture2D<float>  Previous : register(t1);
        Texture2D<float4> Field    : register(t2);
        SamplerState Linear : register(s0);
        SamplerState Point  : register(s1);

        struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

        VSOut VS(uint vid : SV_VertexID)
        {
            float2 t = float2(vid & 1, (vid >> 1) & 1);
            VSOut o;
            o.pos = float4(Rect.xy + t * Rect.zw, 0.0, 1.0);
            o.uv  = t;
            return o;
        }

        // Half-size brightness. At exactly half size one linear tap between four source
        // pixels is their average, which is all the reduction needs.
        float ReducePS(VSOut i) : SV_Target
        {
            return Current.SampleLevel(Linear, i.uv, 0);
        }

        // An offset that is not a whole number of pixels has the previous frame read
        // between its pixels, which softens it, and a softened patch does not match a
        // sharp one even where it belongs. So the fraction is shared out: each frame is
        // read half of it off its pixels, in opposite directions, and both are softened
        // alike. This is that half - what the new frame's patch is read back by.
        float2 Shared(float2 d)
        {
            return 0.5 * (d - round(d));
        }

        // How unlike two 5x5 patches are: the sum of absolute differences, of the patch
        // around `at` in the new frame and the one displaced by `d` in the previous frame.
        float PatchCost(float2 at, float2 d)
        {
            float2 back = Shared(d);
            float cost = 0.0;
            for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
            {
                float2 o = float2(x, y) - back;
                float a = Current.SampleLevel(Linear, (at + o) / LevelSize, Mip);
                float b = Previous.SampleLevel(Linear, (at + o + d) / LevelSize, Mip);
                cost += abs(a - b);
            }
            return cost;
        }

        // A candidate that is no better than the starting point should not win over it:
        // on a flat area every position matches equally and only noise would decide. So
        // moving away from the prediction costs a little.
        static const float Reluctance = 0.012;

        // What two matches of the same patch can differ by through noise alone, per pixel:
        // about one grey level of an 8-bit picture.
        static const float Noise = 0.004;

        // How surely a patch moved, from how well it matches at its offset and how well
        // where it was: towards +1 when the offset explains it and staying put does not,
        // towards -1 the other way round, and near zero when the two match about equally.
        // That last is a flat patch, an edge sliding along itself - or a patch with two
        // things in it going different ways, which neither answer explains.
        float Surely(float moved, float still)
        {
            return (still - moved) / (still + moved + 2.0 * 25.0 * Noise);
        }

        // Beyond this, either way, the question counts as settled.
        static const float Settled = 0.45;

        // Matching whole pixels cannot see a movement smaller than one, and in the small
        // copies of the picture nearly every movement is. This finds the remainder from
        // the slopes instead (Lucas-Kanade): where brightness changes by so much per
        // pixel, a brightness difference of so much between the frames means the patch
        // sits that fraction of a pixel away. It is solved over the whole patch for both
        // directions at once. On a flat patch there are no slopes and the answer is "no
        // correction"; along a plain edge only the movement across the edge can be told,
        // and the damping term makes it return just that rather than something wild.
        // `grip` is how much brightness changes across the patch sideways and up and down:
        // what there was to hold on to in each direction. A level edge has none sideways,
        // and no sideways offset of it can be told from another.
        float2 Slopes(float2 at, float2 d, out float2 grip)
        {
            float2 back = Shared(d);
            float gxx = 0.0, gxy = 0.0, gyy = 0.0, bx = 0.0, by = 0.0;
            for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
            {
                float2 q = at + float2(x, y) - back;
                float c  = Current.SampleLevel(Linear, q / LevelSize, Mip);
                float ix = 0.5 * (Current.SampleLevel(Linear, (q + float2(1.0, 0.0)) / LevelSize, Mip)
                                - Current.SampleLevel(Linear, (q - float2(1.0, 0.0)) / LevelSize, Mip));
                float iy = 0.5 * (Current.SampleLevel(Linear, (q + float2(0.0, 1.0)) / LevelSize, Mip)
                                - Current.SampleLevel(Linear, (q - float2(0.0, 1.0)) / LevelSize, Mip));
                float it = Previous.SampleLevel(Linear, (q + d) / LevelSize, Mip) - c;
                gxx += ix * ix; gxy += ix * iy; gyy += iy * iy;
                bx  += ix * it; by  += iy * it;
            }

            grip = float2(gxx, gyy);

            const float Damping = 0.003;
            gxx += Damping; gyy += Damping;
            float det = gxx * gyy - gxy * gxy;
            float2 delta = -float2(gyy * bx - gxy * by, gxx * by - gxy * bx) / det;
            return clamp(delta, -1.0, 1.0);
        }

        // Each level answers two questions and keeps both answers. Where was this patch,
        // if it moved: the best of the offsets around where the level above said to look.
        // And did it move at all: how that match compares with the patch simply being
        // where it was. The second matters because a coarse level sees a still thing
        // together with what moves around it - the background behind something crossing
        // it, a game's HUD over its world - and hands the movement down to both.
        //
        // Out: xy the offset, z how surely the patch moved (see Surely).
        float4 MatchPS(VSOut i) : SV_Target
        {
            float2 at = i.uv * LevelSize;
            // The level above answered in its own pixels, which are twice the size of these.
            float2 start = HasParent > 0.5 ? Field.SampleLevel(Linear, i.uv, 0).xy * 2.0 : float2(0.0, 0.0);

            float  best = 1e9, bestRaw = 0.0;
            float2 bestD = start;
        #if QUALITY >= 1
            // A patch at the edge of something has, in the level above, neighbours on both
            // sides of that edge, and the blend of their answers is neither's. So each of
            // the four around it is tried as it stands, and the search starts from the one
            // this patch is found at best - the blend unless one of them clearly beats it.
            if (HasParent > 0.5)
            {
                float2 corner = floor(i.uv * AboveSize - 0.5);
                float startCost = PatchCost(at, start) - Noise * 25.0;
                float2 blend = start;
                [loop] for (int k = 0; k < 4; k++)
                {
                    float2 uv = (corner + float2(k & 1, k >> 1) + 0.5) / AboveSize;
                    float2 theirs = Field.SampleLevel(Point, uv, 0).xy * 2.0;
                    // One the search around the blend reaches anyway is not worth the asking.
                    float2 apart = abs(theirs - blend);
                    if (max(apart.x, apart.y) <= Radius) continue;
                    float cost = PatchCost(at, theirs);
                    if (cost < startCost) { startCost = cost; start = theirs; }
                }
            }

            int radius = (int)(Radius + 0.5);
            [loop] for (int y = -radius; y <= radius; y++)
            [loop] for (int x = -radius; x <= radius; x++)
            {
                float2 delta = float2(x, y);
                float raw = PatchCost(at, start + delta);
                float cost = raw + Reluctance * 25.0 * length(delta);
                if (cost < best) { best = cost; bestRaw = raw; bestD = start + delta; }
            }
        #else
            for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
            {
                float2 delta = float2(x, y);
                float raw = PatchCost(at, start + delta);
                float cost = raw + Reluctance * 25.0 * length(delta);
                if (cost < best) { best = cost; bestRaw = raw; bestD = start + delta; }
            }
        #endif

            float sure = Surely(bestRaw, PatchCost(at, float2(0.0, 0.0)));

            // Only a patch that surely stood still gives its offset up, and then it is
            // exactly still: the slopes would pull it towards whatever else is in the patch
            // and does move. In doubt the offset is kept, because a finer level may yet
            // confirm it and can only look near where it is told to; z carries the doubt.
            float2 grip;
            float2 fine = Slopes(at, bestD, grip);
            bestD = sure < -Settled ? float2(0.0, 0.0) : bestD + fine;
            return float4(bestD, sure, grip.x + grip.y);
        }

        // Below this, in one direction, a patch has nothing to match by: brightness changing
        // by a grey level or two a pixel, summed over the patch.
        static const float Grip = 0.00125;

        // Offsets closer together than this, in this level's pixels, are the same answer.
        static const float Same = 0.0625;

        // A second round of the same correction on the finest level, from the first's
        // answer. It also notes, in w, in which directions the patch had something to match
        // by: 4 for sideways, 8 for up and down.
        float4 RefinePS(VSOut i) : SV_Target
        {
            float2 at = i.uv * LevelSize;
            float4 coarse = Field.SampleLevel(Point, i.uv, 0);

            float2 grip;
            float2 fine = Slopes(at, coarse.xy, grip);
            float2 d = coarse.z < -Settled ? coarse.xy : coarse.xy + fine;
            return float4(d, coarse.z, (grip.x >= Grip ? 4.0 : 0.0) + (grip.y >= Grip ? 8.0 : 0.0));
        }

        #if QUALITY >= 1
        // The same correction as Slopes makes, over a patch seven pixels across instead of
        // five: twice the pixels to average the picture's own noise over, for an answer
        // that is already within a pixel and only wants steadying.
        float2 SlopesWide(float2 at, float2 d)
        {
            float2 back = Shared(d);
            float gxx = 0.0, gxy = 0.0, gyy = 0.0, bx = 0.0, by = 0.0;
            for (int y = -3; y <= 3; y++)
            for (int x = -3; x <= 3; x++)
            {
                float2 q = at + float2(x, y) - back;
                float c  = Current.SampleLevel(Linear, q / LevelSize, Mip);
                float ix = 0.5 * (Current.SampleLevel(Linear, (q + float2(1.0, 0.0)) / LevelSize, Mip)
                                - Current.SampleLevel(Linear, (q - float2(1.0, 0.0)) / LevelSize, Mip));
                float iy = 0.5 * (Current.SampleLevel(Linear, (q + float2(0.0, 1.0)) / LevelSize, Mip)
                                - Current.SampleLevel(Linear, (q - float2(0.0, 1.0)) / LevelSize, Mip));
                float it = Previous.SampleLevel(Linear, (q + d) / LevelSize, Mip) - c;
                gxx += ix * ix; gxy += ix * iy; gyy += iy * iy;
                bx  += ix * it; by  += iy * it;
            }

            const float Damping = 0.006;
            gxx += Damping; gyy += Damping;
            float det = gxx * gyy - gxy * gxy;
            float2 delta = -float2(gyy * bx - gxy * by, gxx * by - gxy * bx) / det;
            return clamp(delta, -1.0, 1.0);
        }

        // The correction on the picture itself, ahead of FinerPS, which makes it once more
        // and gives the verdict. LevelSize and Mip are the picture's; a vector is two of
        // its pixels. A patch that surely stood still is left exactly still.
        float4 PolishPS(VSOut i) : SV_Target
        {
            float4 f = Field.SampleLevel(Point, i.uv, 0);
            if (f.z < -Settled) return f;
            float2 there = f.xy * 2.0;
            there += SlopesWide(i.uv * LevelSize, there);
            return float4(there * 0.5, f.zw);
        }
        #endif

        // The correction once more, on the copy of the picture with twice the detail. The
        // vectors are worked out on a quarter-size copy, where a quarter of a pixel is a
        // whole pixel of the picture; this halves what is left over. The patch is smaller
        // there, so it is only trusted to adjust an answer, not to find one.
        //
        // With the offset now as good as it will get, how surely the patch moved is asked
        // again, and the verdict given on what each half of the vector is worth, sideways
        // and up-and-down apart. A half is a measurement when the patch had something to
        // match by in that direction, and either it is settled whether the patch moved or
        // the half is the same both ways. That is added to w as 1 for the sideways half
        // and 2 for the other.
        float4 FinerPS(VSOut i) : SV_Target
        {
            float4 f = Field.SampleLevel(Point, i.uv, 0);
            int held = (int)(f.w + 0.5);
            float2 d = f.xy;
            float sure = f.z;

            if (sure >= -Settled)
            {
                // LevelSize and Mip are the finer copy's here; a vector is two of its pixels.
                float2 at = i.uv * LevelSize;
                float2 grip;
                float2 there = d * 2.0;
                there += Slopes(at, there, grip);
                sure = Surely(PatchCost(at, there), PatchCost(at, float2(0.0, 0.0)));
                d = sure < -Settled ? float2(0.0, 0.0) : there * 0.5;
            }

            bool settled = abs(sure) > Settled;
            float verdict = held;
            if ((held & 4) != 0 && (settled || abs(d.x) < Same)) verdict += 1.0;
            if ((held & 8) != 0 && (settled || abs(d.y) < Same)) verdict += 2.0;
            return float4(d, sure, verdict);
        }

        // Two patches no further apart than this in brightness are taken to be the same
        // surface.
        static const float SameSurface = 0.04;

        // How far, in vectors, a patch with nothing to match by looks along its surface for
        // one that knows, and how far a patch that had something but came to no verdict
        // looks around itself.
        #if QUALITY >= 1
        static const int Reach = 128;
        static const int Near = 16;
        #else
        static const int Reach = 64;
        static const int Near = 8;
        #endif

        // Walks from a patch in one direction to the first patch whose vector is a
        // measurement in the half asked about (`bit` 1 sideways, 2 up-and-down). With
        // `surface` set it gives up where the brightness stops being that of `here`.
        bool Ask(float2 at, float2 step, int bit, bool surface, float here, out float2 found, out float distance)
        {
            found = float2(0.0, 0.0);
            distance = 0.0;
            bool got = false;
            int reach = surface ? Reach : Near;
            [loop] for (int k = 1; k <= reach; k++)
            {
                float2 q = at + step * k;
                if (any(q < 0.0) || any(q >= LevelSize)) break;
                float2 uv = q / LevelSize;
                if (surface && abs(Current.SampleLevel(Point, uv, Mip) - here) > SameSurface) break;
                float4 f = Field.SampleLevel(Point, uv, 0);
                if (((int)(f.w + 0.5) & bit) != 0) { found = f.xy; distance = k; got = true; break; }
            }
            return got;
        }

        // One half of a vector that is not a measurement, settled from those that are.
        //
        // A patch with nothing to match by in that direction belongs to whatever it is part
        // of, so it asks along its surface, both ways, for the nearest patches that know.
        // Those sit at the surface's edges, and an edge can only tell of movement across
        // itself - slide a flat strip along its own length and nothing changes - which is
        // why the sideways half is asked along the row, whose ends are the edges to the
        // left and right, and the other half along the column. With a patch that knows on
        // both sides it takes what lies between them: the inside of a moving thing moves
        // with its outline. With one side only it is taken to be still - that is the
        // background beside something crossing it, not part of the thing.
        //
        // A patch that had something to match by and still came to no verdict - two things
        // in it, a faint or a repeating pattern - asks its near neighbours whatever they
        // are, and failing them keeps what it had.
        float Settle(float2 at, float2 step, int bit, bool flat, float here, float own)
        {
            float2 a, b;
            float da, db;
            bool ha = Ask(at, -step, bit, flat, here, a, da);
            bool hb = Ask(at,  step, bit, flat, here, b, db);
            float va = bit == 1 ? a.x : a.y;
            float vb = bit == 1 ? b.x : b.y;

            if (ha && hb) return (va * db + vb * da) / (da + db);
        #if QUALITY >= 1
            // With one side only it goes with that side, and with neither it keeps what the
            // levels above made of it: under a camera that is turning, a shadow at the
            // picture's edge moves with the rest, and a wrong guess on a flat thing shows
            // nowhere - it looks the same wherever it is taken from.
            return ha ? va : hb ? vb : own;
        #else
            if (flat) return 0.0;
            return ha ? va : hb ? vb : own;
        #endif
        }

        // The last word on each vector: its measured halves stand, the others are settled.
        float4 ResolvePS(VSOut i) : SV_Target
        {
            float4 own = Field.SampleLevel(Point, i.uv, 0);
            int verdict = (int)(own.w + 0.5);
            if ((verdict & 3) == 3) return own;

            float2 at = i.uv * LevelSize;
            float here = Current.SampleLevel(Point, i.uv, Mip);
            if ((verdict & 1) == 0) own.x = Settle(at, float2(1.0, 0.0), 1, (verdict & 4) == 0, here, own.x);
            if ((verdict & 2) == 0) own.y = Settle(at, float2(0.0, 1.0), 2, (verdict & 8) == 0, here, own.y);
            return own;
        }

        // Orders two vectors a half at a time: the smaller sideways and the smaller
        // up-and-down into the first, the larger into the second.
        void Order(inout float2 a, inout float2 b)
        {
            float2 low = min(a, b);
            b = max(a, b);
            a = low;
        }

        // The middle one of a vector and its eight neighbours, each half apart; `px` is
        // one step of the field in texture coordinates.
        float2 Middle(float2 uv, float2 px)
        {
            float2 v0 = Field.SampleLevel(Point, uv + px * float2(-1.0, -1.0), 0).xy;
            float2 v1 = Field.SampleLevel(Point, uv + px * float2( 0.0, -1.0), 0).xy;
            float2 v2 = Field.SampleLevel(Point, uv + px * float2( 1.0, -1.0), 0).xy;
            float2 v3 = Field.SampleLevel(Point, uv + px * float2(-1.0,  0.0), 0).xy;
            float2 v4 = Field.SampleLevel(Point, uv, 0).xy;
            float2 v5 = Field.SampleLevel(Point, uv + px * float2( 1.0,  0.0), 0).xy;
            float2 v6 = Field.SampleLevel(Point, uv + px * float2(-1.0,  1.0), 0).xy;
            float2 v7 = Field.SampleLevel(Point, uv + px * float2( 0.0,  1.0), 0).xy;
            float2 v8 = Field.SampleLevel(Point, uv + px * float2( 1.0,  1.0), 0).xy;

            // The shortest known sequence of comparisons that leaves the middle of nine in
            // the middle.
            Order(v1, v2); Order(v4, v5); Order(v7, v8);
            Order(v0, v1); Order(v3, v4); Order(v6, v7);
            Order(v1, v2); Order(v4, v5); Order(v7, v8);
            Order(v0, v3); Order(v5, v8); Order(v4, v7);
            Order(v3, v6); Order(v1, v4); Order(v2, v5);
            Order(v4, v7); Order(v4, v2); Order(v6, v4);
            Order(v4, v2);
            return v4;
        }

        // A match can simply be wrong - a dark, noisy corner finds itself somewhere else -
        // and a wrong vector stands alone among right ones. Each vector is replaced by the
        // middle one of itself and its eight neighbours, each half apart, which removes a
        // lone stray and leaves an edge between two movements where it was.
        float4 MedianPS(VSOut i) : SV_Target
        {
            float4 own = Field.SampleLevel(Point, i.uv, 0);
            return float4(Middle(i.uv, 1.0 / LevelSize), own.zw);
        }

        #if QUALITY >= 2
        // One vector for every pixel of the picture, out of the field of one for every 2x2
        // block. Between blocks that moved alike the blend of them is the answer already.
        // Where they did not - the edge of one thing over another - the blend is nobody's,
        // and the pixel takes the vector of whichever block around it its own surroundings
        // are found at best. Those surroundings are five pixels across, so what is strongly
        // drawn carries its vector a pixel or two past its own edge: an outline, a thin
        // line, a letter goes with the thing it belongs to, which is what an upscaler wants
        // of vectors - and no further, or the thing would be drawn over what is behind it.
        //
        // LevelSize and Mip are the picture's own here, AboveSize the field's; a vector of
        // the field is two of the picture's pixels. Out: the vector in the picture's pixels.
        float2 DensePS(VSOut i) : SV_Target
        {
            float2 at = i.uv * LevelSize;
            float2 corner = floor(i.uv * AboveSize - 0.5);
            float2 blend = Field.SampleLevel(Linear, i.uv, 0).xy * 2.0;
            float2 d = blend;

            float2 c0 = Field.SampleLevel(Point, (corner + float2(0.5, 0.5)) / AboveSize, 0).xy * 2.0;
            float2 c1 = Field.SampleLevel(Point, (corner + float2(1.5, 0.5)) / AboveSize, 0).xy * 2.0;
            float2 c2 = Field.SampleLevel(Point, (corner + float2(0.5, 1.5)) / AboveSize, 0).xy * 2.0;
            float2 c3 = Field.SampleLevel(Point, (corner + float2(1.5, 1.5)) / AboveSize, 0).xy * 2.0;

            // Blocks that agree to within half a pixel have nothing to choose between.
            float2 low = min(min(c0, c1), min(c2, c3)), high = max(max(c0, c1), max(c2, c3));
            if (max(high.x - low.x, high.y - low.y) > 0.5)
            {
                float best = PatchCost(at, blend) - Noise * 25.0 * 0.5;
                float cost = PatchCost(at, c0); if (cost < best) { best = cost; d = c0; }
                cost = PatchCost(at, c1); if (cost < best) { best = cost; d = c1; }
                cost = PatchCost(at, c2); if (cost < best) { best = cost; d = c2; }
                cost = PatchCost(at, c3); if (cost < best) { best = cost; d = c3; }
            }
            return d;
        }

        // The strays among the per-pixel vectors removed, as MedianPS does for the blocks'.
        float2 DenseMedianPS(VSOut i) : SV_Target
        {
            return Middle(i.uv, 1.0 / LevelSize);
        }
        #endif

        // BT.709 primaries to the swapchain's BT.2020; see the renderer's SdrToPQ.
        static const float3x3 Rec709To2020 = {
            0.6274, 0.3293, 0.0433,
            0.0691, 0.9195, 0.0114,
            0.0164, 0.0880, 0.8956 };

        float3 LinearToPQ(float3 L)
        {
            const float m1 = 0.1593017578125, m2 = 78.84375;
            const float c1 = 0.8359375, c2 = 18.8515625, c3 = 18.6875;
            float3 Lm = pow(saturate(L), m1);
            return pow((c1 + c2 * Lm) / (1.0 + c3 * Lm), m2);
        }

        float3 Hue(float turn)
        {
            return saturate(abs(frac(turn + float3(0.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0) - 1.0);
        }

        // The field at the picture's own size and in its own pixels, for PerPixel.
        float2 SpreadPS(VSOut i) : SV_Target
        {
            return Field.SampleLevel(Linear, i.uv, 0).xy * UnitToOut;
        }

        float4 ShowPS(VSOut i) : SV_Target
        {
            // The field says where each patch was; the picture moved the opposite way.
            float2 here = -Field.SampleLevel(Linear, i.uv, 0).xy * UnitToOut;
            float  speed = length(here);

            // Direction as colour, speed as strength. Full strength at 1.5% of the width a frame.
            float3 tint = Hue(atan2(here.y, here.x) / 6.2831853);
            float  strength = saturate(speed / (OutSize.x * 0.015));

            // A needle per cell, from its centre along the motion there, drawn three times
            // its real length so that a slow drift is still visible.
            const float Cell = 28.0;
            float2 p = i.uv * OutSize;
            float2 centre = (floor(p / Cell) + 0.5) * Cell;
            float2 v = -Field.SampleLevel(Point, centre / OutSize, 0).xy * UnitToOut * 3.0;
            float  len = length(v);
            if (len > Cell * 1.6) v *= Cell * 1.6 / len;

            float2 rel = p - centre;
            float  along = saturate(dot(rel, v) / max(dot(v, v), 1e-4));
            float  needle = saturate(1.3 - length(rel - along * v)) * step(0.75, len);
            float  hub = saturate(2.2 - length(rel));

            float3 rgb;
            float  alpha;
            if (Alone > 0.5)
            {
                rgb = lerp(tint * strength, float3(1.0, 1.0, 1.0), max(needle, hub * 0.5));
                alpha = 1.0;
            }
            else
            {
                float mark = max(needle, hub * 0.6);
                rgb = lerp(tint, float3(1.0, 1.0, 1.0), mark);
                alpha = max(strength * 0.5, mark * 0.9);
            }

            if (IsPq > 0.5) rgb = LinearToPQ(mul(Rec709To2020, pow(rgb, 2.2)) * (Nits / 10000.0));
            return float4(rgb, alpha);
        }
        """;
}
