using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Rayxel.Diagnostics;

namespace Rayxel.Capture;

/// <summary>One format the device itself declares, from its own descriptors.</summary>
public sealed record UsbFormat(int Index, string Name, bool Compressed)
{
    public List<UsbFrame> Frames { get; } = [];
}

/// <summary>A resolution and the frame intervals the device says it supports for it.</summary>
public sealed record UsbFrame(int Index, int Width, int Height, List<double> Rates)
{
    /// <summary>Bytes the device reserves for one frame — the size it will actually send.</summary>
    public uint MaxFrameBytes { get; init; }
}

/// <summary>
/// One unit or terminal of the VideoControl interface — the device's control surface.
/// <para>
/// This is the whole list of what a UVC device will let a host ask of it. Standard
/// controls (brightness, exposure, zoom) live on the processing unit and the camera
/// terminal; anything a vendor invents has to arrive as an <em>extension unit</em>, which
/// is the only place a proprietary channel such as HDMI CEC could exist.
/// </para>
/// </summary>
public sealed record UsbControlUnit(string Kind, int Id, string Detail)
{
    /// <summary>The vendor's own GUID, for an extension unit. Null for everything else.</summary>
    public Guid? ExtensionGuid { get; init; }

    /// <summary>Named controls this unit declares, from its bmControls bitmap.</summary>
    public IReadOnlyList<string> Controls { get; init; } = [];

    /// <summary>Controls the bitmap sets that have no name in the spec — a vendor's own.</summary>
    public int UnnamedControls { get; init; }
}

/// <summary>One endpoint of the VideoStreaming interface, as the device declares it.</summary>
public sealed record UsbEndpoint(int Address, string Kind, int MaxPacketSize, int Interval)
{
    /// <summary>Extra transactions per microframe. High speed only; 0 for a plain endpoint.</summary>
    public int AdditionalTransactions { get; init; }

    /// <summary>Burst from the SuperSpeed companion, or -1 when there is no companion.</summary>
    public int Burst { get; init; } = -1;

    /// <summary>Bytes per service interval from the SuperSpeed companion. Isochronous only.</summary>
    public uint BytesPerInterval { get; init; }

    /// <summary>On the VideoControl interface rather than VideoStreaming.</summary>
    public bool OnVideoControl { get; init; }

    public bool In => (Address & 0x80) != 0;
}

/// <summary>
/// What the hardware says about itself, read straight from its USB descriptors.
/// </summary>
public sealed record UsbVideoInfo(
    int VendorId,
    int ProductId,
    string Speed,
    string UsbVersion,
    List<UsbFormat> Formats,
    double CeilingBytesPerSecond,
    string CeilingSource)
{
    /// <summary>
    /// What each independent check said about the negotiated link speed. Three sources
    /// disagree often enough that only listing them all is honest: the V1 connection
    /// query, the device descriptor's bMaxPacketSize0, and the endpoint descriptors.
    /// </summary>
    public List<string> SpeedEvidence { get; init; } = [];

    /// <summary>USB_DEVICE_SPEED: 2 high, 3 SuperSpeed, 4 SuperSpeed+.</summary>
    public int SpeedCode { get; init; }

    /// <summary>Set when the device says it can do SuperSpeed but is not running there.</summary>
    public bool DowngradedLink { get; init; }

    /// <summary>
    /// Roughly what the negotiated link can carry in payload, after line coding and protocol
    /// overhead. Deliberately approximate: the question it answers is whether a claimed mode
    /// could physically fit, not what throughput to expect.
    /// </summary>
    public double PracticalBytesPerSecond => SpeedCode switch
    {
        2 => 480e6 / 8 * 0.80,             // high speed; the 480 figure is already post-coding
        3 => 5e9 / 10 * 0.85,              // SuperSpeed, 8b/10b line coding
        4 => 10e9 / 132 * 128 / 8 * 0.85,  // SuperSpeed+, 128b/132b
        _ => 0
    };

    public List<UsbEndpoint> Endpoints { get; init; } = [];

    /// <summary>Everything the VideoControl interface declares it can be asked.</summary>
    public List<UsbControlUnit> ControlUnits { get; init; } = [];

    /// <summary>Which hub and port answered, so it can be found in Device Manager.</summary>
    public string Location { get; init; } = "";
}

