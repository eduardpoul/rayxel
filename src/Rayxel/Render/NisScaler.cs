using System.Text;
using System.Text.RegularExpressions;
using Rayxel.Diagnostics;
using Vortice.D3DCompiler;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Rayxel.Render;

/// <summary>
/// NVIDIA Image Scaling — a vendor-agnostic spatial upscaler that runs as a plain compute
/// shader on any GPU. Serves two purposes here: an upscale path for hardware without a
/// driver filter, and a cheap baseline to measure RTX VSR against.
/// <para>
/// The shader and its coefficient tables are NVIDIA's MIT-licensed sources in NIS/. The
/// tables are parsed out of the header at load time rather than transcribed, so there is
/// no second copy of several hundred constants to drift out of sync.
/// </para>
/// </summary>
public sealed class NisScaler : IDisposable
{
    // NVIDIA's recommended tuning for their own hardware; also fine elsewhere.
    private const int BlockWidth = 32, BlockHeight = 24, ThreadGroupSize = 256;
    private const int PhaseCount = 64, FilterSize = 8;

    private readonly ID3D11Device _device;
    private ID3D11ComputeShader? _shader;
    private ID3D11Buffer? _constants;
    private ID3D11SamplerState? _sampler;
    private ID3D11ShaderResourceView? _coefScaleView, _coefUsmView;
    private ID3D11Texture2D? _coefScale, _coefUsm;

    private ID3D11Texture2D? _output;
    private ID3D11UnorderedAccessView? _outputUav;
    private int _outWidth, _outHeight;

    private NisHdrMode _builtForHdrMode = (NisHdrMode)999;

    public bool Available { get; private set; }
    public string? LastError { get; private set; }
    public ID3D11ShaderResourceView? OutputView { get; private set; }
    public int OutputWidth => _outWidth;
    public int OutputHeight => _outHeight;

    /// <summary>
    /// 0 = no sharpening, 1 = maximum. NVIDIA's own default is 0.5; the design lowers it to
    /// 0.4 because at the scale factors this app actually runs at — around 1.27x on an
    /// ultrawide — 0.5 reads as an unsharp mask rather than as extra detail. That was the
    /// first reaction the first time it was tried, so the default follows the observation.
    /// </summary>
    public float Sharpness { get; set; } = 0.4f;

    public NisScaler(ID3D11Device device) => _device = device;

    /// <summary>
    /// Compiles the shader for a given HDR mode and uploads the coefficient tables.
    /// Recompiles when the HDR mode changes, since NIS bakes it in as a define.
    /// </summary>
    public bool Initialize(NisHdrMode hdrMode)
    {
        if (Available && _builtForHdrMode == hdrMode) return true;

        try
        {
            if (ReadEmbedded("NIS_Scaler.h") is not { } scalerSource ||
                ReadEmbedded("NIS_Config.h") is not { } configSource)
            {
                LastError = "NIS sources are missing from the assembly";
                Log.Warn(LastError);
                return false;
            }

            var source = BuildShaderSource(scalerSource, hdrMode);
            Compiler.Compile(source, [], null, "main", "NIS.hlsl", "cs_5_0", out var blob, out var err);
            if (blob is null)
            {
                LastError = $"NIS compile failed: {err?.AsString()}";
                Log.Warn(LastError);
                return false;
            }

            _shader?.Dispose();
            _shader = _device.CreateComputeShader(blob.AsSpan());
            blob.Dispose();

            if (_constants is null)
            {
                _constants = _device.CreateBuffer(new BufferDescription(256, BindFlags.ConstantBuffer,
                    ResourceUsage.Dynamic, CpuAccessFlags.Write));

                _sampler = _device.CreateSamplerState(new SamplerDescription
                {
                    Filter = Filter.MinMagMipLinear,
                    AddressU = TextureAddressMode.Clamp,
                    AddressV = TextureAddressMode.Clamp,
                    AddressW = TextureAddressMode.Clamp,
                    MaxLOD = float.MaxValue
                });

                (_coefScale, _coefScaleView) = CreateCoefficientTexture(configSource, "coef_scale");
                (_coefUsm, _coefUsmView) = CreateCoefficientTexture(configSource, "coef_usm");
            }

            _builtForHdrMode = hdrMode;
            Available = true;
            LastError = null;
            Log.Info($"NIS ready (HDR mode {hdrMode}, block {BlockWidth}x{BlockHeight}, {ThreadGroupSize} threads)");
            return true;
        }
        catch (Exception ex)
        {
            LastError = $"NIS init failed: {ex.Message}";
            Log.Warn(LastError);
            Available = false;
            return false;
        }
    }

