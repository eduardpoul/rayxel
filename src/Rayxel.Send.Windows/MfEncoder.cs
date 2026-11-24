using System.Collections.Concurrent;
using Rayxel.Link;
using Rayxel.Link.Media;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace Rayxel.Send.Windows;

/// <summary>
/// HEVC and AV1 on the GPU's own encoder, through the Media Foundation transform its driver
/// installs: NVIDIA's NVENC, AMD's VCN and Intel's Quick Sync all come with one. Only
/// hardware transforms are taken - a software HEVC encoder at a game's frame rate would cost
/// more than the game.
/// <para>
/// Hardware transforms are asynchronous: they say when they want a picture and when they have
/// a bitstream, by events, and work on their own threads in between. Encode is synchronous
/// all the same - one picture in, its bitstream out, on the caller's thread - by waiting for
/// those events. A thread of this encoder's own does nothing but take them off the
/// transform's queue (IMFMediaEventGenerator::GetEvent blocks), so the caller can wait for
/// them with a timeout and a transform that hangs is reported rather than waited for
/// forever. With low-latency mode on and no B-frames every encoder gives back the frame it
/// was given before it asks for the next, and that is what this counts on.
/// </para>
/// <para>
/// Pictures go in from memory: each is copied, with the pointer laid over it on the way, into
/// a Media Foundation buffer, and the transform uploads it to the GPU itself. Handing it a
/// D3D11 texture through a DXGI device manager instead would spare that upload, but it ties
/// the encoder to a device on its own adapter, and the vendors' transforms each have their
/// own rules about which inputs they take once a device manager is set. Memory input is the
/// path all three take alike, it is what the capture contract hands over anyway, and the copy
/// costs about what the read-back before it does.
/// </para>
/// <para>
/// What comes out is made into what Rayxel's viewer decodes (see <see cref="HevcFrames"/>
/// and <see cref="Av1Frames"/>): one access unit or temporal unit a frame, with the parameter
/// sets or the sequence header in front of every keyframe.
/// </para>
/// </summary>
public sealed unsafe class MfEncoder : IVideoEncoder
{
    // MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER
    private const uint EnumFlags = 0x04 | 0x40;

    private const int NeedMoreInput = unchecked((int)0xC00D6D72);
    private const int StreamChange = unchecked((int)0xC00D6D61);

    // Media event types, from mfobjects.h.
    private const int EventError = 1, EventNeedInput = 601, EventHaveOutput = 602, EventDrainComplete = 603;
    private const int EventPumpEnded = -1;

    /// <summary>How long a picture may be inside the encoder before it is taken to be held back rather than slow.</summary>
    private const long HeldBackNs = 500_000_000;

    /// <summary>Input samples taken in turn. One is only filled again once its frame has come out, three frames later.</summary>
    private const int InputSamples = 4;

    private const int Nv12FourCC = 0x3231564E;

    private static readonly Guid Av1Subtype = new("31305641-0000-0010-8000-00aa00389b71");
    private static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MajorTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid AverageBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid Profile = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid SequenceHeader = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    private static readonly Guid VideoPrimaries = new("dbfbe4d7-0740-4ee0-8192-850ab0e21935");
    private static readonly Guid TransferFunction = new("5fb0fce9-be5c-4935-a811-ec838f8eed93");
    private static readonly Guid YuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    private static readonly Guid NominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    private static readonly Guid CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
    private static readonly Guid TransformAsync = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    private static readonly Guid TransformAsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    private static readonly Guid LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3");

    private static readonly object Gate = new();
    private static bool _started;
    private static readonly Dictionary<Wire.Codec, (bool Ok, string Why)> Probed = [];

