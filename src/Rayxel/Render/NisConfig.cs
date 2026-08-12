using System.Runtime.InteropServices;

namespace Rayxel.Render;

public enum NisHdrMode : uint { None = 0, Linear = 1, Pq = 2 }

/// <summary>
/// CPU-side constant buffer for NIS. Field order and semantics are a direct port of
/// <c>NISConfig</c> in NIS_Config.h and must not be reordered — the shader reads them
/// positionally. Values come from <see cref="Update"/>, ported from NVScalerUpdateConfig.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 256)]
public struct NisConfig
{
    public float DetectRatio, DetectThres, MinContrastRatio, RatioNorm;
    public float ContrastBoost, Eps, SharpStartY, SharpScaleY;
    public float SharpStrengthMin, SharpStrengthScale, SharpLimitMin, SharpLimitScale;
    public float ScaleX, ScaleY, DstNormX, DstNormY;
    public float SrcNormX, SrcNormY;
    public uint InputViewportOriginX, InputViewportOriginY, InputViewportWidth, InputViewportHeight;
    public uint OutputViewportOriginX, OutputViewportOriginY, OutputViewportWidth, OutputViewportHeight;
    public float Reserved0, Reserved1;

    /// <summary>
    /// Port of NVScalerUpdateConfig. Returns false when the scale factor falls outside
    /// what NIS supports — it only handles upscales between 1x and 2x, so an output
    /// smaller than the input, or more than double it, has to fall back elsewhere.
    /// </summary>
    public static bool Update(ref NisConfig c, float sharpness,
        uint inputWidth, uint inputHeight,
        uint outputWidth, uint outputHeight,
        NisHdrMode hdrMode = NisHdrMode.None)
    {
        sharpness = Math.Clamp(sharpness, 0f, 1f);
        float slider = sharpness - 0.5f; // map 0..1 onto -0.5..+0.5

        // Sharpening is scaled asymmetrically: 0% must mean no sharpening at all, while
        // 100% must not run away into obvious over-sharpening.
        float maxScale = slider >= 0f ? 1.25f : 1.75f;
        float minScale = slider >= 0f ? 1.25f : 1.0f;
        float limitScale = slider >= 0f ? 1.25f : 1.0f;

        float detectRatio = 2 * 1127f / 1024f;

        float detectThres = 64.0f / 1024.0f;
        float minContrastRatio = 2.0f;
        float maxContrastRatio = 10.0f;

        float sharpStartY = 0.45f;
        float sharpEndY = 0.9f;
        float sharpStrengthMin = MathF.Max(0.0f, 0.4f + slider * minScale * 1.2f);
        float sharpStrengthMax = 1.6f + slider * maxScale * 1.8f;
        float sharpLimitMin = MathF.Max(0.1f, 0.14f + slider * limitScale * 0.32f);
        float sharpLimitMax = 0.5f + slider * limitScale * 0.6f;

        if (hdrMode is NisHdrMode.Linear or NisHdrMode.Pq)
        {
            detectThres = 32.0f / 1024.0f;
            minContrastRatio = 1.5f;
            maxContrastRatio = 5.0f;

            sharpStrengthMin = MathF.Max(0.0f, 0.4f + slider * minScale * 1.1f);
            sharpStrengthMax = 2.2f + slider * maxScale * 1.8f;
            sharpLimitMin = MathF.Max(0.06f, 0.10f + slider * limitScale * 0.28f);
            sharpLimitMax = 0.6f + slider * limitScale * 0.6f;

            (sharpStartY, sharpEndY) = hdrMode == NisHdrMode.Pq ? (0.35f, 0.55f) : (0.3f, 0.5f);
        }

        if (inputWidth == 0 || inputHeight == 0 || outputWidth == 0 || outputHeight == 0)
            return false;

        c.InputViewportWidth = inputWidth;
        c.InputViewportHeight = inputHeight;
        c.OutputViewportWidth = outputWidth;
        c.OutputViewportHeight = outputHeight;
        c.InputViewportOriginX = 0;
        c.InputViewportOriginY = 0;
        c.OutputViewportOriginX = 0;
        c.OutputViewportOriginY = 0;

        c.SrcNormX = 1f / inputWidth;
        c.SrcNormY = 1f / inputHeight;
        c.DstNormX = 1f / outputWidth;
        c.DstNormY = 1f / outputHeight;
        c.ScaleX = inputWidth / (float)outputWidth;
        c.ScaleY = inputHeight / (float)outputHeight;

        c.DetectRatio = detectRatio;
        c.DetectThres = detectThres;
        c.MinContrastRatio = minContrastRatio;
        c.RatioNorm = 1.0f / (maxContrastRatio - minContrastRatio);
        c.ContrastBoost = 1.0f;
        c.Eps = 1.0f / 255.0f;
        c.SharpStartY = sharpStartY;
        c.SharpScaleY = 1.0f / (sharpEndY - sharpStartY);
        c.SharpStrengthMin = sharpStrengthMin;
        c.SharpStrengthScale = sharpStrengthMax - sharpStrengthMin;
        c.SharpLimitMin = sharpLimitMin;
        c.SharpLimitScale = sharpLimitMax - sharpLimitMin;

        return c.ScaleX is >= 0.5f and <= 1f && c.ScaleY is >= 0.5f and <= 1f;
    }
}