    /// <summary>
    /// Reads one of NVIDIA's headers out of the assembly.
    /// <para>
    /// Embedded rather than copied next to the executable so a single-file build actually
    /// is a single file. Loose content files sit beside the exe and go missing the moment
    /// someone moves just the exe — which is exactly what people do.
    /// </para>
    /// </summary>
    private static string? ReadEmbedded(string fileName)
    {
        var assembly = typeof(NisScaler).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;

        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Wraps NIS_Scaler.h with the bindings and defines it expects. The constant buffer
    /// must be declared before the include: the shader reads those names as globals.
    /// </summary>
    private static string BuildShaderSource(string scalerSource, NisHdrMode hdrMode)
    {
        var sb = new StringBuilder(scalerSource.Length + 2048);
        sb.AppendLine("#define NIS_SCALER 1");
        sb.AppendLine("#define NIS_HLSL 1");
        sb.AppendLine($"#define NIS_HDR_MODE {(uint)hdrMode}");
        sb.AppendLine($"#define NIS_BLOCK_WIDTH {BlockWidth}");
        sb.AppendLine($"#define NIS_BLOCK_HEIGHT {BlockHeight}");
        sb.AppendLine($"#define NIS_THREAD_GROUP_SIZE {ThreadGroupSize}");
        sb.AppendLine("""
            cbuffer cb : register(b0)
            {
                float kDetectRatio;
                float kDetectThres;
                float kMinContrastRatio;
                float kRatioNorm;
                float kContrastBoost;
                float kEps;
                float kSharpStartY;
                float kSharpScaleY;
                float kSharpStrengthMin;
                float kSharpStrengthScale;
                float kSharpLimitMin;
                float kSharpLimitScale;
                float kScaleX;
                float kScaleY;
                float kDstNormX;
                float kDstNormY;
                float kSrcNormX;
                float kSrcNormY;
                uint  kInputViewportOriginX;
                uint  kInputViewportOriginY;
                uint  kInputViewportWidth;
                uint  kInputViewportHeight;
                uint  kOutputViewportOriginX;
                uint  kOutputViewportOriginY;
                uint  kOutputViewportWidth;
                uint  kOutputViewportHeight;
                float reserved0;
                float reserved1;
            };

            SamplerState samplerLinearClamp : register(s0);
            Texture2D in_texture   : register(t0);
            Texture2D coef_scaler  : register(t1);
            Texture2D coef_usm     : register(t2);
            RWTexture2D<unorm float4> out_texture : register(u0);
            """);
        sb.AppendLine(scalerSource);
        sb.AppendLine($$"""
            [numthreads({{ThreadGroupSize}}, 1, 1)]
            void main(uint3 blockIdx : SV_GroupID, uint3 threadIdx : SV_GroupThreadID)
            {
                NVScaler(blockIdx.xy, threadIdx.x);
            }
            """);
        return sb.ToString();
    }

    /// <summary>
    /// Pulls a [64][8] float table out of NIS_Config.h and uploads it as a 2x64 RGBA32F
    /// texture, which is how the shader indexes it (four coefficients per texel).
    /// </summary>
    private unsafe (ID3D11Texture2D, ID3D11ShaderResourceView) CreateCoefficientTexture(string headerText, string name)
    {
        var start = headerText.IndexOf($"{name}[kPhaseCount][kFilterSize]", StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException($"table {name} not found in NIS_Config.h");
        var open = headerText.IndexOf('{', start);
        var close = headerText.IndexOf("};", open, StringComparison.Ordinal);
        if (open < 0 || close < 0) throw new InvalidOperationException($"table {name} is malformed");

        var body = headerText[open..close];
        var values = Regex.Matches(body, @"-?\d+\.\d+(?:[eE][-+]?\d+)?f?")
                          .Select(m => float.Parse(m.Value.TrimEnd('f'), System.Globalization.CultureInfo.InvariantCulture))
                          .ToArray();

        if (values.Length != PhaseCount * FilterSize)
            throw new InvalidOperationException($"table {name}: expected {PhaseCount * FilterSize} values, parsed {values.Length}");

        var texture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = FilterSize / 4,   // four floats per RGBA texel
            Height = PhaseCount,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R32G32B32A32_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Immutable,
            BindFlags = BindFlags.ShaderResource
        }, new SubresourceData(values.AsMemory().Pin().Pointer, FilterSize * sizeof(float)));

        Log.Debug($"NIS table {name}: {values.Length} coefficients uploaded");
        return (texture, _device.CreateShaderResourceView(texture));
    }

