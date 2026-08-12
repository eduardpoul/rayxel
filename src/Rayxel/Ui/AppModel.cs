using Rayxel.Audio;
using Rayxel.Capture;
using Rayxel.Render;
using Rayxel.Settings;

namespace Rayxel.Ui;

public enum Screen { Splash, Devices, App }

/// <summary>Which of the design's four states the app is in right now.</summary>
public enum AppStatus { Nominal, Compressed, Partial, NoSignal }

/// <summary>
/// Everything the interface reads, and the handful of things it can ask for.
/// <para>
/// The actions are delegates rather than methods because the work behind them touches
/// D3D and the capture device, and that has to happen on the frame loop's thread with
/// its existing error handling — not from inside a widget.
/// </para>
/// </summary>
public sealed class AppModel
{
    public required Renderer Renderer { get; init; }
    public required FramePump Pump { get; init; }

    public List<CaptureDeviceInfo> Devices { get; set; } = [];
    public Dictionary<string, DeviceProbe> Probed { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DeviceProbe? Active { get; set; }

    // Live measurements, refreshed by the frame loop every frame.
    public double AppCpuMs;
    public double GpuMs;
    public readonly FrameHistory History = new();

    // What the readout actually shows. Held still for a second at a time: at 120 fps the
    // live figures repaint 120 times a second and the last digits never stop moving, which
    // makes a panel of numbers unreadable and looks like something is wrong.
    public PumpStats Shown = new();
    public double ShownCpuMs;
    public double ShownGpuMs;
    public string Composition = "?";

    /// <summary>Takes the values the readout will show until the next tick.</summary>
    public void SampleForDisplay(string composition)
    {
        Shown = Pump.Snapshot();
        ShownCpuMs = AppCpuMs;
        ShownGpuMs = GpuMs;
        Composition = composition;
    }

    // Interface state. None of it survives a restart, by design.
    public Screen Screen = Screen.Splash;
    public bool ShowTelemetry;          // off by default, per the handoff
    public bool ShowSettings = true;
    public bool ModeTableOpen;
    public float OverlayNits = 230f;

    /// <summary>Level our own shader encodes the video at for a PQ swapchain.</summary>
    public float VideoNits = 200f;
    public double SplashElapsedMs;

    /// <summary>Diagnostic only: renders the no-signal card without unplugging anything.</summary>
    public bool ForceNoSignal;

    /// <summary>Time since the panel was last touched. It hides itself once this runs out.</summary>
    public double PanelIdleMs;

    /// <summary>Height the telemetry block took last frame, so the gear can sit clear of it.</summary>
    public float TelemetryHeight;

    /// <summary>
    /// True while the "really quit?" dialog is up. Closing by accident during a game means
    /// losing the picture mid-play, so both Esc and the Quit button ask first.
    /// </summary>
    public bool ConfirmQuit;

    public void PanelActivity() => PanelIdleMs = 0;

    public Action<int> SelectScaler = _ => { };
    public Action ToggleHdr = () => { };
    public Action<CaptureMode, double> SelectMode = (_, _) => { };
    public Action<CaptureDeviceInfo> SwitchDevice = _ => { };
    public Action ToggleBorderless = () => { };
    public Action Quit = () => { };

    /// <summary>Which of the three window modes is active.</summary>
    public Native.WindowKind WindowKind = Native.WindowKind.Windowed;

    public Action<Native.WindowKind> SetWindowKind = _ => { };

    /// <summary>
    /// Output width over source width. The number that decides whether an upscaler is doing
    /// anything at all — below 1 it is scaling down and no filter can add detail.
    /// </summary>
    public double ScaleFactor
    {
        get
        {
            var m = Pump.CurrentMode;
            return m is { Width: > 0 } ? Renderer.Width / (double)m.Width : 0;
        }
    }

    /// <summary>
    /// The design's colour rule for the scale factor: below 1 it is working downwards,
    /// 1–1.3 is marginal, above that it is doing real work.
    /// </summary>
    public System.Numerics.Vector4 ScaleColour => ScaleFactor switch
    {
        < 1.0 => Theme.Bad,
        <= 1.3 => Theme.Warn,
        _ => Theme.Alpha(Theme.TextPrimary, 0.55f)
    };

    /// <summary>`×2` when integral, `×1.79` otherwise — the design prints no trailing zeros.</summary>
    public string ScaleLabel
    {
        get
        {
            double s = Math.Round(ScaleFactor, 2);
            return s <= 0 ? "" : $"×{(Math.Abs(s - Math.Round(s)) < 0.005 ? s.ToString("0") : s.ToString("0.##"))}";
        }
    }

    // Audio. The card's HDMI input is a separate Windows device with no link to the video
    // one, so both ends are named explicitly and both are remembered.
    public AudioLoopback Audio = new();
    public List<AudioEndpointInfo> AudioInputs = [];
    public List<AudioEndpointInfo> AudioOutputs = [];
    public string? AudioInputId;
    public string? AudioOutputId;
    public bool AudioEnabled = true;
    public Action RestartAudio = () => { };

    /// <summary>Set whenever something worth remembering changes; the loop saves on a delay.</summary>
    public bool SettingsDirty;

    public void Touch() => SettingsDirty = true;

    /// <summary>Copies the live state into the object that gets written to disk.</summary>
    public void CaptureInto(AppSettings s)
    {
        s.Language = Strings.Current;
        s.Scaler = ScalerIndex;
        s.Hdr = HdrOn;
        s.NisSharpness = Renderer.Nis?.Sharpness ?? s.NisSharpness;
        s.OverlayNits = OverlayNits;
        s.VideoNits = VideoNits;
        s.ShowTelemetry = ShowTelemetry;
        s.ShowSettings = ShowSettings;
        s.Borderless = WindowKind != Native.WindowKind.Windowed;
        s.DeviceLink = Pump.Device.SymbolicLink;

        if (Pump.CurrentMode is { } m)
        {
            s.ModeWidth = m.Width;
            s.ModeHeight = m.Height;
            s.ModeFps = Pump.CurrentFps;
            s.ModeFormat = m.Format.Name;
        }

        s.AudioEnabled = AudioEnabled;
        s.AudioInputId = AudioInputId;
        s.AudioOutputId = AudioOutputId;
    }

    public double BudgetMs => Pump.CurrentFps > 0 ? 1000.0 / Pump.CurrentFps : 0;

    /// <summary>Headroom as displayed — from the held sample, so it does not flicker.</summary>
    public double HeadroomMs => BudgetMs - ShownCpuMs - ShownGpuMs;

    /// <summary>0 = own shader, 1 = NIS, 2 = the driver filter.</summary>
    public int ScalerIndex =>
        Renderer.UseNis ? 1 : Renderer.Processor?.SuperResolutionEnabled == true ? 2 : 0;

    public bool HdrOn => Renderer.Processor?.HdrEnabled == true;

    public bool DriverPathAvailable => Renderer.Processor is { Available: true };

    /// <summary>
    /// Whether SDR->HDR can be reached at all, and why not when it cannot. The design
    /// insists disabled controls explain themselves rather than merely grey out.
    /// </summary>
    public (bool Can, string Reason) HdrReachable()
    {
        if (!DriverPathAvailable) return (false, Strings.T("hdrVendor"));
        if (!Renderer.DisplaySupportsHdr) return (false, Strings.T("hdrNa"));
        if (Renderer.UseNis) return (false, Strings.T("hdrPath"));
        return (true, HdrOn ? Strings.T("hdrOn") : Strings.T("hdrOff"));
    }

    /// <summary>
    /// True when the window is not larger than the source, which makes any upscaler inert
    /// however it is toggled.
    /// </summary>
    public bool ScalerInert
    {
        get
        {
            var m = Pump.CurrentMode;
            return m is not null && m.Width > 0 && Renderer.Width <= m.Width;
        }
    }

    public AppStatus Status
    {
        get
        {
            if (Pump.State is CaptureState.NoSignal or CaptureState.Lost) return AppStatus.NoSignal;
            if (Pump.CurrentMode?.HasCodecInPath == true) return AppStatus.Compressed;
            // Composited, dropping frames or out of budget: working, but not as intended.
            if (Composition == "Composed" || HeadroomMs < 0 || Shown.FramesDropped > 0)
                return AppStatus.Partial;
            return AppStatus.Nominal;
        }
    }

    public string StatusWord => Status switch
    {
        AppStatus.Nominal => Strings.T("stNominal"),
        AppStatus.Compressed => Strings.T("stCompressed"),
        AppStatus.Partial => Strings.T("stPartial"),
        _ => Strings.T("stNoSignal")
    };

    public System.Numerics.Vector4 StatusColour => Status switch
    {
        AppStatus.Nominal => Theme.Good,
        AppStatus.Compressed or AppStatus.Partial => Theme.Warn,
        _ => Theme.Bad
    };
}

/// <summary>
/// Frame times for the graph: 70 samples at 5 Hz, which is a fourteen-second window.
/// Sampled rather than per-frame on purpose — at 120 fps a per-frame graph is noise, and
/// the design asks for the same cadence the rest of the readout updates at.
/// </summary>
public sealed class FrameHistory
{
    public const int Capacity = 70;
    private readonly float[] _samples = new float[Capacity];
    private int _count;

    public void Push(float ms)
    {
        if (_count < Capacity)
        {
            _samples[_count++] = ms;
            return;
        }
        Array.Copy(_samples, 1, _samples, 0, Capacity - 1);
        _samples[Capacity - 1] = ms;
    }

    public ReadOnlySpan<float> Samples => _samples.AsSpan(0, _count);

    public void Clear() => _count = 0;
}
