using SharpGen.Runtime;
using Rayxel.Diagnostics;
using Vortice.MediaFoundation;

namespace Rayxel.Capture;

/// <summary>Why a device can or cannot be used right now.</summary>
public enum DeviceAvailability
{
    /// <summary>Opened, and has at least one mode we can put on screen.</summary>
    Ready,

    /// <summary>Something else owns it. Only one application at a time may hold a capture source.</summary>
    Busy,

    /// <summary>Opened, but exposes nothing this app can render.</summary>
    Unusable,

    /// <summary>Present in the enumeration and then refused to open, for a reason of its own.</summary>
    Unreadable
}

/// <summary>Everything learned about one device by opening it and reading its mode list.</summary>
public sealed record DeviceProbe(
    CaptureDeviceInfo Device,
    DeviceAvailability Availability,
    List<CaptureMode> Modes,
    LinkBudget Link,
    string? Problem)
{
    public bool IsReady => Availability == DeviceAvailability.Ready;

    public IEnumerable<CaptureMode> Renderable => Modes.Where(m => m.IsRenderable);

    /// <summary>The mode the whole project is aiming at: no codec in the path, fastest, then largest.</summary>
    public CaptureMode? BestRaw => Renderable
        .Where(m => m.Transport == WireTransport.Raw)
        .OrderByDescending(m => m.MaxFps)
        .ThenByDescending(m => m.Width * m.Height)
        .FirstOrDefault();

    /// <summary>True when the device can hand us 10-bit pixels, which is what HDR capture needs.</summary>
    public bool SupportsTenBit => Renderable.Any(m => m.Format.IsHighBitDepth);

    /// <summary>
    /// Raw pixels per second the device can actually deliver. The one number that ranks
    /// two unlike cards against each other, because it is what a raw mode costs the wire.
    /// </summary>
    public double RawPixelRate => Renderable
        .Where(m => m.Transport == WireTransport.Raw)
        .Select(m => m.Width * (double)m.Height * m.MaxFps)
        .DefaultIfEmpty(0)
        .Max();

    public int Count(WireTransport t) => Modes.Count(m => m.Transport == t);

    /// <summary>
    /// What the wire actually carries, in MiB/s — the divisor for a mode's transfer time.
    /// <para>
    /// Seeded from the inferred ceiling and replaced with the figure from the device's own
    /// USB descriptors when those can be read. The two differ by about 13% and the
    /// descriptor one is right: the inferred ceiling is the byte rate of the fastest mode
    /// offered, which is where the wire ran out, not the speed of the wire itself. Measured
    /// end to end the true figure came out at ~400 MiB/s against an inferred 356.
    /// </para>
    /// </summary>
    public double WireMiBs { get; set; }

    /// <summary>True when the device answered the hub IOCTLs and its own format list was read.</summary>
    public bool DescriptorsRead { get; set; }

    /// <summary>
    /// Modes whose transport the descriptors overruled — where the two detectors, reasoning
    /// about a list Windows composed, disagreed with what the hardware says it has.
    /// </summary>
    public int DescriptorCorrections { get; set; }

    /// <summary>Modes the hardware itself settles, rather than either detector inferring.</summary>
    public int DescriptorBacked => Modes.Count(m => m.Evidence.HasFlag(TransportEvidence.Descriptor));

    /// <summary>
    /// Modes only the link-budget detector caught: they claim to be uncompressed and the
    /// capture stack gave no structural hint, but the wire cannot carry them. Worth saying
    /// out loud, because it is the case where we are reasoning rather than observing.
    /// </summary>
    public int BandwidthOnlyDetections => Modes.Count(m => m.Evidence == TransportEvidence.Bandwidth);
}

/// <summary>
/// Enumerates capture devices and the media types they expose. Kept separate from
/// the frame pump so the mode list can be inspected without opening a stream.
/// </summary>
public static class CaptureDevice
{
    private static bool _started;

    public static void EnsureStarted()
    {
        if (_started) return;
        MediaFactory.MFStartup(false).CheckError();
        _started = true;
        Log.Debug("MFStartup ok");
    }