/// <summary>
/// Reads a UVC device's descriptors through its USB hub, without touching the driver.
/// <para>
/// Everything else in this app learns about the card through Media Foundation, which
/// helpfully invents extra media types — an NV12 entry that is really JPEG on the wire.
/// The hub IOCTLs go around that: they hand back the configuration descriptor the device
/// itself published, so the format list is first-hand and the isochronous endpoints state
/// the real bandwidth rather than leaving it to be inferred.
/// </para>
/// <para>
/// This is read-only. It does not open the video stream, does not rebind the driver, and
/// does not stop any other application from using the camera. Displacing usbvideo.sys with
/// WinUSB would allow capturing frames this way too — and would take the device away from
/// every other program on the machine, which is why this stops at reading.
/// </para>
/// </summary>
public static class UsbDescriptors
{
    // CTL_CODE(FILE_DEVICE_USB=0x22, function, METHOD_BUFFERED, FILE_ANY_ACCESS)
    private const uint IoctlGetNodeInformation = 0x220408;                 // function 258
    private const uint IoctlGetDescriptorFromNodeConnection = 0x220410;    // function 260
    private const uint IoctlGetNodeConnectionInformationEx = 0x220448;     // function 274
    private const uint IoctlGetNodeConnectionInformationExV2 = 0x22045C;   // function 279

    private static readonly Guid UsbHubInterface = new("f18a0e88-c30c-11d0-8815-00a0c906bed8");

    /// <summary>
    /// Finds the port this capture device is plugged into and reads its descriptors.
    /// Returns null when the device cannot be located or the hub refuses — this is a
    /// diagnostic, and nothing in the app depends on it succeeding.
    /// </summary>
    public static UsbVideoInfo? Read(CaptureDeviceInfo device)
    {
        if (!device.Hardware.HasUsbId)
        {
            Log.Debug($"'{device.FriendlyName}' has no USB id; nothing to read");
            return null;
        }

        try
        {
            // A USB 3 device shows up on both the SuperSpeed hub and its USB 2 companion,
            // and the companion answers first. Taking the first match reported a 480 Mbit
            // link for a card demonstrably moving 3 Gbit — so every match is collected and
            // the fastest wins.
            UsbVideoInfo? best = null;
            int bestSpeed = -1;

            foreach (var hubPath in EnumerateHubs())
            {
                using var hub = CreateFile(hubPath, 0x40000000 /* GENERIC_WRITE */, 0x00000002 /* FILE_SHARE_WRITE */,
                                           IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
                if (hub.IsInvalid) continue;

                int ports = ReadPortCount(hub);
                for (int port = 1; port <= ports; port++)
                {
                    if (!ReadConnection(hub, port, out var conn)) continue;
                    if (conn.VendorId != device.Hardware.VendorId ||
                        conn.ProductId != device.Hardware.ProductId) continue;

                    var config = ReadConfigurationDescriptor(hub, port);
                    if (config is null) continue;

                    var (formats, endpoints, units, ceiling, source) = ParseVideoStreaming(config);
                    var link = ResolveSpeed(hub, port, conn, endpoints, config);

                    Log.Debug($"port match on hub port {port}: {SpeedName(link.Code)}, {formats.Count} formats, " +
                              $"{ceiling / (1024 * 1024):0} MiB/s declared");
                    foreach (var line in link.Evidence) Log.Debug($"  {line}");

                    if (link.Code > bestSpeed)
                    {
                        bestSpeed = link.Code;
                        best = new UsbVideoInfo(conn.VendorId, conn.ProductId, SpeedName(link.Code),
                                                Version(conn.BcdUsb), formats, ceiling, source)
                        {
                            SpeedEvidence = link.Evidence,
                            SpeedCode = link.Code,
                            DowngradedLink = link.CapableOfSuperSpeed && link.Code < 3,
                            Endpoints = endpoints,
                            ControlUnits = units,
                            Location = $"port {port} of {ShortHubName(hubPath)}",
                        };
                    }
                }
            }

            if (best is null)
                Log.Debug($"no USB port reports {device.Hardware.VendorId:x4}:{device.Hardware.ProductId:x4}");
            return best;
        }
        catch (Exception ex)
        {
            Log.Warn($"USB descriptor read failed: {ex.Message}");
            return null;
        }
    }

    // ── hub enumeration ──────────────────────────────────────────────────────────

    private static IEnumerable<string> EnumerateHubs()
    {
        var guid = UsbHubInterface;
        var set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, 0x12 /* PRESENT | DEVICEINTERFACE */);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) yield break;

