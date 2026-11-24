using System.Security.Cryptography;
using Microsoft.Win32;
using Rayxel.Link;
using Rayxel.Link.Input;
using Rayxel.Link.Media;
using Rayxel.Link.Security;
using Rayxel.Link.Sender;

namespace Rayxel.Send.Windows;

/// <summary>
/// A sender on Windows: a PC's desktop, and whatever game is on it. The pieces:
/// <list type="bullet">
/// <item><see cref="DesktopCapture"/> - a display's picture by desktop duplication, in NV12, converted on the GPU.</item>
/// <item><see cref="MfEncoder"/> - HEVC and AV1 on the GPU's own encoder through Media Foundation; <see cref="PyroWaveEncoder"/> for PyroWave.</item>
/// <item><see cref="DesktopCursor"/> - the pointer, which duplication reports beside the picture rather than in it.</item>
/// <item><see cref="SendInputDesk"/> - the viewer's keyboard and mouse. No gamepad: see <see cref="OpenPad"/>.</item>
/// </list>
/// <para>
/// The pointer is known only to the capture, and the sender asks for a cursor separately and
/// keeps it for another length of time (one per viewer, where a capture outlives viewers). So
/// the two meet in a <see cref="PointerFeed"/> that this platform owns: every capture made
/// here reports into it and every cursor made here draws from it, whichever came first.
/// </para>
/// </summary>
public sealed class WindowsPlatform : ISenderPlatform
{
    private readonly PointerFeed _pointer = new();

    public string Name { get; } = Describe();

    public string Model => "PC";

    /// <summary>The first display; see <see cref="DesktopCapture"/> for how they are counted.</summary>
    public string DefaultSource => "0";

    public string DefaultDeviceName => Environment.MachineName;

    public ICaptureSource CreateCapture(string source) => new DesktopCapture(source, _pointer);

    public ICursorSource? CreateCursor() => new DesktopCursor(_pointer);

    public bool CanEncode(Wire.Codec codec, out string why) =>
        codec == Wire.Codec.PyroWave ? PyroWaveEncoder.Available(out why) : MfEncoder.Available(codec, out why);

    public IVideoEncoder? OpenEncoder(EncoderConfig config, out string why) =>
        config.Codec == Wire.Codec.PyroWave ? PyroWaveEncoder.Open(config, out why) : MfEncoder.Open(config, out why);

    public IPadSink? OpenPad(out string why)
    {
        why = "a virtual gamepad on Windows needs the ViGEmBus driver, which this sender does not use (yet)";
        return null;
    }

    public IDeskSink? OpenDesk(out string why) => SendInputDesk.Open(out why);

    /// <summary>
    /// Keeps this program's private key the way Windows keeps such things: encrypted with
    /// DPAPI for the current user, so that the file is useless to any other account and on
    /// any other machine. To be called once, before the identity is loaded.
    /// </summary>
    public static void ProtectKeysWithDpapi()
    {
        KeyProtection.Protect = bytes => ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        KeyProtection.Unprotect = bytes => ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
    }

    /// <summary>
    /// "Windows 11 24H2 (build 26100.2605)". The product name in the registry says Windows 10
    /// on Windows 11 too, for old programs' sake, so the generation is told by the build.
    /// </summary>
    private static string Describe()
    {
        var version = Environment.OSVersion.Version;
        string product = version.Major == 10 ? version.Build >= 22000 ? "Windows 11" : "Windows 10" : $"Windows {version.Major}.{version.Minor}";
        string release = "", build = version.Build.ToString();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key?.GetValue("DisplayVersion") is string display && display.Length > 0) release = " " + display;
            if (key?.GetValue("UBR") is int revision) build += $".{revision}";
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            // The build alone, then.
        }
        return $"{product}{release} (build {build})";
    }
}