    /// <summary>
    /// Whether this machine has a hardware encoder for <paramref name="codec"/> that Media
    /// Foundation offers. Asked of the system once and remembered: GPUs do not come and go.
    /// </summary>
    public static bool Available(Wire.Codec codec, out string why)
    {
        if (codec is not (Wire.Codec.Hevc or Wire.Codec.Av1))
        {
            why = $"Media Foundation has no {Wire.CodecName(codec)} encoder";
            return false;
        }

        lock (Gate)
        {
            if (!Probed.TryGetValue(codec, out var known))
            {
                known = Probe(codec);
                Probed[codec] = known;
            }
            why = known.Why;
            return known.Ok;
        }
    }

    /// <summary>An encoder for <paramref name="config"/>, on the first hardware transform that takes it; null with the reason.</summary>
    public static MfEncoder? Open(EncoderConfig config, out string why)
    {
        if (config.Codec is not (Wire.Codec.Hevc or Wire.Codec.Av1))
        {
            why = $"Media Foundation has no {Wire.CodecName(config.Codec)} encoder";
            return null;
        }
        if (config.Width < 2 || config.Height < 2 || ((config.Width | config.Height) & 1) != 0)
        {
            why = $"4:2:0 needs an even size, not {config.Width}x{config.Height}";
            return null;
        }
        if (!StartMediaFoundation(out why)) return null;

        string codecName = Wire.CodecName(config.Codec);
        var problems = new List<string>();
        MfEncoder? opened = null;
        using (var found = Enumerate(config.Codec))
        {
            foreach (var activate in found)
            {
                try
                {
                    if (opened is not null) continue;
                    string name = NameOf(activate, codecName);
                    try
                    {
                        opened = Create(activate, name, config);
                    }
                    catch (Exception ex) when (ex is SharpGenException or InvalidOperationException)
                    {
                        problems.Add($"{name}: {Explain(ex)}");
                        LinkLog.Debug($"encoder: {name} would not open: {Explain(ex)}");
                    }
                }
                finally
                {
                    activate.Dispose();
                }
            }
        }

        if (opened is null)
        {
            why = problems.Count == 0
                ? $"no GPU here has a hardware {codecName} encoder that Media Foundation offers"
                : $"no hardware {codecName} encoder would open: {string.Join("; ", problems)}";
            return null;
        }
        why = "";
        LinkLog.Info($"encoder: {opened.Description}");
        return opened;
    }

    private static (bool, string) Probe(Wire.Codec codec)
    {
        if (!StartMediaFoundation(out string why)) return (false, why);
        string codecName = Wire.CodecName(codec);
        var names = new List<string>();
        try
        {
            using var found = Enumerate(codec);
            foreach (var activate in found)
            {
                names.Add(NameOf(activate, codecName));
                activate.Dispose();
            }
        }
        catch (SharpGenException ex)
        {
            return (false, $"the {codecName} encoders could not be listed ({Explain(ex)})");
        }
        if (names.Count == 0) return (false, $"no GPU here has a hardware {codecName} encoder that Media Foundation offers");
        LinkLog.Debug($"encoder: hardware {codecName}: {string.Join(", ", names)}");
        return (true, "");
    }