    public static List<CaptureDeviceInfo> Enumerate()
    {
        EnsureStarted();
        var result = new List<CaptureDeviceInfo>();

        using var collection = MediaFactory.MFEnumVideoDeviceSources();
        int i = 0;
        foreach (var activate in collection)
        {
            string name = TryGetString(activate, MfGuids.DeviceFriendlyName) ?? "(unnamed)";
            string link = TryGetString(activate, MfGuids.DeviceSymbolicLink) ?? "";
            result.Add(new CaptureDeviceInfo(i++, name, link));
            activate.Dispose();
        }
        return result;
    }

    /// <summary>
    /// Picks a device from a command-line selector: an interface path, a name fragment, or
    /// an enumeration ordinal. Null selector means "decide for me".
    /// </summary>
    public static CaptureDeviceInfo? Resolve(string? selector, IReadOnlyList<CaptureDeviceInfo> devices)
    {
        if (devices.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(selector)) return null;

        var exact = devices.FirstOrDefault(d =>
            string.Equals(d.SymbolicLink, selector, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        // An ordinal only means anything against the list it was printed from, so it is
        // accepted but resolved here and never stored.
        if (int.TryParse(selector, out int ordinal))
            return devices.FirstOrDefault(d => d.Ordinal == ordinal);

        return devices.FirstOrDefault(d =>
            d.FriendlyName.Contains(selector, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Opens each candidate in turn and returns the one with the most raw throughput.
    /// <para>
    /// Software cameras are left alone unless nothing physical works: they advertise modes
    /// they have no wire for, so they would win a capability comparison they have no
    /// business being in.
    /// </para>
    /// </summary>
    public static (DeviceProbe? Chosen, List<DeviceProbe> Examined) ChooseBest(
        IReadOnlyList<CaptureDeviceInfo> devices)
    {
        var examined = new List<DeviceProbe>();

        foreach (var group in new[] { devices.Where(d => !d.IsVirtual), devices.Where(d => d.IsVirtual) })
        {
            foreach (var d in group) examined.Add(Probe(d));

            var best = examined.Where(p => p.IsReady)
                               .OrderByDescending(p => p.RawPixelRate)
                               .ThenByDescending(p => p.Renderable.Count())
                               .FirstOrDefault();
            if (best is not null) return (best, examined);
        }

        return (null, examined);
    }

    /// <summary>
    /// Opens a device, reads its modes, works out its link budget, and closes it again.
    /// Never throws: a device that will not open is a state to display, not a crash.
    /// </summary>
    public static DeviceProbe Probe(CaptureDeviceInfo device)
    {
        try
        {
            List<CaptureMode> modes;
            using (var source = OpenSource(device))
            using (var reader = CreateReader(source))
                modes = EnumerateModes(reader, applyLinkBudget: !device.IsVirtual);

            var link = LinkBudget.Measure(modes);
            var renderable = modes.Count(m => m.IsRenderable);

            return new DeviceProbe(
                device,
                renderable > 0 ? DeviceAvailability.Ready : DeviceAvailability.Unusable,
                modes,
                link,
                renderable > 0 ? null : $"{modes.Count} modes, none in a format this app can render");
        }
        catch (Exception ex)
        {
            var (availability, problem) = Classify(ex);
            Log.Warn($"probe of '{device.FriendlyName}' failed: {problem}");
            return new DeviceProbe(device, availability, [], LinkBudget.Unknown, problem);
        }
    }

    // HRESULT_FROM_WIN32 of well-known Win32 errors - mechanical, so these are certain.
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);
    private const int ERROR_SHARING_VIOLATION = unchecked((int)0x80070020);
    private const int ERROR_BUSY = unchecked((int)0x800700AA);

    /// <summary>
    /// MF_E_HW_MFT_FAILED_START_STREAMING. Observed directly: with one Rayxel instance
    /// streaming, a second one opens the same card and configures a media type without
    /// complaint, then fails every read with this. The device is not refused - the
    /// hardware simply will not feed two consumers.
    /// </summary>
    private const int MF_E_HW_MFT_FAILED_START_STREAMING = unchecked((int)0xC00D3704);

    private static (DeviceAvailability, string) Classify(Exception ex)
    {
        if (ex is not SharpGenException sg)
            return (DeviceAvailability.Unreadable, ex.Message);

        // Any code not named here reports its own hex, so an unrecognised failure is
        // still diagnosable and never gets described as something it is not.
        int code = sg.ResultCode.Code;
        return code switch
        {
            E_ACCESSDENIED => (DeviceAvailability.Busy,
                "access denied - another application holds this device, or Windows camera "
                + "privacy settings are blocking it"),
            ERROR_SHARING_VIOLATION or ERROR_BUSY or MF_E_HW_MFT_FAILED_START_STREAMING =>
                (DeviceAvailability.Busy, "in use by another application"),
            _ => (DeviceAvailability.Unreadable, $"0x{code:X8}: {sg.Message}")
        };
    }

    /// <summary>
    /// Activates the media source for a device. Caller owns the result.
    /// <para>
    /// Matched on the interface path, never on the enumeration ordinal: the ordinal is a
    /// position in a list that moves the moment anything is plugged in, unplugged or woken,
    /// and using it to reopen a device is how you end up streaming the wrong camera.
    /// </para>
    /// </summary>
    public static IMFMediaSource OpenSource(CaptureDeviceInfo device)
    {
        EnsureStarted();
        using var collection = MediaFactory.MFEnumVideoDeviceSources();

        foreach (var activate in collection)
        {
            var link = TryGetString(activate, MfGuids.DeviceSymbolicLink) ?? "";
            if (!string.Equals(link, device.SymbolicLink, StringComparison.OrdinalIgnoreCase))
            {
                activate.Dispose();
                continue;
            }

            var source = activate.ActivateObject<IMFMediaSource>();
            activate.Dispose();
            Log.Debug($"activated source for '{device.FriendlyName}' ({device.ShortId})");
            return source;
        }

        throw new InvalidOperationException(
            $"'{device.FriendlyName}' is no longer present - it was unplugged or claimed by another application");
    }

    /// <summary>
    /// Creates a source reader configured for minimum latency. Passing a DXGI device
    /// manager makes samples arrive as D3D11 textures instead of system-memory copies.
    /// </summary>
    public static IMFSourceReader CreateReader(IMFMediaSource source, IMFDXGIDeviceManager? dxgiManager = null)
    {
        var attrs = MediaFactory.MFCreateAttributes(8);

        // Trims MF's internal queueing; without it the reader buffers for smoothness.
        attrs.Set(MfGuids.LowLatency, true);

        if (dxgiManager is not null)
        {
            attrs.Set(MfGuids.SourceReaderD3DManager, dxgiManager);
            attrs.Set(MfGuids.EnableAdvancedVideoProcessing, true);
            Log.Debug("source reader: D3D manager attached, advanced video processing on");
        }

        var reader = MediaFactory.MFCreateSourceReaderFromMediaSource(source, attrs);
        attrs.Dispose();

        reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
        return reader;
    }

    /// <summary>
    /// Reads every native media type off the first video stream.
    /// Enumeration ends when MF reports MF_E_NO_MORE_TYPES.
    /// </summary>
    /// <param name="applyLinkBudget">
    /// False for software devices. They have no wire, so a payload that "exceeds the link"
    /// exceeds nothing, and the physical detector would invent codecs that are not there.
    /// </param>
    public static List<CaptureMode> EnumerateModes(IMFSourceReader reader, bool applyLinkBudget = true)
    {
        var modes = new List<CaptureMode>();
        for (int index = 0; ; index++)
        {
            IMFMediaType type;
            try
            {
                type = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, index);
            }
            catch (SharpGenException)
            {
                break; // MF_E_NO_MORE_TYPES
            }

            using (type)
            {
                var mode = Describe(index, type);
                if (mode is not null) modes.Add(mode);
            }
        }
        return ClassifyTransport(modes, applyLinkBudget);
    }

    /// <summary>
    /// Works out what really travels over the wire for each mode, using two independent
    /// detectors so the answer does not depend on one vendor's behaviour.
    /// <para>
    /// <b>Structure.</b> The capture stack publishes the <em>decoded</em> output of a
    /// compressed type as an extra native type, so an NV12 entry can be a codec on the
    /// wire. The two are indistinguishable by subtype, but the synthesized entry is
    /// emitted directly before the compressed type it came from, at the same geometry and
    /// rate. That adjacency is exact where it holds.
    /// </para>
    /// <para>
    /// <b>Physics.</b> A mode whose uncompressed payload exceeds the link budget the
    /// device's own other modes reveal cannot be uncompressed, whatever order its types
    /// were published in. This catches hardware whose publishing order we have never seen.
    /// </para>
    /// </summary>
    public static List<CaptureMode> ClassifyTransport(List<CaptureMode> modes, bool applyLinkBudget = true)
    {
        var byIndex = modes.ToDictionary(m => m.TypeIndex);

        for (int i = 0; i < modes.Count; i++)
        {
            var m = modes[i];
            if (m.IsCompressed)
            {
                modes[i] = m with { Transport = WireTransport.Compressed, WireCodec = m.Format.Name };
                continue;
            }

            var twin = byIndex.TryGetValue(m.TypeIndex + 1, out var next)
                       && next.IsCompressed
                       && next.Width == m.Width
                       && next.Height == m.Height
                       && Math.Abs(next.MaxFps - m.MaxFps) < 0.01
                ? next
                : null;

            modes[i] = twin is null
                ? m with { Transport = WireTransport.Raw }
                : m with
                {
                    Transport = WireTransport.CompressedDecoded,
                    Evidence = TransportEvidence.Adjacency,
                    WireCodec = twin.Format.Name
                };
        }

        // The budget is derived from what survived as raw. A single mis-read mode cannot
        // set it, because a budget only counts as known when two different geometries
        // agree on the same byte rate.
        var budget = applyLinkBudget ? LinkBudget.Measure(modes) : null;
        if (budget is { Corroborated: true })
        {
            for (int i = 0; i < modes.Count; i++)
            {
                var m = modes[i];
                if (m.IsCompressed) continue;
                if (m.MegabytesPerSecond(m.MaxFps) <= budget.SuspectAboveMiBs) continue;

                if (m.Transport == WireTransport.CompressedDecoded)
                {
                    modes[i] = m with { Evidence = m.Evidence | TransportEvidence.Bandwidth };
                    continue;
                }

                // Claims to be raw, and the device has no wire wide enough to carry it. The
                // codec is unknown here - we only know there has to be one.
                modes[i] = m with
                {
                    Transport = WireTransport.CompressedDecoded,
                    Evidence = TransportEvidence.Bandwidth
                };
            }
        }

        MarkRedundant(modes);
        return modes;
    }

    /// <summary>
    /// Replaces inference with the device's own account of itself, where it can be had.
    /// <para>
    /// Two things come out of the USB descriptors. The <b>link speed</b>, which the
    /// inferred ceiling only approximates — that figure is the byte rate of the fastest
    /// mode offered, where the wire ran out, not the speed of the wire. On the ShadowCast
    /// the two are 356 and 405 MiB/s and end-to-end timing of four frame sizes put the
    /// truth at ~400. And the <b>real format list</b>, which settles most of what the two
    /// detectors were guessing at.
    /// </para>
    /// <para>Read-only, opens no stream, and takes the device away from nobody. Everything
    /// here is best-effort: a device that will not answer keeps the inferred answers.</para>
    /// </summary>
    public static void ApplyHardwareFacts(DeviceProbe probe)
    {
        probe.WireMiBs = probe.Link.CeilingMiBs;
        if (probe.Device.IsVirtual) return;

        var usb = UsbDescriptors.Read(probe.Device);
        if (usb is null || usb.Formats.Count == 0) return;

        probe.DescriptorsRead = true;
        if (usb.PracticalBytesPerSecond > 0)
            probe.WireMiBs = usb.PracticalBytesPerSecond / (1024 * 1024);

        probe.DescriptorCorrections = CorrectFromDescriptors(probe.Modes, usb);

        // Reclassification can create or destroy a raw twin, so redundancy has to be
        // decided again against the corrected list.
        MarkRedundant(probe.Modes);
    }

    /// <summary>
    /// Rewrites the transport classification from what the hardware declares.
    /// <para>
    /// Both detectors are inferences about a list Windows composed. The configuration
    /// descriptor is the device's own statement, and comparing them per fourcc, geometry
    /// and rate gives three cases:
    /// </para>
    /// <list type="bullet">
    /// <item>Hardware declares none — every entry Windows publishes there is decoder
    /// output. Provable, and it is where the honest compressed modes live: 4K60 and
    /// 1080p240 exist only because of the codec.</item>
    /// <item>Hardware declares as many as Windows publishes — none is invented. Provable,
    /// and it can <em>clear</em> a flag the adjacency rule got wrong.</item>
    /// <item>Windows publishes more than the hardware declares — the surplus is proved and
    /// its identity is not. The count is forced; which entries carry it stays the
    /// detectors' choice.</item>
    /// </list>
    /// <para>Returns how many modes changed transport.</para>
    /// </summary>
    private static int CorrectFromDescriptors(List<CaptureMode> modes, UsbVideoInfo usb)
    {
        int changed = 0;

        // Only the uncompressed-looking types are in question. A mode whose own subtype is
        // a codec was never claiming otherwise, and nothing here can improve on that.
        foreach (var group in modes.Select((m, i) => (Mode: m, Index: i))
                                   .Where(e => !e.Mode.IsCompressed)
                                   .GroupBy(e => (e.Mode.Format.Name, e.Mode.Width, e.Mode.Height,
                                                  Fps: Math.Round(e.Mode.MaxFps, 1))))
        {
            var (name, w, h, fps) = group.Key;

            bool Uncompressed(UsbFormat f) =>
                !f.Compressed && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase);

            // A fourcc the descriptor walk could not name, or one Windows calls something
            // else, would look like "the hardware has none of these" and condemn every
            // entry. Absence of evidence, so say nothing about this format at all.
            if (!usb.Formats.Any(Uncompressed)) continue;

            int declared = usb.Formats.Count(f => Uncompressed(f)
                && f.Frames.Any(fr => fr.Width == w && fr.Height == h
                                      && fr.Rates.Any(r => Math.Abs(r - fps) < 0.6)));

            var entries = group.OrderBy(e => e.Mode.TypeIndex).ToList();
            int invented = Math.Max(0, entries.Count - declared);

            var fake = new HashSet<int>();
            if (invented >= entries.Count)
            {
                fake.UnionWith(entries.Select(e => e.Index));
            }
            else if (invented > 0)
            {
                // Keep the detectors' choice where it fits the proven count, then top up.
                // The top-up order is a tie-break, not a finding: nothing here knows which
                // of two identical-looking entries is the device's own.
                foreach (var e in entries.Where(e => e.Mode.Transport == WireTransport.CompressedDecoded))
                    if (fake.Count < invented) fake.Add(e.Index);
                foreach (var e in Enumerable.Reverse(entries))
                    if (fake.Count < invented) fake.Add(e.Index);
            }

            foreach (var e in entries)
            {
                bool shouldBeFake = fake.Contains(e.Index);
                bool isFake = e.Mode.Transport == WireTransport.CompressedDecoded;

                if (shouldBeFake == isFake)
                {
                    // Already right: record that the hardware backs it, so the reason can
                    // be told apart from a guess later.
                    modes[e.Index] = e.Mode with { Evidence = e.Mode.Evidence | TransportEvidence.Descriptor };
                    continue;
                }

                modes[e.Index] = shouldBeFake
                    ? e.Mode with
                    {
                        Transport = WireTransport.CompressedDecoded,
                        Evidence = e.Mode.Evidence | TransportEvidence.Descriptor
                    }
                    : e.Mode with
                    {
                        Transport = WireTransport.Raw,
                        Evidence = TransportEvidence.Descriptor,
                        WireCodec = null
                    };
                changed++;
            }
        }

        return changed;
    }

    /// <summary>
    /// Hides the modes there is no reason to ever choose: a codec-carrying entry that has a
    /// genuine uncompressed twin at the same format, size and rate. The twin delivers the
    /// same picture without an encode on the device and a decode on the host, so the pair
    /// only makes the list twice as long and invites the worse half to be picked.
    /// <para>
    /// What survives is every compressed mode that reaches a rate no uncompressed format
    /// does — on this card 4K60, 1440p144 and 1080p240. Those exist <em>because</em> of the
    /// codec, and removing them would take away capability rather than clutter.
    /// </para>
    /// <para>
    /// This trusts the classification to have flagged the right member of a pair. Where
    /// hardware declares one stream and the capture stack publishes two, which of them is
    /// the invention is not provable from the type list — but it is the same bet the
    /// automatic mode choice already makes, and getting it backwards costs a decode rather
    /// than a broken picture.
    /// </para>
    /// </summary>
    private static void MarkRedundant(List<CaptureMode> modes)
    {
        var genuine = modes.Where(m => m.Transport == WireTransport.Raw && m.IsRenderable).ToList();

        for (int i = 0; i < modes.Count; i++)
        {
            var m = modes[i];
            if (m.Transport != WireTransport.CompressedDecoded || !m.IsRenderable) continue;

            // The twin has to cover the whole range, not just reach the same peak: a mode
            // offering 30-120 is not replaced by one that only does 120.
            bool covered = genuine.Any(o => o.Format.Name == m.Format.Name
                                            && o.Width == m.Width && o.Height == m.Height
                                            && o.MaxFps >= m.MaxFps - 0.5
                                            && o.MinFps <= m.MinFps + 0.5);
            if (covered) modes[i] = m with { IsRedundant = true };
        }
    }

    private static CaptureMode? Describe(int index, IMFMediaType type)
    {
        if (TryGetGuid(type, MfGuids.MajorType) is not { } major || major != MfGuids.MajorTypeVideo)
            return null;
        if (TryGetGuid(type, MfGuids.Subtype) is not { } subtype)
            return null;

        var format = VideoFormat.FromSubtype(subtype);

        var (w, h) = TryGetUInt64(type, MfGuids.FrameSize) is { } size
            ? MfGuids.Unpack(size)
            : (0u, 0u);
        if (w == 0 || h == 0) return null;

        double nominal = Ratio(TryGetUInt64(type, MfGuids.FrameRate));
        double min = Ratio(TryGetUInt64(type, MfGuids.FrameRateRangeMin));
        double max = Ratio(TryGetUInt64(type, MfGuids.FrameRateRangeMax));

        // Devices that report no explicit range still have one implicit rate.
        if (min <= 0) min = nominal;
        if (max <= 0) max = nominal;

        return new CaptureMode(index, format, (int)w, (int)h, nominal, min, max);
    }

    private static double Ratio(ulong? packed)
    {
        if (packed is not { } p) return 0;
        var (num, den) = MfGuids.Unpack(p);
        return den == 0 ? 0 : num / (double)den;
    }

    /// <summary>Dumps every attribute on a media type, naming the ones we recognise.</summary>
    public static void DumpAttributes(IMFMediaType type, string prefix = "    ")
    {
        uint count;
        try { count = type.Count; }
        catch (Exception ex) { Log.Warn($"{prefix}cannot read attribute count: {ex.Message}"); return; }

        for (uint i = 0; i < count; i++)
        {
            try
            {
                var value = type.GetItemByIndex(i, out Guid key);
                var name = MfGuids.KnownNames.TryGetValue(key, out var known) ? known : key.ToString();
                Log.Raw($"{prefix}{name,-32} = {FormatValue(key, value)}", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                Log.Warn($"{prefix}attribute {i} unreadable: {ex.Message}");
            }
        }
    }

    private static string FormatValue(Guid key, object? value)
    {
        if (value is null) return "(null)";

        // GetItemByIndex hands back a Variant wrapper; the payload is in .Value.
        var unwrapped = value.GetType().GetProperty("Value")?.GetValue(value) ?? value;
        var s = unwrapped.ToString() ?? "";

        // The packed UINT64 attributes are unreadable as plain numbers.
        if (key == MfGuids.FrameSize && ulong.TryParse(s, out var size))
        {
            var (w, h) = MfGuids.Unpack(size);
            return $"{w}x{h}";
        }
        if ((key == MfGuids.FrameRate || key == MfGuids.FrameRateRangeMin || key == MfGuids.FrameRateRangeMax)
            && ulong.TryParse(s, out var rate))
        {
            var (num, den) = MfGuids.Unpack(rate);
            return den == 0 ? s : $"{num}/{den} = {num / (double)den:0.###} fps";
        }
        if (key == MfGuids.Subtype && Guid.TryParse(s, out var sub))
            return $"{VideoFormat.FromSubtype(sub).Name}  ({sub})";

        return s;
    }

    private static string? TryGetString(IMFActivate activate, Guid key)
    {
        try { return activate.GetString(key); }
        catch (SharpGenException) { return null; }
    }

    private static Guid? TryGetGuid(IMFMediaType type, Guid key)
    {
        try { return type.GetGUID(key); }
        catch (SharpGenException) { return null; }
    }

    private static ulong? TryGetUInt64(IMFMediaType type, Guid key)
    {
        try { return type.GetUInt64(key); }
        catch (SharpGenException) { return null; }
    }
}
