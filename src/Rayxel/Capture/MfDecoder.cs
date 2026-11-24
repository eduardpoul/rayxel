using SharpGen.Runtime;
using Rayxel.Diagnostics;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace Rayxel.Capture;

/// <summary>Receives each decoded picture. The texture is only valid during the call.</summary>
internal interface IPictureSink
{
    void OnPicture(ID3D11Texture2D texture, int subresource, long sampleTime);
}

/// <summary>
/// Windows' own HEVC or AV1 decoder, fed one frame at a time and decoding on the GPU
/// straight into textures on our device.
/// <para>
/// It is the same Media Foundation transform a capture card's compressed modes go through
/// inside the source reader, used directly because here the compressed frames come from
/// the network and not from a reader. Nothing is bundled: the decoder is the one the
/// "HEVC Video Extensions" or "AV1 Video Extension" package installs, and it hands the
/// bitstream to the graphics driver, so the decode itself is the GPU's.
/// </para>
/// </summary>
internal sealed class MfDecoder : IDisposable
{
    // MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_LOCALMFT | MFT_ENUM_FLAG_SORTANDFILTER
    private const uint EnumFlags = 0x1 | 0x10 | 0x40;

    private const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    private const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);

    /// <summary>MFT_FRIENDLY_NAME_Attribute</summary>
    private static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");

    /// <summary>MFVideoFormat_AV1, the FourCC 'AV01'. Vortice has no name for it.</summary>
    private static readonly Guid Av1Subtype = new("31305641-0000-0010-8000-00aa00389b71");

    private readonly IMFTransform _transform;
    private readonly string _codecName;

    public string Name { get; }

    public NetWire.Codec Codec { get; }

    /// <summary>The size the decoder says it is producing, padding included.</summary>
    public (int Width, int Height) OutputSize { get; private set; }

    /// <param name="codec">HEVC or AV1: the two Windows decodes itself.</param>
    public MfDecoder(IMFDXGIDeviceManager deviceManager, NetWire.Codec codec, int width, int height)
    {
        CaptureDevice.EnsureStarted();
        Codec = codec;
        _codecName = codec == NetWire.Codec.Av1 ? "AV1" : "HEVC";
        Guid subtype = codec == NetWire.Codec.Av1 ? Av1Subtype : VideoFormatGuids.Hevc;
        (_transform, Name) = Create(subtype);

        try
        {
            // The same switch the source reader gets. For a decoder it means "give each
            // picture back as soon as it is decoded" rather than holding some for smoothness.
            using (var attributes = _transform.Attributes)
                attributes.Set(MfGuids.LowLatency, true);

            // Hands the decoder our device. From here it decodes on the GPU and its output
            // samples are textures we can copy from without the picture ever visiting RAM.
            _transform.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(nuint)deviceManager.NativePointer);

            using (var input = MediaFactory.MFCreateMediaType())
            {
                input.Set(MfGuids.MajorType, MfGuids.MajorTypeVideo);
                input.Set(MfGuids.Subtype, subtype);
                input.Set(MfGuids.FrameSize, MfGuids.Pack((uint)width, (uint)height));
                input.Set(MfGuids.InterlaceMode, 2u); // MFVideoInterlace_Progressive
                _transform.SetInputType(0, input, 0);
            }

            SelectOutputType();

            _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
            _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        }
        catch
        {
            _transform.Dispose();
            throw;
        }
    }

    private (IMFTransform, string) Create(Guid subtype)
    {
        var wanted = new RegisterTypeInfo { GuidMajorType = MfGuids.MajorTypeVideo, GuidSubtype = subtype };
        using var found = MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoDecoder, EnumFlags, wanted, null);

        IMFTransform? transform = null;
        string name = $"{_codecName} decoder";
        foreach (var activate in found)
        {
            if (transform is null)
            {
                try
                {
                    transform = activate.ActivateObject<IMFTransform>();
                    try { name = activate.GetString(FriendlyName); } catch (SharpGenException) { }
                }
                catch (SharpGenException ex)
                {
                    Log.Debug($"an {_codecName} decoder would not start: {ex.ResultCode}");
                }
            }
            activate.Dispose();
        }

        string package = Codec == NetWire.Codec.Av1 ? "AV1 Video Extension" : "HEVC Video Extensions";
        return transform is not null
            ? (transform, name)
            : throw new InvalidOperationException(
                $"Windows has no {_codecName} decoder. Install \"{package}\" from the Microsoft Store.");
    }

    /// <summary>
    /// Picks NV12 among what the decoder offers. Called again whenever the decoder says the
    /// stream changed, which it does once at the start: the size in the bitstream, with its
    /// padding, is not known until the first parameter set has been read.
    /// </summary>
    private void SelectOutputType()
    {
        for (int index = 0; ; index++)
        {
            IMFMediaType type;
            try { type = _transform.GetOutputAvailableType(0, index); }
            catch (SharpGenException) { break; } // MF_E_NO_MORE_TYPES
            using (type)
            {
                if (type.GetGUID(MfGuids.Subtype) != VideoFormatGuids.NV12) continue;
                _transform.SetOutputType(0, type, 0);
                var (w, h) = MfGuids.Unpack(type.GetUInt64(MfGuids.FrameSize));
                OutputSize = ((int)w, (int)h);
                return;
            }
        }
        throw new InvalidOperationException($"{Name} offers no NV12 output");
    }

    /// <summary>
    /// Decodes one frame — an HEVC access unit in Annex B form, or an AV1 temporal unit of
    /// OBUs — and passes on whatever pictures come out. With a stream that never reorders,
    /// that is the same frame.
    /// </summary>
    public unsafe void Decode(ReadOnlySpan<byte> frame, long sampleTime, IPictureSink sink)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(frame.Length);
        buffer.Lock(out IntPtr memory, out _, out _);
        frame.CopyTo(new Span<byte>((void*)memory, frame.Length));
        buffer.Unlock();
        buffer.CurrentLength = frame.Length;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = sampleTime;

        _transform.ProcessInput(0, sample, 0);
        Drain(sink);
    }

    private void Drain(IPictureSink sink)
    {
        // Bounded: a decoder that keeps announcing stream changes is broken, and looping on
        // it forever would hang the thread that also has to keep the connection alive.
        for (int turn = 0; turn < 16; turn++)
        {
            var output = new OutputDataBuffer();
            Result result = _transform.ProcessOutput(default, 1, ref output, out _);
            try
            {
                if (result.Code == MF_E_TRANSFORM_NEED_MORE_INPUT) return;
                if (result.Code == MF_E_TRANSFORM_STREAM_CHANGE)
                {
                    SelectOutputType();
                    continue;
                }
                result.CheckError();
                if (output.Sample is { } picture) Deliver(picture, sink);
            }
            finally
            {
                output.Sample?.Dispose();
                output.Events?.Dispose();
            }
        }
    }

    private void Deliver(IMFSample picture, IPictureSink sink)
    {
        if (picture.BufferCount == 0) return;
        using var buffer = picture.GetBufferByIndex(0);
        using var dxgi = buffer.QueryInterfaceOrNull<IMFDXGIBuffer>();

        // A system-memory sample means the decoder fell back to software. Nothing here
        // could display it without a copy through RAM, and it would not be worth showing.
        if (dxgi is null) throw new InvalidOperationException($"the {_codecName} decoder is not decoding on the GPU");

        var pointer = dxgi.GetResource(MfGuids.ID3D11Texture2D);
        if (pointer == IntPtr.Zero) return;

        using var texture = new ID3D11Texture2D(pointer);
        sink.OnPicture(texture, (int)dxgi.SubresourceIndex, picture.SampleTime);
    }

    public void Dispose()
    {
        try { _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); }
        catch (SharpGenException) { }
        _transform.Dispose();
    }
}
