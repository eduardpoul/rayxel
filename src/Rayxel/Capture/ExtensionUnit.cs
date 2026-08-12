using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Rayxel.Diagnostics;

namespace Rayxel.Capture;

/// <summary>What one vendor control turned out to be, from asking rather than guessing.</summary>
public sealed record XuControl(int Selector)
{
    /// <summary>Whether the driver says it can be read, written, or both.</summary>
    public bool CanGet { get; init; }
    public bool CanSet { get; init; }

    /// <summary>Payload length in bytes, or 0 when nothing would answer.</summary>
    public int Length { get; init; }

    /// <summary>The value as it stands. Empty when the control refused to be read.</summary>
    public byte[] Current { get; init; } = [];

    public string? Error { get; init; }
}

/// <summary>
/// Reads a UVC extension unit's controls through kernel streaming.
/// <para>
/// An extension unit is the only place the class specification lets a vendor put anything
/// of its own, so whatever a card can do beyond standard UVC is behind one. What the
/// controls <em>mean</em> is the vendor's business and usually undocumented — but their
/// shape is not. Asking each selector how long it is and what it currently holds says a
/// great deal about what it could be, without a single guess.
/// </para>
/// <para>
/// <b>Read-only, deliberately.</b> Only GET and BASICSUPPORT are ever issued. A blind
/// SET_CUR into an undocumented vendor control is how an EDID gets overwritten or a
/// firmware setting corrupted, and nothing here needs to write to learn anything.
/// </para>
/// </summary>
public static class ExtensionUnit
{
    // CTL_CODE(FILE_DEVICE_KS = 0x2F, 0, METHOD_NEITHER, FILE_ANY_ACCESS)
    private const uint IoctlKsProperty = 0x002F0003;

    private const uint TypeGet = 0x00000001;
    private const uint TypeSet = 0x00000002;
    private const uint TypeBasicSupport = 0x00000200;
    private const uint TypeTopology = 0x10000000;

    private static readonly Guid TopologySet = new("720D4AC0-7533-11D0-A5D6-28DB04C10000");
    private const uint TopologyNodes = 1;

    /// <summary>The node type usbvideo.sys gives an extension unit.</summary>
    private static readonly Guid NodeTypeDevSpecific = new("941C7AC0-C559-11D0-8A2B-00A0C9255AC1");

    /// <summary>
    /// Opens the device's KS filter and reads every control of the extension unit.
    /// Returns null when the filter will not open — most often because something else is
    /// already streaming from the card.
    /// </summary>
    public static List<XuControl>? Probe(CaptureDeviceInfo device, Guid extensionGuid, int controlCount)
    {
        using var filter = CreateFile(device.SymbolicLink,
            0x80000000 | 0x40000000 /* GENERIC_READ | GENERIC_WRITE */,
            0x00000001 | 0x00000002 /* FILE_SHARE_READ | FILE_SHARE_WRITE */,
            IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);

        if (filter.IsInvalid)
        {
            Log.Warn($"cannot open the capture filter (win32 {Marshal.GetLastWin32Error()})");
            return null;
        }

        if (FindNode(filter, extensionGuid) is not { } node)
        {
            Log.Warn("the extension unit has no topology node - Windows is not exposing it");
            return null;
        }

        Log.Info($"  extension unit is topology node {node}");

        var results = new List<XuControl>();
        for (int selector = 1; selector <= controlCount; selector++)
            results.Add(ReadControl(filter, extensionGuid, node, selector));
        return results;
    }

