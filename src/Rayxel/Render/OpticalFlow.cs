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
/// call it none. The result is one vector for every 4x4 block of the picture.
/// </para>
/// <para>
/// This is a guess, and it guesses wrong in known places: where something came out from
/// behind something else and has nothing to match, on repeating patterns, on flat areas,
/// and wherever two things move in the same place, such as a game's interface over its
/// world. The cost of the match is kept beside each vector so that a later stage can tell
/// a confident one from a doubtful one.
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
    private ID3D11PixelShader? _reducePs, _matchPs, _refinePs, _showPs;
    private ID3D11Buffer? _constants;
    private ID3D11SamplerState? _point;
    private bool _compiled, _failed;

    // Two luma pyramids, swapped every frame: the newest and the one before it.
    private readonly ID3D11Texture2D?[] _pyramid = new ID3D11Texture2D?[2];
    private readonly ID3D11RenderTargetView?[] _pyramidTop = new ID3D11RenderTargetView?[2];
    private readonly ID3D11ShaderResourceView?[] _pyramidView = new ID3D11ShaderResourceView?[2];
    private int _current;
    private bool _havePrevious;

    // One vector field per pyramid level searched, coarsest last, plus the refined result.
    private ID3D11Texture2D?[] _field = [];
    private ID3D11RenderTargetView?[] _fieldTarget = [];
    private ID3D11ShaderResourceView?[] _fieldView = [];
    private (int Width, int Height)[] _levelSize = [];
    private ID3D11Texture2D? _result;
    private ID3D11RenderTargetView? _resultTarget;
    private ID3D11ShaderResourceView? _resultView;

    private int _videoWidth, _videoHeight;

    public FlowView View { get; set; } = FlowView.Off;

    /// <summary>True once there is a vector field to show: two frames have been seen.</summary>
    public bool Ready { get; private set; }

    /// <summary>Why it is not running, when it is not.</summary>
    public string? LastError { get; private set; }

    /// <summary>Vectors per frame, as columns by rows.</summary>
    public (int Width, int Height) FieldSize => _levelSize.Length > 0 ? _levelSize[0] : (0, 0);

    public OpticalFlow(ID3D11Device device) => _device = device;

    private bool Compile()
    {
        if (_compiled) return true;
        if (_failed) return false;

        try
        {
            _vs = _device.CreateVertexShader(Build("VS", "vs_5_0"));
            _reducePs = _device.CreatePixelShader(Build("ReducePS", "ps_5_0"));
            _matchPs = _device.CreatePixelShader(Build("MatchPS", "ps_5_0"));
            _refinePs = _device.CreatePixelShader(Build("RefinePS", "ps_5_0"));
            _showPs = _device.CreatePixelShader(Build("ShowPS", "ps_5_0"));

            _constants = _device.CreateBuffer(new BufferDescription(64, BindFlags.ConstantBuffer,
                ResourceUsage.Dynamic, CpuAccessFlags.Write));
            _point = _device.CreateSamplerState(new SamplerDescription
            {
                Filter = Filter.MinMagMipPoint,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                MaxLOD = float.MaxValue
            });

            _compiled = true;
            Log.Info("optical flow: shaders ready");
            return true;
        }
        catch (Exception ex)
        {
            _failed = true;
            LastError = ex.Message;
            Log.Warn($"optical flow unavailable: {ex.Message}");
            return false;
        }

        static ReadOnlySpan<byte> Build(string entry, string profile)
        {
            Compiler.Compile(ShaderSource, [], null, entry, "flow.hlsl", profile, out var blob, out var errors);
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

        int topWidth = Math.Max(1, videoWidth / 2), topHeight = Math.Max(1, videoHeight / 2);

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
        (_result, _resultTarget, _resultView) = Field(_levelSize[0].Width, _levelSize[0].Height);

        _havePrevious = false;
        Ready = false;

        int reach = 0;
        for (int i = 0; i < _levelSize.Length; i++) reach += SearchRadius << (FinestMip + 1 + i);
        Log.Info($"optical flow: {_levelSize[0].Width}x{_levelSize[0].Height} vectors for {videoWidth}x{videoHeight}, " +
                 $"{_levelSize.Length} levels, movement of up to about {reach} px a frame");
    }

    // How far each level looks either side of where the level above said to look.
    private const int SearchRadius = 2;

    private (ID3D11Texture2D, ID3D11RenderTargetView, ID3D11ShaderResourceView) Field(int width, int height)
    {
        // xy is the vector in this level's own pixels, z the cost of the match it won with,
        // w how much texture there was to judge by: near zero means the vector was inherited.
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

        // The new frame's brightness at half size, and from that every smaller copy.
        Unbind(context);
        context.OMSetRenderTargets(_pyramidTop[_current]!);
        context.RSSetViewport(0, 0, Math.Max(1, videoWidth / 2), Math.Max(1, videoHeight / 2));
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
            SetConstants(context, _levelSize[i].Width, _levelSize[i].Height, FinestMip + i, hasParent);
            context.PSSetShaderResource(0, _pyramidView[_current]!);
            context.PSSetShaderResource(1, _pyramidView[previous]!);
            if (hasParent) context.PSSetShaderResource(2, _fieldView[i + 1]!);
            context.Draw(4, 0);
        }

        // One more round of the sub-pixel correction on the finest level.
        Unbind(context);
        context.OMSetRenderTargets(_resultTarget!);
        context.RSSetViewport(0, 0, _levelSize[0].Width, _levelSize[0].Height);
        SetConstants(context, _levelSize[0].Width, _levelSize[0].Height, FinestMip, hasParent: true);
        context.PSSetShader(_refinePs);
        context.PSSetShaderResource(0, _pyramidView[_current]!);
        context.PSSetShaderResource(1, _pyramidView[previous]!);
        context.PSSetShaderResource(2, _fieldView[0]!);
        context.Draw(4, 0);

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
            p[14] = 0; p[15] = 0;
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

    private void SetConstants(ID3D11DeviceContext context, int width, int height, int mip, bool hasParent)
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
            for (int i = 8; i < 16; i++) p[i] = 0;
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
        _resultView?.Dispose(); _resultView = null;
        _resultTarget?.Dispose(); _resultTarget = null;
        _result?.Dispose(); _result = null;
        _videoWidth = _videoHeight = 0;
        Ready = false;
    }

    public void Dispose()
    {
        Release();
        _point?.Dispose(); _constants?.Dispose();
        _showPs?.Dispose(); _refinePs?.Dispose(); _matchPs?.Dispose(); _reducePs?.Dispose(); _vs?.Dispose();
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
            float  UnitToOut;  // show: window pixels per vector unit
            float  Alone;      // show: 1 = vectors on black, 0 = over the video
            float  IsPq;       // show: 1 when the swapchain is PQ
            float  Nits;       // show: level to encode at for PQ
            float2 _pad;
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

        // How unlike two 5x5 patches are: the sum of absolute differences, of the patch
        // around `at` in the new frame and the one displaced by `d` in the previous frame.
        float PatchCost(float2 at, float2 d)
        {
            float cost = 0.0;
            for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
            {
                float2 o = float2(x, y);
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

        // Matching whole pixels cannot see a movement smaller than one, and in the small
        // copies of the picture nearly every movement is. This finds the remainder from
        // the slopes instead (Lucas-Kanade): where brightness changes by so much per
        // pixel, a brightness difference of so much between the frames means the patch
        // sits that fraction of a pixel away. It is solved over the whole patch for both
        // directions at once. On a flat patch there are no slopes and the answer is "no
        // correction"; along a plain edge only the movement across the edge can be told,
        // and the damping term makes it return just that rather than something wild.
        // `strength` is how well both directions were pinned down.
        float2 Slopes(float2 at, float2 d, out float strength)
        {
            float gxx = 0.0, gxy = 0.0, gyy = 0.0, bx = 0.0, by = 0.0;
            for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
            {
                float2 q = at + float2(x, y);
                float c  = Current.SampleLevel(Linear, q / LevelSize, Mip);
                float ix = 0.5 * (Current.SampleLevel(Linear, (q + float2(1.0, 0.0)) / LevelSize, Mip)
                                - Current.SampleLevel(Linear, (q - float2(1.0, 0.0)) / LevelSize, Mip));
                float iy = 0.5 * (Current.SampleLevel(Linear, (q + float2(0.0, 1.0)) / LevelSize, Mip)
                                - Current.SampleLevel(Linear, (q - float2(0.0, 1.0)) / LevelSize, Mip));
                float it = Previous.SampleLevel(Linear, (q + d) / LevelSize, Mip) - c;
                gxx += ix * ix; gxy += ix * iy; gyy += iy * iy;
                bx  += ix * it; by  += iy * it;
            }

            float trace = gxx + gyy;
            float det   = gxx * gyy - gxy * gxy;
            strength = 0.5 * (trace - sqrt(max(trace * trace - 4.0 * det, 0.0)));

            const float Damping = 0.003;
            gxx += Damping; gyy += Damping;
            det = gxx * gyy - gxy * gxy;
            float2 delta = -float2(gyy * bx - gxy * by, gxx * by - gxy * bx) / det;
            return clamp(delta, -1.0, 1.0);
        }

        float4 MatchPS(VSOut i) : SV_Target
        {
            float2 at = i.uv * LevelSize;
            // The level above answered in its own pixels, which are twice the size of these.
            float2 start = HasParent > 0.5 ? Field.SampleLevel(Linear, i.uv, 0).xy * 2.0 : float2(0.0, 0.0);

            float  best = 1e9;
            float2 bestD = start;
            for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
            {
                float2 delta = float2(x, y);
                float cost = PatchCost(at, start + delta) + Reluctance * 25.0 * length(delta);
                if (cost < best) { best = cost; bestD = start + delta; }
            }

            float strength;
            bestD += Slopes(at, bestD, strength);
            return float4(bestD, best / 25.0, strength);
        }

        // A second round of the same correction on the finest level, from the first's answer.
        float4 RefinePS(VSOut i) : SV_Target
        {
            float2 at = i.uv * LevelSize;
            float4 coarse = Field.SampleLevel(Point, i.uv, 0);

            float strength;
            float2 d = coarse.xy + Slopes(at, coarse.xy, strength);
            return float4(d, coarse.z, strength);
        }

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

            if (IsPq > 0.5) rgb = LinearToPQ(pow(rgb, 2.2) * (Nits / 10000.0));
            return float4(rgb, alpha);
        }
        """;
}
