using System.Runtime.InteropServices;
using SharpGen.Runtime;

namespace Rayxel.Send.Windows;

/// <summary>
/// ICodecAPI, the interface through which a Media Foundation encoder is told its rate
/// control, GOP and latency, and asked for a keyframe. Vortice does not wrap it, and SetValue
/// is all this needs, so it is called through the vtable directly.
/// </summary>
internal sealed unsafe class CodecApi : IDisposable
{
    private static readonly Guid Iid = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");

    private const ushort VtBool = 11, VtUI4 = 19;

    // The CODECAPI_ properties used, from codecapi.h.
    public static readonly Guid RateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    public static readonly Guid MeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    public static readonly Guid MaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    public static readonly Guid BufferSize = new("0db96574-b6a4-4c8b-8106-3773de0310cd");
    public static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    public static readonly Guid BPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    public static readonly Guid ForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    public static readonly Guid LowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid ScenarioInfo = new("b28a6e64-3ff9-446a-8a4b-0d7a53413236");

    public const uint RateControlCbr = 0;      // eAVEncCommonRateControlMode_CBR
    public const uint ScenarioDisplayRemoting = 1; // eAVScenarioInfo_DisplayRemoting

    /// <summary>VARIANT as it is on x64: the type, three reserved words, then sixteen bytes of value.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public ulong Value;
    }

    private IntPtr _api;

    private CodecApi(IntPtr api) => _api = api;

    /// <summary>The encoder's ICodecAPI, or null when it has none.</summary>
    public static CodecApi? Of(ComObject encoder) =>
        encoder.QueryInterface(Iid, out IntPtr api).Success && api != IntPtr.Zero ? new CodecApi(api) : null;

    public int Set(Guid property, uint value) => SetValue(property, VtUI4, value);

    /// <summary>VARIANT_TRUE is all ones in a 16-bit word.</summary>
    public int Set(Guid property, bool value) => SetValue(property, VtBool, value ? 0xFFFFu : 0u);

    /// <summary>ICodecAPI::SetValue, the tenth entry of the vtable (after IUnknown's three and six of its own). The HRESULT.</summary>
    private int SetValue(Guid property, ushort type, ulong value)
    {
        var variant = new Variant { Type = type, Value = value };
        var call = (delegate* unmanaged<IntPtr, Guid*, Variant*, int>)(*(void***)_api)[9];
        return call(_api, &property, &variant);
    }

    public void Dispose()
    {
        if (_api != IntPtr.Zero) Marshal.Release(_api);
        _api = IntPtr.Zero;
    }
}