        try
        {
            var data = new SpDeviceInterfaceData { cbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out uint needed, IntPtr.Zero);
                if (needed == 0) continue;

                var buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    // cbSize is the size of the fixed part only: 8 on 64-bit, not the
                    // buffer length. Getting this wrong is the classic way this call fails.
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, needed, out _, IntPtr.Zero))
                    {
                        var path = Marshal.PtrToStringUni(buffer + 4);
                        if (!string.IsNullOrEmpty(path)) yield return path;
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private static int ReadPortCount(SafeFileHandle hub)
    {
        // USB_NODE_INFORMATION: ULONG NodeType, then USB_HUB_DESCRIPTOR. Packed, so
        // bNumberOfPorts lands at offset 6.
        var buffer = new byte[96];
        if (!DeviceIoControl(hub, IoctlGetNodeInformation, buffer, buffer.Length, buffer, buffer.Length, out _, IntPtr.Zero))
            return 0;
        return buffer[6];
    }

    private static string SpeedName(int code) => code switch
    {
        0 => "low (1.5 Mbit/s)",
        1 => "full (12 Mbit/s)",
        2 => "high (480 Mbit/s)",
        3 => "SuperSpeed (5 Gbit/s)",
        4 => "SuperSpeed+ (10 Gbit/s or better)",
        _ => $"unknown ({code})"
    };

    /// <summary>bcdUSB says what the device *is*; the port speed says how it is *running*.</summary>
    private static string Version(int bcd) => $"USB {(bcd >> 8) & 0xFF}.{(bcd >> 4) & 0x0F}";

    private static string ShortHubName(string path)
    {
        // \\?\usb#root_hub30#4&1ec2f4d5&0&0#{f18a0e88-...} -> root_hub30
        var parts = path.Split('#');
        return parts.Length > 1 ? parts[1] : path;
    }

    /// <summary>What the connection query says about one port.</summary>
    private readonly record struct Connection(int VendorId, int ProductId, int BcdUsb,
                                              int V1Speed, int MaxPacketSize0);

    private static bool ReadConnection(SafeFileHandle hub, int port, out Connection conn)
    {
        conn = default;

        // Base struct is 35 bytes plus a pipe array; a generous buffer avoids caring.
        var buffer = new byte[2048];
        BitConverter.GetBytes(port).CopyTo(buffer, 0);

        if (!DeviceIoControl(hub, IoctlGetNodeConnectionInformationEx, buffer, buffer.Length,
                             buffer, buffer.Length, out _, IntPtr.Zero))
            return false;

        // ConnectionStatus at offset 31; 1 = DeviceConnected.
        if (BitConverter.ToUInt32(buffer, 31) != 1) return false;

        // USB_DEVICE_DESCRIPTOR starts at 4: bcdUSB at +2, bMaxPacketSize0 at +7,
        // idVendor at +8, idProduct at +10. Speed is the byte after the descriptor.
        conn = new Connection(
            VendorId: BitConverter.ToUInt16(buffer, 4 + 8),
            ProductId: BitConverter.ToUInt16(buffer, 4 + 10),
            BcdUsb: BitConverter.ToUInt16(buffer, 4 + 2),
            V1Speed: buffer[23],
            MaxPacketSize0: buffer[4 + 7]);
        return true;
    }

    private readonly record struct LinkVerdict(int Code, bool CapableOfSuperSpeed, List<string> Evidence);

    /// <summary>
    /// Works out how fast the port is really running, from every source that has an opinion.
    /// <para>
    /// This needs more than one source. The V1 connection query cannot express SuperSpeed —
    /// it is documented to report high speed for a SuperSpeed device — so believing it alone
    /// says 480 Mbit/s for a card visibly moving several gigabits. The V2 query exists for
    /// exactly this, but only the USB 3 hub driver answers it. The descriptors themselves are
    /// the tiebreaker and need no IOCTL at all: the USB 3 specification fixes bMaxPacketSize0
    /// at 9 (an exponent, so 512 bytes) for a device operating at SuperSpeed, against 64 at
    /// high speed, and the hub only hands back endpoint companion descriptors for a device
    /// enumerated on the SuperSpeed bus.
    /// </para>
    /// </summary>
    private static LinkVerdict ResolveSpeed(SafeFileHandle hub, int port, Connection conn,
                                            List<UsbEndpoint> endpoints, byte[] config)
    {
        var evidence = new List<string>();
        int code = conn.V1Speed;
        bool capable = false;

        evidence.Add($"connection query (V1): {SpeedName(conn.V1Speed)}" +
                     (conn.V1Speed == 2 ? " - but V1 cannot express SuperSpeed" : ""));

        // 1. The device descriptor. Decisive on its own, and free.
        if (conn.MaxPacketSize0 == 9)
        {
            code = Math.Max(code, 3);
            capable = true;
            evidence.Add("device descriptor: bMaxPacketSize0 = 9 (512 bytes) - only legal at SuperSpeed");
        }
        else
        {
            evidence.Add($"device descriptor: bMaxPacketSize0 = {conn.MaxPacketSize0} bytes - " +
                         (conn.MaxPacketSize0 == 64 ? "the high-speed value" : "below high speed"));
        }

        if (conn.BcdUsb >= 0x0300)
        {
            capable = true;
            evidence.Add($"device descriptor: bcdUSB {Version(conn.BcdUsb)} - the device is SuperSpeed capable");
        }

        // 2. Endpoint companions. The hub returns these only for a device enumerated on the
        //    SuperSpeed bus, so their presence settles how it is running, not what it can do.
        if (HasCompanionDescriptor(config))
        {
            code = Math.Max(code, 3);
            capable = true;
            evidence.Add("configuration descriptor carries SuperSpeed endpoint companions - " +
                         "the device enumerated on the SuperSpeed bus");
        }
        else
        {
            evidence.Add("configuration descriptor has no SuperSpeed endpoint companions");
        }

        foreach (var ep in endpoints.Where(e => e.Kind == "bulk"))
        {
            string reading = ep.MaxPacketSize switch
            {
                1024 => "the SuperSpeed value",
                512 => "the high-speed value",
                _ => "unexpected"
            };
            evidence.Add($"bulk endpoint 0x{ep.Address:X2}: wMaxPacketSize {ep.MaxPacketSize} - {reading}");
            if (ep.MaxPacketSize == 1024) code = Math.Max(code, 3);
        }

        // 3. The V2 query, which is the only source that can name SuperSpeed+.
        if (ReadSuperSpeed(hub, port) is { } v2)
        {
            evidence.Add(v2.Text);
            if (v2.Operating > code) code = v2.Operating;
            if (v2.Capable) capable = true;
        }
        else
        {
            evidence.Add($"V2 connection query: no answer (win32 {Marshal.GetLastWin32Error()})");
        }

        return new LinkVerdict(code, capable, evidence);
    }

    private static bool HasCompanionDescriptor(byte[] config)
    {
        for (int i = 0; i + 1 < config.Length;)
        {
            int length = config[i];
            if (length < 2 || i + length > config.Length) break;
            if (config[i + 1] == DescriptorSsCompanion) return true;
            i += length;
        }
        return false;
    }

    private readonly record struct V2Speed(int Operating, bool Capable, string Text);

    /// <summary>
    /// Asks the V2 interface whether the port is actually operating above high speed.
    /// Returns null when the hub does not answer — only the USB 3 hub driver implements this.
    /// </summary>
    private static V2Speed? ReadSuperSpeed(SafeFileHandle hub, int port)
    {
        // USB_NODE_CONNECTION_INFORMATION_EX_V2: ConnectionIndex, Length, SupportedUsbProtocols,
        // Flags. All four are ULONG, so the structure is 16 bytes.
        //
        // SupportedUsbProtocols is an *input*: the caller states which protocols it understands,
        // and the hub rejects the request outright if it is left empty. Bit 0 is USB 1.1, bit 1
        // is USB 2.0, bit 2 is USB 3.0. Omitting bit 2 is what made every earlier call come back
        // ERROR_INVALID_PARAMETER, which reads like "not supported" and is nothing of the sort.
        var buffer = new byte[16];
        BitConverter.GetBytes(port).CopyTo(buffer, 0);
        BitConverter.GetBytes(16).CopyTo(buffer, 4);
        BitConverter.GetBytes(0x04u).CopyTo(buffer, 8);   // SupportedUsbProtocols.Usb300 = 1

        if (!DeviceIoControl(hub, IoctlGetNodeConnectionInformationExV2, buffer, buffer.Length,
                             buffer, buffer.Length, out _, IntPtr.Zero))
            return null;

        // Flags is output-only, so whatever comes back is the hub's own answer. The
        // SupportedUsbProtocols field is not read back: it may simply echo the request,
        // and there is no way to tell from one call which it did.
        uint flags = BitConverter.ToUInt32(buffer, 12);

        bool atSuperSpeed = (flags & 0x01) != 0;      // operating at SuperSpeed or higher
        bool canSuperSpeed = (flags & 0x02) != 0;     // capable of SuperSpeed or higher
        bool atSuperSpeedPlus = (flags & 0x04) != 0;  // operating at SuperSpeed+ or higher
        bool canSuperSpeedPlus = (flags & 0x08) != 0; // capable of SuperSpeed+ or higher

        string text = $"V2 connection query: flags 0x{flags:X2} - operating at " +
                      (atSuperSpeedPlus ? "SuperSpeed+" : atSuperSpeed ? "SuperSpeed" : "high speed or below") +
                      ", capable of " +
                      (canSuperSpeedPlus ? "SuperSpeed+" : canSuperSpeed ? "SuperSpeed" : "high speed or below");

        return new V2Speed(atSuperSpeedPlus ? 4 : atSuperSpeed ? 3 : 0,
                           canSuperSpeed || canSuperSpeedPlus, text);
    }

    private static byte[]? ReadConfigurationDescriptor(SafeFileHandle hub, int port)
    {
        // Nine bytes first, only to learn wTotalLength, then the whole thing.
        var head = Request(hub, port, 9);
        if (head is null || head.Length < 9) return null;

        int total = BitConverter.ToUInt16(head, 2);
        if (total is <= 9 or > 65535) return null;

        return Request(hub, port, total);
    }

    private static byte[]? Request(SafeFileHandle hub, int port, int dataLength)
    {
        const int header = 12;  // ULONG ConnectionIndex + the 8-byte setup packet
        var buffer = new byte[header + dataLength];

        BitConverter.GetBytes(port).CopyTo(buffer, 0);
        buffer[4] = 0x80;                                    // device-to-host, standard, device
        buffer[5] = 0x06;                                    // GET_DESCRIPTOR
        BitConverter.GetBytes((ushort)(0x02 << 8)).CopyTo(buffer, 6);   // configuration, index 0
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);             // wIndex
        BitConverter.GetBytes((ushort)dataLength).CopyTo(buffer, 10);   // wLength

        if (!DeviceIoControl(hub, IoctlGetDescriptorFromNodeConnection, buffer, buffer.Length,
                             buffer, buffer.Length, out uint returned, IntPtr.Zero))
            return null;

        int payload = (int)returned - header;
        if (payload <= 0) return null;

        return buffer.AsSpan(header, Math.Min(payload, dataLength)).ToArray();
    }

    // ── UVC parsing ──────────────────────────────────────────────────────────────

    private const byte DescriptorInterface = 0x04;
    private const byte DescriptorEndpoint = 0x05;
    private const byte DescriptorCsInterface = 0x24;
    private const byte DescriptorSsCompanion = 0x30;

    private const byte ClassVideo = 0x0E;
    private const byte SubclassVideoControl = 0x01;
    private const byte SubclassVideoStreaming = 0x02;

    /// <summary>
    /// Walks the configuration descriptor and picks out what the VideoStreaming interface
    /// declares: its formats, their frames, and the bandwidth its isochronous endpoints
    /// are willing to carry.
    /// </summary>
    private static (List<UsbFormat> Formats, List<UsbEndpoint> Endpoints, List<UsbControlUnit> Units,
                    double Ceiling, string Source)
        ParseVideoStreaming(byte[] config)
    {
        var formats = new List<UsbFormat>();
        var endpoints = new List<UsbEndpoint>();
        var units = new List<UsbControlUnit>();
        UsbFormat? current = null;
        bool inVideoStreaming = false, inVideoControl = false;
        double ceiling = 0;
        string source = "no isochronous endpoint found";

        int i = 0;
        while (i + 1 < config.Length)
        {
            int length = config[i];
            byte type = config[i + 1];
            if (length < 2 || i + length > config.Length) break;

            var d = config.AsSpan(i, length);

            switch (type)
            {
                case DescriptorInterface:
                    bool video = length >= 8 && d[5] == ClassVideo;
                    inVideoStreaming = video && d[6] == SubclassVideoStreaming;
                    inVideoControl = video && d[6] == SubclassVideoControl;
                    break;

                case DescriptorCsInterface when inVideoControl && length >= 3:
                    // Raw bytes too: every offset in here is hand-counted against the UVC
                    // spec, and a wrong one is silent rather than loud.
                    Log.Debug($"  VC descriptor subtype 0x{d[2]:X2}: {Convert.ToHexString(d)}");
                    if (ParseControlUnit(d) is { } unit) units.Add(unit);
                    break;

                case DescriptorCsInterface when inVideoStreaming && length >= 3:
                    ParseClassSpecific(d, formats, ref current);
                    break;

                case DescriptorEndpoint when (inVideoStreaming || inVideoControl) && length >= 7:
                {
                    ushort maxPacket = BitConverter.ToUInt16(d[4..6]);
                    int size = maxPacket & 0x7FF;
                    int extra = (maxPacket >> 11) & 0x03;      // additional transactions, high speed only
                    byte interval = d[6];
                    int kind = d[3] & 0x03;

                    // VideoControl endpoints matter as much as streaming ones here: an
                    // interrupt endpoint on VideoControl is the only way a UVC device can
                    // tell the host something it was not asked, so its absence rules out
                    // anything the card would need to report unprompted.
                    endpoints.Add(new UsbEndpoint(d[2], kind switch
                    {
                        0 => "control",
                        1 => "isochronous",
                        2 => "bulk",
                        _ => "interrupt"
                    }, size, interval)
                    {
                        AdditionalTransactions = extra,
                        OnVideoControl = inVideoControl
                    });

                    // Only isochronous endpoints reserve bandwidth, so only they state a
                    // ceiling. Bulk gets whatever is left over and declares nothing.
                    if (kind != 1) break;

                    // High speed: one service interval every 125 us, so 8000 a second,
                    // divided down by bInterval.
                    double perSecond = 8000.0 / Math.Pow(2, Math.Max(0, interval - 1));
                    double bytes = size * (1 + extra) * perSecond;
                    if (bytes > ceiling)
                    {
                        ceiling = bytes;
                        source = $"isochronous endpoint, {size} bytes x{1 + extra} every " +
                                 $"{Math.Pow(2, Math.Max(0, interval - 1)) * 125:0} us";
                    }
                    break;
                }

                case DescriptorSsCompanion when inVideoStreaming && length >= 6:
                {
                    // The companion belongs to the endpoint just before it.
                    ushort perInterval = BitConverter.ToUInt16(d[4..6]);
                    if (endpoints.Count > 0)
                    {
                        int last = endpoints.Count - 1;
                        endpoints[last] = endpoints[last] with
                        {
                            Burst = d[2],                    // bMaxBurst: 0 means one packet
                            BytesPerInterval = perInterval,
                        };
                    }

                    // SuperSpeed states the real figure directly, and it supersedes the
                    // high-speed arithmetic above for the same endpoint.
                    if (perInterval > 0)
                    {
                        double bytes = perInterval * 8000.0;
                        if (bytes > ceiling)
                        {
                            ceiling = bytes;
                            source = $"SuperSpeed companion, {perInterval} bytes per 125 us interval";
                        }
                    }
                    break;
                }
            }

            i += length;
        }

        return (formats, endpoints, units, ceiling, source);
    }

    // ── VideoControl: the device's control surface ───────────────────────────────

    private static readonly string[] ProcessingControls =
    [
        "brightness", "contrast", "hue", "saturation", "sharpness", "gamma",
        "white balance temperature", "white balance component", "backlight compensation",
        "gain", "power line frequency", "hue auto", "white balance temperature auto",
        "white balance component auto", "digital multiplier", "digital multiplier limit",
        "analog video standard", "analog video lock status", "contrast auto"
    ];

    private static readonly string[] CameraControls =
    [
        "scanning mode", "auto exposure mode", "auto exposure priority",
        "exposure time absolute", "exposure time relative", "focus absolute",
        "focus relative", "iris absolute", "iris relative", "zoom absolute",
        "zoom relative", "pan/tilt absolute", "pan/tilt relative", "roll absolute",
        "roll relative", "reserved", "reserved", "focus auto", "privacy"
    ];

    /// <summary>
    /// One VideoControl unit or terminal. This is the complete list of what the device
    /// will let a host ask of it — anything not here cannot be requested at all.
    /// </summary>
    private static UsbControlUnit? ParseControlUnit(ReadOnlySpan<byte> d)
    {
        const byte VcHeader = 0x01, VcInputTerminal = 0x02, VcOutputTerminal = 0x03;
        const byte VcSelectorUnit = 0x04, VcProcessingUnit = 0x05, VcExtensionUnit = 0x06;

        switch (d[2])
        {
            case VcHeader when d.Length >= 12:
                return new UsbControlUnit("header", 0,
                    $"UVC {(d[4] << 8 | d[3]) >> 8:x}.{(d[3] & 0xFF) >> 4:x}, {d[11]} streaming interface(s)");

            case VcInputTerminal when d.Length >= 8:
            {
                int terminalType = BitConverter.ToUInt16(d[4..6]);
                var unit = new UsbControlUnit("input terminal", d[3], TerminalName(terminalType));

                // A camera terminal carries its own control bitmap: pan, tilt, zoom, focus.
                // An HDMI input has nothing to pan, so this is normally empty or absent.
                if (terminalType == 0x0201 && d.Length >= 15)
                {
                    int size = d[14];
                    var (named, unnamed) = ReadControls(d, 15, size, CameraControls);
                    return unit with { Controls = named, UnnamedControls = unnamed };
                }
                return unit;
            }

            case VcOutputTerminal when d.Length >= 9:
                return new UsbControlUnit("output terminal", d[3],
                    $"{TerminalName(BitConverter.ToUInt16(d[4..6]))}, from unit {d[7]}");

            case VcSelectorUnit when d.Length >= 5:
                return new UsbControlUnit("selector unit", d[3], $"{d[4]} input pin(s)");

            case VcProcessingUnit when d.Length >= 8:
            {
                int size = d[7];
                var (named, unnamed) = ReadControls(d, 8, size, ProcessingControls);
                return new UsbControlUnit("processing unit", d[3], $"{named.Count + unnamed} control(s)")
                {
                    Controls = named,
                    UnnamedControls = unnamed
                };
            }

            case VcExtensionUnit when d.Length >= 24:
            {
                // The only place a vendor can put anything of its own — a proprietary
                // channel over UVC has to arrive here or not at all.
                var guid = new Guid(d[4..20]);
                int pins = d[21];
                int sizeAt = 22 + pins;
                int size = sizeAt < d.Length ? d[sizeAt] : 0;
                var (_, unnamed) = ReadControls(d, sizeAt + 1, size, []);

                return new UsbControlUnit("EXTENSION UNIT", d[3],
                    $"{d[20]} control(s) declared, {unnamed} set in the bitmap")
                {
                    ExtensionGuid = guid,
                    UnnamedControls = unnamed
                };
            }
        }
        return null;
    }

    /// <summary>Reads a bmControls bitmap and names the bits the spec names.</summary>
    private static (List<string> Named, int Unnamed) ReadControls(
        ReadOnlySpan<byte> d, int at, int size, string[] names)
    {
        var named = new List<string>();
        int unnamed = 0;

        for (int b = 0; b < size && at + b < d.Length; b++)
            for (int bit = 0; bit < 8; bit++)
            {
                if ((d[at + b] & (1 << bit)) == 0) continue;
                int index = b * 8 + bit;
                if (index < names.Length && names[index] != "reserved") named.Add(names[index]);
                else unnamed++;
            }

        return (named, unnamed);
    }

    private static string TerminalName(int type) => type switch
    {
        0x0100 => "vendor specific",
        0x0101 => "streaming",
        0x0200 => "input, vendor specific",
        0x0201 => "camera",
        0x0202 => "media transport input",
        0x0300 => "output, vendor specific",
        0x0301 => "display",
        0x0302 => "media transport output",
        0x0400 => "external, vendor specific",
        0x0401 => "composite connector",
        0x0402 => "s-video connector",
        0x0403 => "component connector",
        _ => $"unknown (0x{type:X4})"
    };

    private static void ParseClassSpecific(ReadOnlySpan<byte> d, List<UsbFormat> formats, ref UsbFormat? current)
    {
        const byte FormatUncompressed = 0x04, FrameUncompressed = 0x05;
        const byte FormatMjpeg = 0x06, FrameMjpeg = 0x07;
        const byte FormatFrameBased = 0x10, FrameFrameBased = 0x11;

        switch (d[2])
        {
            case FormatUncompressed when d.Length >= 21:
                // The first four bytes of the format GUID are the FOURCC in ASCII.
                current = new UsbFormat(d[3], Fourcc(d.Slice(5, 4)), Compressed: false);
                formats.Add(current);
                break;

            case FormatFrameBased when d.Length >= 21:
                current = new UsbFormat(d[3], Fourcc(d.Slice(5, 4)), Compressed: true);
                formats.Add(current);
                break;

            case FormatMjpeg when d.Length >= 7:
                current = new UsbFormat(d[3], "MJPEG", Compressed: true);
                formats.Add(current);
                break;

            case FrameUncompressed or FrameMjpeg when d.Length >= 26:
                current?.Frames.Add(ReadFrame(d, intervalsAt: 26, hasBufferSize: true));
                break;

            case FrameFrameBased when d.Length >= 22:
                // Frame-based drops dwMaxVideoFrameBufferSize and gains dwBytesPerLine,
                // so its interval list starts elsewhere.
                current?.Frames.Add(ReadFrame(d, intervalsAt: 26, hasBufferSize: false));
                break;
        }
    }

    private static UsbFrame ReadFrame(ReadOnlySpan<byte> d, int intervalsAt, bool hasBufferSize)
    {
        int width = BitConverter.ToUInt16(d[5..7]);
        int height = BitConverter.ToUInt16(d[7..9]);
        uint bufferSize = hasBufferSize && d.Length >= 21 ? BitConverter.ToUInt32(d[17..21]) : 0;
        byte intervalType = d[25];

        var rates = new List<double>();
        if (intervalType == 0)
        {
            // Continuous: min, max, step — all in 100 ns units.
            if (d.Length >= intervalsAt + 12)
            {
                uint min = BitConverter.ToUInt32(d[intervalsAt..(intervalsAt + 4)]);
                uint max = BitConverter.ToUInt32(d[(intervalsAt + 4)..(intervalsAt + 8)]);
                if (min > 0) rates.Add(1e7 / min);
                if (max > 0 && max != min) rates.Add(1e7 / max);
            }
        }
        else
        {
            for (int n = 0; n < intervalType; n++)
            {
                int at = intervalsAt + n * 4;
                if (at + 4 > d.Length) break;
                uint interval = BitConverter.ToUInt32(d[at..(at + 4)]);
                if (interval > 0) rates.Add(1e7 / interval);
            }
        }

        rates.Sort((a, b) => b.CompareTo(a));
        return new UsbFrame(d[3], width, height, rates) { MaxFrameBytes = bufferSize };
    }

    private static string Fourcc(ReadOnlySpan<byte> b)
    {
        Span<char> c = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            char ch = (char)b[i];
            if (ch is < ' ' or > '~') return "?";
            c[i] = ch;
        }
        return new string(c);
    }

    // ── interop ──────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr deviceInfo, ref Guid interfaceClass,
                                                           uint index, ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SpDeviceInterfaceData data,
        IntPtr detail, uint detailSize, out uint required, IntPtr deviceInfoData);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
                                                    uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code,
        byte[] input, int inputSize, byte[] output, int outputSize, out uint returned, IntPtr overlapped);
}