    private static IMFActivateCollection Enumerate(Wire.Codec codec) =>
        MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder, EnumFlags,
            new RegisterTypeInfo { GuidMajorType = MajorTypeVideo, GuidSubtype = VideoFormatGuids.NV12 },
            new RegisterTypeInfo { GuidMajorType = MajorTypeVideo, GuidSubtype = codec == Wire.Codec.Av1 ? Av1Subtype : VideoFormatGuids.Hevc });

    private static string NameOf(IMFActivate activate, string codecName)
    {
        try { return activate.GetString(FriendlyName).Trim(); }
        catch (SharpGenException) { return $"a hardware {codecName} encoder"; }
    }

    private static bool StartMediaFoundation(out string why)
    {
        lock (Gate)
        {
            why = "";
            if (_started) return true;
            Result started = MediaFactory.MFStartup(false);
            if (started.Failure)
            {
                why = $"Media Foundation would not start (0x{started.Code:X8}); is this an N edition of Windows without the Media Feature Pack?";
                return false;
            }
            _started = true;
            return true;
        }
    }

    private static MfEncoder Create(IMFActivate activate, string name, EncoderConfig config)
    {
        var encoder = new MfEncoder(config, activate.ActivateObject<IMFTransform>(), name);
        try
        {
            encoder.Setup();
            return encoder;
        }
        catch
        {
            encoder.Dispose();
            throw;
        }
    }

    private static string Explain(Exception ex) => ex is SharpGenException sg ? $"0x{sg.ResultCode.Code:X8}" : ex.Message;

    // ----------------------------------------------------------------------------------

    private sealed class InputSlot(IMFSample sample, IMFMediaBuffer buffer, IMF2DBuffer picture, int length)
    {
        public readonly IMFSample Sample = sample;
        public readonly IMFMediaBuffer Buffer = buffer;
        public readonly IMF2DBuffer Picture = picture;
        public readonly int Length = length;
    }

    private enum Outcome { Frame, NeedMoreInput, StreamChanged, Failed }

    private readonly EncoderConfig _config;
    private readonly IMFTransform _transform;
    private readonly string _name;
    private readonly Guid _subtype;
    private readonly int _width, _height, _fps;
    private readonly uint _bitsPerSecond, _bufferBits;
    private readonly long _duration;

    private CodecApi? _api;
    private uint? _gop;
    private string _gopText = "";
    private bool _async, _providesSamples, _streaming, _disposed, _drainEveryFrame;
    private IMFSample? _outputSample;
    private readonly List<InputSlot> _inputs = [];
    private int _nextInput;

    private IMFMediaEventGenerator? _events;
    private Thread? _pump;
    private readonly ConcurrentQueue<(int Type, int Status)> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private int _credits;

    private long _index;
    private bool _keyframe, _keyframeOwed;
    private readonly FrameBuffer _bitstream;
    private readonly HevcFrames? _hevc;
    private readonly Av1Frames? _av1;
    private bool _saidStray, _saidNoForce, _saidIgnoredKey;

    private MfEncoder(EncoderConfig config, IMFTransform transform, string name)
    {
        _config = config;
        _transform = transform;
        _name = name;
        _subtype = config.Codec == Wire.Codec.Av1 ? Av1Subtype : VideoFormatGuids.Hevc;
        _width = config.Width;
        _height = config.Height;
        _fps = config.Fps > 0 ? config.Fps : 60;
        _bitsPerSecond = (uint)Math.Clamp((long)(config.BitrateKbps > 0 ? config.BitrateKbps : 20_000) * 1000, 100_000, uint.MaxValue);

        // The rate control's buffer: so many frames' worth of bits, which is how far one
        // frame may stray from the average. Never less than one frame's.
        double frames = config.BufferFrames > 0 ? Math.Max(1.0, config.BufferFrames) : 1.0;
        _bufferBits = (uint)Math.Min(uint.MaxValue, _bitsPerSecond / (double)_fps * frames);
        _duration = 10_000_000L / _fps;

        _bitstream = new FrameBuffer(_width * _height / 2);
        if (config.Codec == Wire.Codec.Av1) _av1 = new Av1Frames();
        else _hevc = new HevcFrames();
    }

    public Wire.Codec Codec => _config.Codec;
    public string Description { get; private set; } = "";
    public string Error { get; private set; } = "";

    private void Setup()
    {
        // An asynchronous transform refuses everything until it is told the caller knows it
        // is one.
        using (var attributes = _transform.Attributes)
        {
            _async = attributes.GetUInt32(TransformAsync, out uint isAsync).Success && isAsync != 0;
            if (_async) attributes.Set(TransformAsyncUnlock, 1u).CheckError();
            attributes.Set(LowLatency, 1u);
        }

        _api = CodecApi.Of(_transform) ??
               throw new InvalidOperationException("it has no ICodecAPI, so neither its rate control nor its keyframes can be set");

        // The settings go in before the types and again after: some transforms take rate
        // control only before their output type is set, some only once both are.
        Configure(first: true);
        SetOutputType();
        SetInputType();
        Configure(first: false);

        var info = _transform.GetOutputStreamInfo(0);
        var flags = (OutputStreamInfoFlags)info.Flags;
        _providesSamples = (flags & (OutputStreamInfoFlags.OutputStreamProvidesSamples |
                                     OutputStreamInfoFlags.OutputStreamCanProvideSamples)) != 0;
        if (!_providesSamples)
        {
            int size = Math.Max((int)info.Size, _width * _height * 3 / 2);
            _outputSample = MediaFactory.MFCreateSample();
            using var buffer = MediaFactory.MFCreateMemoryBuffer(size);
            _outputSample.AddBuffer(buffer);
        }

        for (int i = 0; i < InputSamples; i++)
        {
            var buffer = MediaFactory.MFCreate2DMediaBuffer(_width, _height, Nv12FourCC, false);
            var picture = buffer.QueryInterface<IMF2DBuffer>();
            var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            _inputs.Add(new InputSlot(sample, buffer, picture, picture.ContiguousLength));
        }

        if (_async)
        {
            _events = _transform.QueryInterface<IMFMediaEventGenerator>();
            _pump = new Thread(Pump) { Name = "encoder events", IsBackground = true };
            _pump.Start();
        }

        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
        _streaming = true;
        ReadSequenceHeader();

        string codecName = Wire.CodecName(_config.Codec);
        string colours = $"{(_config.Bt601 ? "BT.601" : "BT.709")} {(_config.FullRange ? "full" : "limited")} range";
        Description = $"{codecName} Main {_width}x{_height}, CBR {_bitsPerSecond / 1e6:0.0} Mbit/s at {_fps} fps " +
                      $"({_bufferBits / 8 / 1024.0:0} kB buffer), {_gopText}, no B-frames, {colours}, {_name}" +
                      $"{(_async ? "" : " (synchronous)")}, uploaded from memory";
    }

    /// <summary>
    /// Rate control, GOP and latency through ICodecAPI. Which of these a transform takes
    /// varies by vendor and driver; what it refuses is said once, and it encodes regardless.
    /// </summary>
    private void Configure(bool first)
    {
        var api = _api!;
        void Take(string what, int hr, bool matters = false)
        {
            if (hr >= 0 || !first) return;
            string line = $"encoder: {_name} did not take {what} (0x{hr:X8})";
            if (matters) LinkLog.Warn(line);
            else LinkLog.Debug(line);
        }

        Take("the display-remoting scenario", api.Set(CodecApi.ScenarioInfo, CodecApi.ScenarioDisplayRemoting));
        Take("low-latency mode", api.Set(CodecApi.LowLatencyMode, true), matters: true);
        Take("CBR", api.Set(CodecApi.RateControlMode, CodecApi.RateControlCbr), matters: true);
        Take("the bitrate", api.Set(CodecApi.MeanBitRate, _bitsPerSecond), matters: true);
        Take("the peak bitrate", api.Set(CodecApi.MaxBitRate, _bitsPerSecond));
        Take("the buffer size", api.Set(CodecApi.BufferSize, _bufferBits));
        Take("no B-frames", api.Set(CodecApi.BPictureCount, 0u), matters: true);

        if (_config.Gop > 0)
        {
            Take("the GOP length", api.Set(CodecApi.GopSize, (uint)_config.Gop), matters: true);
            _gopText = $"a keyframe every {_config.Gop} frames";
            return;
        }

        // "Only the first and when asked" is a GOP longer than any stream. Encoders have
        // their own ideas of the longest they take, so the longest one takes is found.
        if (_gop is { } known)
        {
            api.Set(CodecApi.GopSize, known);
            return;
        }
        if (!first) return;
        foreach (uint length in (uint[])[int.MaxValue, 65535, 32767, (uint)_fps * 3600, (uint)_fps * 600, (uint)_fps * 60])
        {
            if (api.Set(CodecApi.GopSize, length) < 0) continue;
            _gop = length;
            _gopText = length >= (uint)_fps * 3600
                ? "a keyframe first and when asked"
                : $"a keyframe when asked and at least every {length / (double)_fps:0} s";
            return;
        }
        _gopText = "keyframes as the encoder places them, and when asked";
        LinkLog.Warn($"encoder: {_name} took no GOP length; it will put keyframes where it likes");
    }

    /// <summary>
    /// The codec, size, rate and bitrate, and with them the profile and the colours. A
    /// transform that turns the type down over the last two - they are only descriptions, and
    /// not every vendor's takes them - is asked again without them before it is given up on.
    /// </summary>
    private void SetOutputType()
    {
        for (int attempt = 0; ; attempt++)
        {
            bool full = attempt == 0;
            using var type = MediaFactory.MFCreateMediaType();
            type.Set(MajorType, MajorTypeVideo);
            type.Set(Subtype, _subtype);
            type.Set(AverageBitrate, _bitsPerSecond);
            type.Set(FrameSize, Pack((uint)_width, (uint)_height));
            type.Set(FrameRate, Pack((uint)_fps, 1));
            type.Set(PixelAspectRatio, Pack(1, 1));
            type.Set(InterlaceMode, 2u); // MFVideoInterlace_Progressive
            if (full)
            {
                // eAVEncH265VProfile_Main_420_8 and eAVEncAV1VProfile_Main_420_8 are both 1.
                type.Set(Profile, 1u);
                Colours(type);
            }
            try
            {
                _transform.SetOutputType(0, type, 0);
                if (!full) LinkLog.Debug($"encoder: {_name} took its output type only without a profile and colours");
                return;
            }
            catch (SharpGenException) when (full)
            {
            }
        }
    }

    private void SetInputType()
    {
        IMFMediaType? type = null;
        for (int i = 0; type is null; i++)
        {
            IMFMediaType candidate;
            try { candidate = _transform.GetInputAvailableType(0, i); }
            catch (SharpGenException) { break; } // MF_E_NO_MORE_TYPES, or a transform that lists none

            bool nv12;
            try { nv12 = candidate.GetGUID(Subtype) == VideoFormatGuids.NV12; }
            catch (SharpGenException) { nv12 = false; }
            if (nv12) type = candidate;
            else candidate.Dispose();
        }
        if (type is null)
        {
            type = MediaFactory.MFCreateMediaType();
            type.Set(MajorType, MajorTypeVideo);
            type.Set(Subtype, VideoFormatGuids.NV12);
        }

        using (type)
        {
            type.Set(FrameSize, Pack((uint)_width, (uint)_height));
            type.Set(FrameRate, Pack((uint)_fps, 1));
            type.Set(PixelAspectRatio, Pack(1, 1));
            type.Set(InterlaceMode, 2u);
            Colours(type);
            _transform.SetInputType(0, type, 0);
        }
    }

    /// <summary>
    /// How the pictures' colours are coded, on both types. The transforms that write a colour
    /// description into the stream take it from here; the viewer converts by BT.709 limited
    /// range either way, which is what the capture makes.
    /// </summary>
    private void Colours(IMFMediaType type)
    {
        type.Set(VideoPrimaries, _config.Bt601 ? 5u : 2u);      // MFVideoPrimaries_SMPTE170M, _BT709
        type.Set(TransferFunction, 5u);                         // MFVideoTransFunc_709
        type.Set(YuvMatrix, _config.Bt601 ? 2u : 1u);           // MFVideoTransferMatrix_BT601, _BT709
        type.Set(NominalRange, _config.FullRange ? 1u : 2u);    // MFNominalRange_0_255, _16_235
    }

    private static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    /// <summary>The output type's MF_MT_MPEG_SEQUENCE_HEADER, when the transform has put one there by now.</summary>
    private void ReadSequenceHeader()
    {
        try
        {
            using var type = _transform.GetOutputCurrentType(0);
            byte[] blob = type.GetBlob(SequenceHeader);
            if (blob.Length == 0) return;
            _hevc?.TakeSequenceHeader(blob);
            _av1?.TakeSequenceHeader(blob);
        }
        catch (SharpGenException)
        {
            // Not there (yet): the first keyframe will bring them.
        }
    }

    // ----------------------------------------------------------------------------------

    public bool Encode(in CapturedFrame frame, bool forceKeyframe, CursorOverlay? cursor, out EncodedFrame encoded)
    {
        encoded = default;
        if (!_streaming)
        {
            Error = "the encoder has been closed";
            return false;
        }
        if (frame.Width != _width || frame.Height != _height || frame.Length < frame.Needed)
        {
            Error = $"a {frame.Width}x{frame.Height} picture for an encoder opened for {_width}x{_height}";
            return false;
        }

        long start = Clock.NowNs;
        try
        {
            if (_async && !WaitForInputRequest()) return false;

            var slot = _inputs[_nextInput];
            _nextInput = (_nextInput + 1) % _inputs.Count;
            long fill = Clock.NowNs;
            Fill(slot, frame, cursor);
            long uploadNs = Clock.NowNs - fill;

            bool wanted = forceKeyframe || _keyframeOwed || _index == 0;
            if (wanted && _index > 0)
            {
                int hr = _api!.Set(CodecApi.ForceKeyFrame, 1u);
                if (hr < 0 && !_saidNoForce)
                {
                    _saidNoForce = true;
                    LinkLog.Warn($"encoder: {_name} refused a keyframe request (0x{hr:X8}); a viewer that lost a frame waits for the next one it makes");
                }
            }
            _keyframeOwed = false;

            slot.Sample.SampleTime = _index * _duration;
            slot.Sample.SampleDuration = _duration;
            _transform.ProcessInput(0, slot.Sample, 0);
            if (_async) _credits--;
            _index++;

            if (!TakeOutput()) return false;

            if (wanted && !_keyframe && !_saidIgnoredKey)
            {
                _saidIgnoredKey = true;
                LinkLog.Warn($"encoder: {_name} was asked for a keyframe and made none");
            }
            if (_bitstream.Length == 0)
            {
                Error = $"{_name} gave back an empty bitstream";
                return false;
            }
            encoded = new EncodedFrame(_bitstream.Pointer, _bitstream.Length, _keyframe, uploadNs, Clock.NowNs - start - uploadNs);
            return true;
        }
        catch (Exception ex) when (ex is SharpGenException or InvalidOperationException)
        {
            Error = $"{_name} failed: {Explain(ex)}";
            return false;
        }
    }

    /// <summary>The picture into the next input sample's buffer, with the pointer laid over it.</summary>
    private void Fill(InputSlot slot, in CapturedFrame frame, CursorOverlay? cursor)
    {
        slot.Picture.Lock2D(out IntPtr scanline0, out int pitch);
        try
        {
            // A 2D buffer's NV12 is its luma rows and then its chroma rows, at one pitch.
            byte* luma = (byte*)scanline0;
            CursorOverlay.CopyNv12(frame, luma, pitch, luma + (long)pitch * _height, pitch, cursor);
        }
        finally
        {
            slot.Picture.Unlock2D();
        }
        slot.Buffer.CurrentLength = slot.Length;
    }

    /// <summary>Waits until the transform has asked for a picture. One asked for earlier and not yet given counts.</summary>
    private bool WaitForInputRequest()
    {
        long deadline = Clock.NowNs + WaitNs();
        while (_credits <= 0)
        {
            if (!NextEvent(deadline, out var e))
            {
                Error = $"{_name} asked for no picture within {WaitNs() / 1_000_000} ms";
                return false;
            }
            if (!Handle(e, out bool haveOutput)) return false;
            if (!haveOutput) continue;

            // Output that nothing is waiting for: a frame came out twice, or late. It cannot
            // go out after the frame that follows it, so it is dropped, and the next frame is
            // made a keyframe so that nothing refers to it.
            if (ProcessOutput() == Outcome.Frame)
            {
                _keyframeOwed = true;
                if (!_saidStray)
                {
                    _saidStray = true;
                    LinkLog.Warn($"encoder: {_name} gave back more than one bitstream for a picture; the extra is dropped");
                }
            }
        }
        return true;
    }

    /// <summary>Waits for the bitstream of the picture just given and puts it into the frame buffer.</summary>
    private bool TakeOutput()
    {
        if (!_async)
        {
            for (int turn = 0; turn < 8; turn++)
            {
                switch (ProcessOutput())
                {
                    case Outcome.Frame: return true;
                    case Outcome.Failed: return false;
                    case Outcome.NeedMoreInput:
                        Error = $"{_name} kept the picture back instead of encoding it at once";
                        return false;
                }
            }
            Error = $"{_name} keeps changing its output format";
            return false;
        }

        long now = Clock.NowNs, deadline = now + WaitNs(), heldBack = now + HeldBackNs;
        bool draining = _drainEveryFrame, got = false;
        if (draining) _transform.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);

        while (true)
        {
            if (!NextEvent(draining || got ? deadline : Math.Min(heldBack, deadline), out var e))
            {
                if (!draining && Clock.NowNs < deadline)
                {
                    // Long silence with a frame inside: an encoder that holds pictures back
                    // until it has more, low-latency mode or not. Draining makes it give up
                    // what it holds, so from now on it is drained after every picture.
                    _drainEveryFrame = draining = true;
                    LinkLog.Warn($"encoder: {_name} holds pictures back; it is drained after every one from now on");
                    _transform.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
                    continue;
                }
                Error = $"{_name} gave nothing back within {WaitNs() / 1_000_000} ms";
                return false;
            }

            // A drained transform takes no input until it says it is done, and asks for
            // input anew after that.
            if (e.Type == EventDrainComplete)
            {
                _credits = 0;
                if (got) return true;
                Error = $"{_name} was drained and gave nothing back";
                return false;
            }
            if (!Handle(e, out bool haveOutput)) return false;
            if (!haveOutput) continue;
            switch (ProcessOutput())
            {
                case Outcome.Frame when !draining:
                    return true;
                case Outcome.Frame:
                    got = true;
                    break;
                case Outcome.Failed:
                    return false;
            }
        }
    }

    /// <summary>The first frames may wait for the hardware to warm up; after that a frame takes milliseconds.</summary>
    private long WaitNs() => _index <= 1 ? 3_000_000_000 : 1_000_000_000;

    /// <summary>Books an event. False, with the reason in <see cref="Error"/>, when it is the transform failing.</summary>
    private bool Handle((int Type, int Status) e, out bool haveOutput)
    {
        haveOutput = false;
        switch (e.Type)
        {
            case EventNeedInput:
                _credits++;
                return true;
            case EventHaveOutput:
                haveOutput = true;
                return true;
            case EventPumpEnded:
                Error = $"{_name} stopped sending events (0x{e.Status:X8})";
                return false;
            case EventError:
                Error = $"{_name} reported an error (0x{e.Status:X8})";
                return false;
            default:
                if (e.Status < 0)
                {
                    Error = $"{_name} reported an error (event {e.Type}, 0x{e.Status:X8})";
                    return false;
                }
                return true;
        }
    }

    private bool NextEvent(long deadlineNs, out (int Type, int Status) e)
    {
        e = default;
        int ms = (int)Math.Clamp((deadlineNs - Clock.NowNs) / 1_000_000, 0, int.MaxValue);
        return _signal.Wait(ms) && _queue.TryDequeue(out e);
    }

    /// <summary>Takes the transform's events off its queue, for <see cref="NextEvent"/> to wait on.</summary>
    private void Pump()
    {
        var events = _events!;
        while (true)
        {
            (int, int) e;
            try
            {
                using var received = events.GetEvent(0);
                e = ((int)received.EventType, received.Status.Code);
            }
            catch (SharpGenException ex)
            {
                // MF_E_SHUTDOWN once the transform is shut down: the end of its events.
                _queue.Enqueue((EventPumpEnded, ex.ResultCode.Code));
                _signal.Release();
                return;
            }
            _queue.Enqueue(e);
            _signal.Release();
        }
    }

    /// <summary>One ProcessOutput. A bitstream it gives is put into the frame buffer.</summary>
    private Outcome ProcessOutput()
    {
        var buffers = new OutputDataBuffer { StreamID = 0, Sample = _providesSamples ? null! : _outputSample! };
        Result result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref buffers, out _);
        IMFSample? sample = buffers.Sample;
        try
        {
            if (result.Code == NeedMoreInput) return Outcome.NeedMoreInput;
            if (result.Code == StreamChange)
            {
                Renegotiate();
                return Outcome.StreamChanged;
            }
            if (result.Failure)
            {
                Error = $"{_name} would not hand over a bitstream (0x{result.Code:X8})";
                return Outcome.Failed;
            }
            if (sample is null) return Outcome.NeedMoreInput;
            Collect(sample);
            return Outcome.Frame;
        }
        finally
        {
            // A sample the transform made is ours to release; one we lent it is not a new reference.
            if (sample is not null && (_outputSample is null || sample.NativePointer != _outputSample.NativePointer)) sample.Dispose();
            buffers.Events?.Dispose();
        }
    }

    private void Collect(IMFSample sample)
    {
        bool clean = sample.GetUInt32(CleanPoint, out uint value).Success && value != 0;
        using var buffer = sample.ConvertToContiguousBuffer();
        buffer.Lock(out IntPtr data, out _, out int length);
        try
        {
            var bytes = new ReadOnlySpan<byte>((void*)data, length);
            _keyframe = _hevc?.Write(bytes, clean, _bitstream) ?? _av1!.Write(bytes, clean, _bitstream);
        }
        finally
        {
            buffer.Unlock();
        }
    }

    /// <summary>
    /// The transform has changed its output format, which encoders do once at the start when
    /// they settle the size they actually code. The same codec is taken again in its new form.
    /// </summary>
    private void Renegotiate()
    {
        for (int i = 0; ; i++)
        {
            IMFMediaType type;
            try { type = _transform.GetOutputAvailableType(0, i); }
            catch (SharpGenException) { break; }
            using (type)
            {
                Guid subtype;
                try { subtype = type.GetGUID(Subtype); }
                catch (SharpGenException) { continue; }
                if (subtype != _subtype) continue;
                _transform.SetOutputType(0, type, 0);
                ReadSequenceHeader();
                return;
            }
        }
        throw new InvalidOperationException("it changed its output to a format this sender does not know");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_streaming)
        {
            try
            {
                _transform.ProcessMessage(TMessageType.MessageCommandFlush, UIntPtr.Zero);
                _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero);
            }
            catch (SharpGenException)
            {
                // It is going anyway.
            }
        }
        _streaming = false;

        // Shutting an asynchronous transform down ends its event queue, which is what lets
        // the event thread out of GetEvent.
        using (var shutdown = _transform.QueryInterfaceOrNull<IMFShutdown>())
        {
            try { shutdown?.Shutdown(); }
            catch (SharpGenException) { }
        }

        bool pumped = _pump is null || _pump.Join(TimeSpan.FromSeconds(1));
        if (pumped)
        {
            _events?.Dispose();
            _signal.Dispose();
        }
        else
        {
            LinkLog.Warn($"encoder: {_name} would not shut down; its event thread is left behind");
        }
        _events = null;

        _api?.Dispose();
        _api = null;
        foreach (var slot in _inputs)
        {
            slot.Picture.Dispose();
            slot.Buffer.Dispose();
            slot.Sample.Dispose();
        }
        _inputs.Clear();
        _outputSample?.Dispose();
        _outputSample = null;
        _transform.Dispose();
    }
}
