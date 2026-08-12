namespace Rayxel.Capture;

/// <summary>
/// Media Foundation GUIDs we depend on. Every value here was verified at runtime
/// against Vortice 3.8.3 and matches the Windows SDK headers. Attributes we only
/// want to *observe* (colour primaries, transfer function) are deliberately absent:
/// media types are dumped by enumerating their attributes, so an unrecognised GUID
/// prints raw rather than being guessed at.
/// </summary>
public static class MfGuids
{
    // --- Source reader / read-write attributes -------------------------------
    /// <summary>MF_LOW_LATENCY — disables reordering and trims internal queueing.</summary>
    public static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    /// <summary>MF_READWRITE_DISABLE_CONVERTERS — fail rather than silently insert a converter MFT.</summary>
    public static readonly Guid DisableConverters = new("98d5b065-1374-4847-8d5d-31520fee7156");

    /// <summary>MF_SOURCE_READER_D3D_MANAGER — makes samples arrive as D3D11 textures.</summary>
    public static readonly Guid SourceReaderD3DManager = new("ec822da2-e1e9-4b29-a0d8-563c719f5269");

    /// <summary>MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING</summary>
    public static readonly Guid EnableAdvancedVideoProcessing = new("0f81da2c-b537-4672-a8b2-a681b17307a3");

    /// <summary>MF_SOURCE_READER_DISABLE_DXVA</summary>
    public static readonly Guid DisableDxva = new("aa456cfd-3943-4a1e-a77d-1838c0ea2e35");

    // --- Capture device enumeration ------------------------------------------
    public static readonly Guid DeviceSourceType = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");
    public static readonly Guid DeviceSourceTypeVidcap = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");
    public static readonly Guid DeviceFriendlyName = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");
    public static readonly Guid DeviceSymbolicLink = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");
    public static readonly Guid DeviceMaxBuffers = new("7dd9b730-4f2d-41d5-8f95-0cc9a912ba26");

    // --- Media type attributes ------------------------------------------------
    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    /// <summary>MF_MT_FRAME_SIZE — UINT64, width in the high 32 bits, height in the low 32.</summary>
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    /// <summary>MF_MT_FRAME_RATE — UINT64, numerator high, denominator low.</summary>
    public static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid FrameRateRangeMin = new("d2e7558c-dc1f-403f-9a72-d28bb1eb3b5e");
    public static readonly Guid FrameRateRangeMax = new("e3371d41-b4cf-4a05-bd4e-20b88bb2c4d6");
    public static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid AllSamplesIndependent = new("c9173739-5e56-461c-b713-46fb995cb95f");
    public static readonly Guid SampleSize = new("dad3ab78-1990-408b-bce2-eba673dacc10");
    public static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    public static readonly Guid MajorTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");

    /// <summary>IID_ID3D11Texture2D, for IMFDXGIBuffer::GetResource.</summary>
    public static readonly Guid ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    /// <summary>Names for attribute GUIDs we recognise; anything else is printed raw.</summary>
    public static readonly IReadOnlyDictionary<Guid, string> KnownNames = new Dictionary<Guid, string>
    {
        [MajorType] = "MF_MT_MAJOR_TYPE",
        [Subtype] = "MF_MT_SUBTYPE",
        [FrameSize] = "MF_MT_FRAME_SIZE",
        [FrameRate] = "MF_MT_FRAME_RATE",
        [FrameRateRangeMin] = "MF_MT_FRAME_RATE_RANGE_MIN",
        [FrameRateRangeMax] = "MF_MT_FRAME_RATE_RANGE_MAX",
        [InterlaceMode] = "MF_MT_INTERLACE_MODE",
        [AllSamplesIndependent] = "MF_MT_ALL_SAMPLES_INDEPENDENT",
        [SampleSize] = "MF_MT_SAMPLE_SIZE",
        [DefaultStride] = "MF_MT_DEFAULT_STRIDE",

        // Colour description. These four were identified from the values the device
        // reports rather than from the SDK headers, so they are labelled with a "?".
        // Each observed value matches the documented enum for that attribute
        // (primaries=2 BT709, matrix=1 BT709, range=2 16-235, transfer=6 BT709),
        // which is what makes the mapping credible — confirm before relying on it.
        [new Guid("dbfbe4d7-0740-4ee0-8192-850ab0e21935")] = "MF_MT_VIDEO_PRIMARIES?",
        [new Guid("3e23d450-2c75-4d25-a00e-b91670d12327")] = "MF_MT_YUV_MATRIX?",
        [new Guid("c21b8ee5-b956-4071-8daf-325edf5cab11")] = "MF_MT_VIDEO_NOMINAL_RANGE?",
        [new Guid("65df2370-c773-4c33-aa64-843e068efb0c")] = "MF_MT_TRANSFER_FUNCTION?",
    };

    public static (uint High, uint Low) Unpack(ulong packed) => ((uint)(packed >> 32), (uint)(packed & 0xFFFFFFFF));

    public static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;
}
