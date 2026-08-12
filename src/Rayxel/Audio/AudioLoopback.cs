using NAudio.CoreAudioApi;
using NAudio.Wave;
using Rayxel.Diagnostics;

namespace Rayxel.Audio;

/// <summary>
/// Carries the card's HDMI audio through to a speaker, and keeps the delay bounded.
/// <para>
/// The bounding is the interesting part. Capture and playback run off two different
/// clocks that are never exactly equal, so the buffer between them drifts — slowly, but
/// in one direction, forever. Left alone it becomes a second of lag by the end of an
/// evening. Audio that is briefly interrupted is far less bad than audio that gradually
/// stops matching the picture, so when the buffer grows past its budget the excess is
/// thrown away.
/// </para>
/// </summary>
public sealed class AudioLoopback : IDisposable
{
    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private MMDevice? _captureDevice;
    private MMDevice? _renderDevice;

    /// <summary>Playback buffer we aim to keep. Below this WASAPI starts to stutter.</summary>
    private const int TargetMs = 60;

    /// <summary>Where drift is cut back. Roughly four frames at 120 fps.</summary>
    private const int MaxMs = 120;

    public bool Running { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Audio sitting between capture and playback right now: the delay we are adding.</summary>
    public double BufferedMs =>
        _buffer is null ? 0 : _buffer.BufferedDuration.TotalMilliseconds;

    public long Resyncs { get; private set; }

    public string InputName { get; private set; } = "";
    public string OutputName { get; private set; } = "";
    public string? Format { get; private set; }

    /// <summary>
    /// Starts the loop. Returns false and records why rather than throwing: no sound is a
    /// degraded app, and taking the video down with it would be a poor trade.
    /// </summary>
    public bool Start(string? captureId, string? renderId)
    {
        Stop();
        LastError = null;

        try
        {
            _captureDevice = AudioEndpoints.Open(captureId);
            if (_captureDevice is null) { LastError = "no audio input selected"; return false; }

            _renderDevice = AudioEndpoints.Open(renderId) ?? AudioEndpoints.DefaultRender();
            if (_renderDevice is null) { LastError = "no audio output available"; return false; }

            // Event-driven with a small buffer on both ends. Polling would add its own
            // interval to the delay for nothing.
            _capture = new WasapiCapture(_captureDevice, true, 20);
            _buffer = new BufferedWaveProvider(_capture.WaveFormat)
            {
                BufferDuration = TimeSpan.FromMilliseconds(500),
                // A full buffer means playback has stalled. Dropping the newest audio is
                // wrong - it is the oldest that is stale - but the resync below is what
                // actually handles that, and this stops it throwing in the meantime.
                DiscardOnBufferOverflow = true
            };

            _capture.DataAvailable += OnData;
            _capture.RecordingStopped += OnStopped;

            _output = new WasapiOut(_renderDevice, AudioClientShareMode.Shared, true, TargetMs);
            _output.Init(_buffer);

            _capture.StartRecording();
            _output.Play();

            InputName = _captureDevice.FriendlyName;
            OutputName = _renderDevice.FriendlyName;
            Format = $"{_capture.WaveFormat.SampleRate / 1000.0:0.#} kHz · {_capture.WaveFormat.Channels} ch";
            Running = true;

            Log.Info($"audio: {InputName} -> {OutputName}  ({Format}, target {TargetMs} ms)");
            return true;
        }
        catch (Exception ex)
        {
            LastError = Explain(ex);
            Log.Warn($"audio start failed: {LastError}");
            Stop();
            return false;
        }
    }

    /// <summary>
    /// Turns the WASAPI HRESULTs that actually happen into something a person can act on.
    /// A bare 0x88890004 tells the user nothing about the console having gone to sleep.
    /// </summary>
    private static string Explain(Exception ex)
    {
        var code = (uint)ex.HResult;
        return code switch
        {
            0x88890004 => "the audio endpoint went away (0x88890004) - "
                          + "the console is probably asleep or the HDMI cable is out",
            0x88890008 => "the device does not accept this audio format (0x88890008)",
            0x8889000A => "another application has this audio device exclusively (0x8889000A)",
            0x80070005 => "access denied - check Windows microphone privacy settings",
            _ => $"{ex.Message} (0x{code:X8})"
        };
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        try { _buffer?.AddSamples(e.Buffer, 0, e.BytesRecorded); }
        catch (Exception ex) { LastError = ex.Message; }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            LastError = e.Exception.Message;
            Log.Warn($"audio capture stopped: {e.Exception.Message}");
        }
        Running = false;
    }

    /// <summary>
    /// Cuts the buffer back when the two clocks have drifted apart. Called from the frame
    /// loop; costs nothing when there is nothing to do.
    /// </summary>
    public void TrimDrift()
    {
        if (!Running || _buffer is null) return;
        if (_buffer.BufferedDuration.TotalMilliseconds <= MaxMs) return;

        // Everything buffered is by definition already late, so there is nothing worth
        // keeping. A short gap now beats a permanent offset from the picture.
        _buffer.ClearBuffer();
        Resyncs++;
        Log.Debug($"audio resync #{Resyncs}: buffer exceeded {MaxMs} ms");
    }

    public void Stop()
    {
        Running = false;
        try
        {
            if (_capture is not null)
            {
                _capture.DataAvailable -= OnData;
                _capture.RecordingStopped -= OnStopped;
                _capture.StopRecording();
                _capture.Dispose();
            }
        }
        catch (Exception ex) { Log.Debug($"audio capture stop: {ex.Message}"); }
        _capture = null;

        try { _output?.Stop(); _output?.Dispose(); }
        catch (Exception ex) { Log.Debug($"audio output stop: {ex.Message}"); }
        _output = null;

        _buffer = null;
        _captureDevice?.Dispose(); _captureDevice = null;
        _renderDevice?.Dispose(); _renderDevice = null;
    }

    public void Dispose() => Stop();
}
