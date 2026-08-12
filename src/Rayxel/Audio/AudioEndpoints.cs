using NAudio.CoreAudioApi;
using Rayxel.Capture;
using Rayxel.Diagnostics;

namespace Rayxel.Audio;

/// <summary>One WASAPI endpoint, reduced to what the UI needs.</summary>
public sealed record AudioEndpointInfo(string Id, string Name, bool IsDefault)
{
    public override string ToString() => Name;
}

/// <summary>
/// Finds the audio that belongs with the video.
/// <para>
/// A capture card presents its HDMI audio as a completely separate Windows recording
/// device — nothing links it to the video source except the hardware underneath. Matching
/// them by name works until someone owns two cards from the same maker, so the USB
/// vendor and product ids out of the device instance path are the real key.
/// </para>
/// </summary>
public static class AudioEndpoints
{
    public static List<AudioEndpointInfo> List(DataFlow flow)
    {
        var result = new List<AudioEndpointInfo>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string defaultId = "";
            try { using var d = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia); defaultId = d.ID; }
            catch { /* a machine with no output at all is legal, if odd */ }

            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                    result.Add(new AudioEndpointInfo(device.ID, device.FriendlyName, device.ID == defaultId));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"audio endpoint enumeration failed: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// The recording endpoint sitting on the same card as <paramref name="video"/>.
    /// <para>
    /// Matched on the <em>adapter</em> name — <c>DeviceFriendlyName</c>, which is the
    /// hardware's own name ("ShadowCast 2 Pro") rather than the composed endpoint label
    /// ("HDMI (ShadowCast 2 Pro)"). An endpoint's PnP instance id would be better still,
    /// because it identifies the individual unit, but it is simply not in an endpoint's
    /// property store: all 70 entries were checked on real hardware and none carries it.
    /// </para>
    /// <para>
    /// So two identical cards cannot be told apart here. That is what the input selector
    /// in the panel is for, and why this logs which endpoint it chose.
    /// </para>
    /// </summary>
    public static AudioEndpointInfo? MatchTo(CaptureDeviceInfo video)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();

            try
            {
                foreach (var device in endpoints)
                {
                    if (string.Equals(TryAdapterName(device), video.FriendlyName, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Info($"audio input matched to the card: {device.FriendlyName}");
                        return new AudioEndpointInfo(device.ID, device.FriendlyName, false);
                    }
                }

                // Weaker, for cards whose audio adapter is named slightly differently.
                foreach (var device in endpoints)
                {
                    if (device.FriendlyName.Contains(video.FriendlyName, StringComparison.OrdinalIgnoreCase))
                    {
                        Log.Info($"audio input matched by endpoint name: {device.FriendlyName}");
                        return new AudioEndpointInfo(device.ID, device.FriendlyName, false);
                    }
                }

                // Never fall back to "some microphone". Piping a webcam mic into the
                // speakers would be worse than the silence it replaced.
                Log.Warn($"no audio input belongs to '{video.FriendlyName}' - " +
                         "pick one in the panel if the card does carry HDMI audio");
                return null;
            }
            finally
            {
                foreach (var d in endpoints) d.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"audio input match failed: {ex.Message}");
            return null;
        }
    }

    private static string? TryAdapterName(MMDevice device)
    {
        try { return device.DeviceFriendlyName; }
        catch { return null; }
    }

    /// <summary>Opens an endpoint by id, or null when it has been unplugged since we listed it.</summary>
    public static MMDevice? Open(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDevice(id);
        }
        catch (Exception ex)
        {
            Log.Warn($"audio endpoint '{id}' unavailable: {ex.Message}");
            return null;
        }
    }

    public static MMDevice? DefaultRender()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch { return null; }
    }
}