    /// <summary>
    /// Reads the controls again at lengths already known, skipping the size ladder. For
    /// telling a control that reports live state apart from one that holds configuration,
    /// which needs several reads and not several minutes of them.
    /// </summary>
    public static List<XuControl>? Reread(CaptureDeviceInfo device, Guid extensionGuid,
                                          IReadOnlyList<XuControl> known)
    {
        using var filter = CreateFile(device.SymbolicLink,
            0x80000000 | 0x40000000, 0x00000001 | 0x00000002,
            IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (filter.IsInvalid) return null;
        if (FindNode(filter, extensionGuid) is not { } node) return null;

        var results = new List<XuControl>();
        foreach (var c in known)
        {
            if (c.Length <= 0) { results.Add(c); continue; }

            var get = new KspNode
            {
                Property = new KsProperty
                {
                    Set = extensionGuid,
                    Id = (uint)c.Selector,
                    Flags = TypeGet | TypeTopology
                },
                NodeId = node
            };
            results.Add(c with { Current = QueryNode(filter, get, c.Length) ?? [] });
        }
        return results;
    }

    /// <summary>
    /// Finds which topology node is the extension unit. usbvideo publishes one node per
    /// unit in descriptor order and an extension unit gets the device-specific type; the
    /// node id is its index in that list.
    /// </summary>
    private static uint? FindNode(SafeFileHandle filter, Guid extensionGuid)
    {
        var request = new KsProperty { Set = TopologySet, Id = TopologyNodes, Flags = TypeGet };

        // KSMULTIPLE_ITEM header first, only to learn how many nodes there are.
        var header = Query(filter, request, 8);
        if (header is null || header.Length < 8) return null;

        int size = BitConverter.ToInt32(header, 0);
        int count = BitConverter.ToInt32(header, 4);
        if (count <= 0 || size < 8 + count * 16) return null;

        var all = Query(filter, request, size);
        if (all is null || all.Length < 8 + count * 16) return null;

        for (int i = 0; i < count; i++)
        {
            var type = new Guid(all.AsSpan(8 + i * 16, 16));
            Log.Debug($"  topology node {i}: {type}");

            // The extension unit's own GUID is what a driver may report instead of the
            // generic device-specific type, so both count as a match.
            if (type == NodeTypeDevSpecific || type == extensionGuid) return (uint)i;
        }
        return null;
    }

    private static XuControl ReadControl(SafeFileHandle filter, Guid set, uint node, int selector)
    {
        var basic = new KspNode
        {
            Property = new KsProperty { Set = set, Id = (uint)selector, Flags = TypeBasicSupport | TypeTopology },
            NodeId = node
        };

        bool canGet = false, canSet = false;
        if (QueryNode(filter, basic, 4) is { Length: >= 4 } access)
        {
            uint flags = BitConverter.ToUInt32(access, 0);
            canGet = (flags & TypeGet) != 0;
            canSet = (flags & TypeSet) != 0;
        }

        var get = new KspNode
        {
            Property = new KsProperty { Set = set, Id = (uint)selector, Flags = TypeGet | TypeTopology },
            NodeId = node
        };

        // The control's length is not in the descriptor, and usbvideo validates the buffer
        // against what the device reports rather than telling us. Walking the plausible
        // sizes finds it in a few tries and costs nothing but reads.
        // Up to 8 KiB: an EDID with every extension block is 512, and a firmware or LUT
        // buffer can be larger still. The driver validates the length exactly — every
        // size below the real one comes back ERROR_INSUFFICIENT_BUFFER — so the first
        // that succeeds is the answer rather than an upper bound.
        int lastError = 0;
        foreach (int size in (int[])[1, 2, 3, 4, 6, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192])
        {
            if (QueryNode(filter, get, size) is { } value)
                return new XuControl(selector) { CanGet = canGet, CanSet = canSet, Length = size, Current = value };
            lastError = Marshal.GetLastWin32Error();
        }

        return new XuControl(selector)
        {
            CanGet = canGet,
            CanSet = canSet,
            Error = $"no length answered (win32 {lastError})"
        };
    }

    private static byte[]? Query(SafeFileHandle filter, KsProperty request, int outputSize) =>
        Send(filter, request, Marshal.SizeOf<KsProperty>(), outputSize);

    private static byte[]? QueryNode(SafeFileHandle filter, KspNode request, int outputSize) =>
        Send(filter, request, Marshal.SizeOf<KspNode>(), outputSize);

    private static byte[]? Send<T>(SafeFileHandle filter, T request, int requestSize, int outputSize)
        where T : struct
    {
        var input = Marshal.AllocHGlobal(requestSize);
        var output = Marshal.AllocHGlobal(outputSize);
        try
        {
            Marshal.StructureToPtr(request, input, false);
            for (int i = 0; i < outputSize; i++) Marshal.WriteByte(output, i, 0);

            if (!DeviceIoControl(filter, IoctlKsProperty, input, requestSize, output, outputSize,
                                 out uint returned, IntPtr.Zero))
                return null;

            var result = new byte[Math.Min(outputSize, Math.Max(0, (int)returned))];
            Marshal.Copy(output, result, 0, result.Length);
            return result.Length == 0 ? [] : result;
        }
        finally
        {
            Marshal.FreeHGlobal(input);
            Marshal.FreeHGlobal(output);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KsProperty
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    /// <summary>A property aimed at one topology node rather than the filter as a whole.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct KspNode
    {
        public KsProperty Property;
        public uint NodeId;
        public uint Reserved;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
                                                    uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code,
        IntPtr input, int inputSize, IntPtr output, int outputSize, out uint returned, IntPtr overlapped);
}
