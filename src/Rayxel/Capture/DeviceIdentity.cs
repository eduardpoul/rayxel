using System.Text.RegularExpressions;

namespace Rayxel.Capture;

/// <summary>
/// Which Windows enumerator the device hangs off. This bounds what its latency can
/// physically be before any mode is picked: a USB card is limited by its link, an
/// internal card is not, and a software camera is limited by whatever feeds it.
/// </summary>
public enum DeviceBus
{
    /// <summary>Real hardware on USB. Bandwidth is capped by the link and the cap is felt.</summary>
    Usb,

    /// <summary>An internal capture card. No USB ceiling to work around.</summary>
    PciExpress,

    /// <summary>
    /// Software: OBS Virtual Camera, NDI, phone-as-webcam bridges. It advertises modes it
    /// has no wire for, so bandwidth arithmetic against it is meaningless and its latency
    /// is set by whatever is upstream, which we cannot see.
    /// </summary>
    Virtual,

    Other
}

/// <summary>
/// What the device interface path says about the hardware behind it.
/// <para>
/// The path is the only stable handle available. Friendly names collide the instant two
/// identical cards are plugged in, and the enumeration ordinal shifts whenever anything
/// is added, removed or woken.
/// </para>
/// </summary>
public readonly record struct DeviceHardwareId(
    DeviceBus Bus,
    string Enumerator,
    int VendorId,
    int ProductId,
    int InterfaceNumber,
    string Instance)
{
    public static readonly DeviceHardwareId Unknown = new(DeviceBus.Other, "?", -1, -1, -1, "");

    public bool HasUsbId => VendorId >= 0 && ProductId >= 0;

    /// <summary>
    /// Identifies the <em>model</em>. Two of the same card share it; two different cards do
    /// not. This is the key any per-card knowledge would hang off.
    /// </summary>
    public string ModelKey => HasUsbId ? $"{Enumerator}:{VendorId:x4}:{ProductId:x4}" : Enumerator;

    public override string ToString() =>
        HasUsbId ? $"{Enumerator} {VendorId:x4}:{ProductId:x4}" : Enumerator;

    private static readonly Regex Vid = new(@"vid_([0-9a-f]{4})", RegexOptions.IgnoreCase);
    private static readonly Regex Pid = new(@"pid_([0-9a-f]{4})", RegexOptions.IgnoreCase);
    private static readonly Regex Mi = new(@"mi_([0-9a-f]{2})", RegexOptions.IgnoreCase);

    /// <summary>
    /// Pulls the hardware identity out of a device interface path, which looks like
    /// <c>\\?\usb#vid_534d&amp;pid_2109&amp;mi_00#7&amp;1a2b3c4d&amp;0&amp;0000#{guid}\global</c>.
    /// Anything unparseable degrades to <see cref="Unknown"/> rather than throwing —
    /// a device we cannot identify is still a device we can open.
    /// </summary>
    public static DeviceHardwareId Parse(string? symbolicLink)
    {
        if (string.IsNullOrWhiteSpace(symbolicLink)) return Unknown;

        const string prefix = @"\\?\";
        var body = symbolicLink.StartsWith(prefix, StringComparison.Ordinal)
            ? symbolicLink[prefix.Length..]
            : symbolicLink;

        var parts = body.Split('#');
        if (parts.Length == 0) return Unknown;

        var enumerator = parts[0].ToLowerInvariant();
        var deviceId = parts.Length > 1 ? parts[1] : "";
        var instance = parts.Length > 2 ? parts[2] : "";

        var bus = enumerator switch
        {
            "usb" or "usbstor" => DeviceBus.Usb,
            "pci" => DeviceBus.PciExpress,
            // Software devices are registered under the root or software enumerators.
            "root" or "swd" or "sw" or "umb" => DeviceBus.Virtual,
            _ => DeviceBus.Other
        };

        return new DeviceHardwareId(
            bus,
            enumerator,
            Hex(Vid, deviceId),
            Hex(Pid, deviceId),
            Hex(Mi, deviceId),
            instance);
    }

    private static int Hex(Regex pattern, string text) =>
        pattern.Match(text) is { Success: true } m
            ? Convert.ToInt32(m.Groups[1].Value, 16)
            : -1;
}

/// <summary>A video capture device as Media Foundation sees it.</summary>
/// <param name="Ordinal">
/// Position in the current enumeration. Display only — it is not an identity and must
/// never be used to reopen the device, because it moves when the device list changes.
/// </param>
public sealed record CaptureDeviceInfo(int Ordinal, string FriendlyName, string SymbolicLink)
{
    /// <summary>Parsed once: the path is fixed for the lifetime of this record.</summary>
    public DeviceHardwareId Hardware { get; } = DeviceHardwareId.Parse(SymbolicLink);

    public DeviceBus Bus => Hardware.Bus;

    /// <summary>True when nothing physical is behind it, so its mode list describes software, not a wire.</summary>
    public bool IsVirtual => Bus == DeviceBus.Virtual;

    /// <summary>
    /// Stable identity. Two identical cards produce the same friendly name and the same
    /// model key, but never the same interface path.
    /// </summary>
    public bool Is(CaptureDeviceInfo other) =>
        string.Equals(SymbolicLink, other.SymbolicLink, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Enough to tell two of the same card apart in a list, without printing the whole
    /// interface path at the user.
    /// </summary>
    public string ShortId => Hardware.Instance.Length > 0
        ? $"{Hardware}  #{Hardware.Instance}"
        : Hardware.ToString();

    public override string ToString() => FriendlyName;
}