    /// <summary>
    /// Upscales <paramref name="input"/> (RGB) into an internal texture exposed as
    /// <see cref="OutputView"/>. Returns false when NIS cannot handle the ratio — it only
    /// covers upscales between 1x and 2x.
    /// </summary>
    public bool Process(ID3D11DeviceContext context, ID3D11ShaderResourceView input,
                        int srcWidth, int srcHeight, int dstWidth, int dstHeight, NisHdrMode hdrMode)
    {
        if (!Initialize(hdrMode)) return false;

        var config = new NisConfig();
        if (!NisConfig.Update(ref config, Sharpness, (uint)srcWidth, (uint)srcHeight,
                              (uint)dstWidth, (uint)dstHeight, hdrMode))
        {
            LastError = $"NIS does not cover {srcWidth}x{srcHeight} -> {dstWidth}x{dstHeight} " +
                        "(only upscales between 1x and 2x)";
            return false;
        }

        EnsureOutput(dstWidth, dstHeight);

        unsafe
        {
            var mapped = context.Map(_constants!, 0, Vortice.Direct3D11.MapMode.WriteDiscard);
            *(NisConfig*)mapped.DataPointer = config;
            context.Unmap(_constants!, 0);
        }

        context.CSSetShader(_shader);
        context.CSSetConstantBuffer(0, _constants);
        context.CSSetSampler(0, _sampler);
        context.CSSetShaderResources(0, [input, _coefScaleView!, _coefUsmView!]);
        context.CSSetUnorderedAccessView(0, _outputUav);

        context.Dispatch((uint)((dstWidth + BlockWidth - 1) / BlockWidth),
                         (uint)((dstHeight + BlockHeight - 1) / BlockHeight), 1);

        // Unbind: the output is sampled as an SRV next, and D3D will not allow both.
        context.CSSetUnorderedAccessView(0, null);
        context.CSSetShaderResources(0, [null, null, null]);
        context.CSSetShader(null);

        LastError = null;
        return true;
    }

    private void EnsureOutput(int width, int height)
    {
        if (_output is not null && _outWidth == width && _outHeight == height) return;

        OutputView?.Dispose(); _outputUav?.Dispose(); _output?.Dispose();

        _output = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.UnorderedAccess
        });
        _outputUav = _device.CreateUnorderedAccessView(_output);
        OutputView = _device.CreateShaderResourceView(_output);
        _outWidth = width; _outHeight = height;
        Log.Debug($"NIS output surface {width}x{height}");
    }

    public void Dispose()
    {
        OutputView?.Dispose(); _outputUav?.Dispose(); _output?.Dispose();
        _coefScaleView?.Dispose(); _coefScale?.Dispose();
        _coefUsmView?.Dispose(); _coefUsm?.Dispose();
        _sampler?.Dispose(); _constants?.Dispose(); _shader?.Dispose();
    }
}
