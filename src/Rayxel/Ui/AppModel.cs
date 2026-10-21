using Rayxel.Audio;
using Rayxel.Capture;
using Rayxel.Render;
using Rayxel.Settings;

namespace Rayxel.Ui;

public enum Screen { Splash, Devices, App }

/// <summary>What is open above the bottom bar. One at a time, like any Fluent flyout.</summary>
public enum Flyout { None, Mode, Scaler, More }

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

    /// <summary>The one-line readout, or the full card with the graph. T walks off → line → card.</summary>
    public bool TelemetryDetailed;

    /// <summary>
    /// Whether the bottom bar is wanted. It comes up on any pointer movement or on Esc and
    /// goes away by itself — see <see cref="PanelIdleMs"/> — so this is never persisted:
    /// every start shows it for a moment, which is how anyone finds out it exists.
    /// </summary>
    public bool ShowBar = true;

    /// <summary>
    /// How far the bar has faded in, 0 to 1. Follows <see cref="ShowBar"/> over a sixth of
    /// a second, Fluent's own duration for a surface appearing.
    /// </summary>
    public float BarFade;

    public Flyout Flyout;
    public float OverlayNits = 230f;

    /// <summary>Level our own shader encodes the video at for a PQ swapchain.</summary>
    public float VideoNits = 200f;
    public double SplashElapsedMs;

    /// <summary>Diagnostic only: renders the no-signal card without unplugging anything.</summary>
    public bool ForceNoSignal;

    /// <summary>Diagnostic only: the "device in use" card, without a second app holding the card.</summary>
    public bool ForceBusy;

    /// <summary>Diagnostic only: the compositor warning, on a swapchain that is in fact on an overlay plane.</summary>
    public bool ForceComposed;

    /// <summary>Time since the bar was last touched. It hides itself once this runs out.</summary>
    public double PanelIdleMs;

    /// <summary>Height the telemetry block took last frame. Nothing reads it since the
    /// corner gear went; kept because the overlay measures itself anyway and the next thing
    /// placed against that block will want it.</summary>
    public float TelemetryHeight;

    /// <summary>
    /// True while the "really quit?" dialog is up. Closing by accident during a game means
    /// losing the picture mid-play, so both Esc and the Quit button ask first.
    /// </summary>
    public bool ConfirmQuit;

    public void PanelActivity() => PanelIdleMs = 0;

    /// <summary>The pointer moved over the window: bring the bar up and start its five seconds over.</summary>
    public void PointerMoved()
    {
        ShowBar = true;
        PanelIdleMs = 0;
    }

    /// <summary>Opens <paramref name="which"/>, or closes it if it is the one already open.</summary>
    public void ToggleFlyout(Flyout which)
    {
        Flyout = Flyout == which ? Flyout.None : which;
        ShowBar = true;
        PanelIdleMs = 0;
    }

    /// <summary>Off, then the one-line readout, then the full card, then off again.</summary>
    public void CycleTelemetry()
    {
        if (!ShowTelemetry) { ShowTelemetry = true; TelemetryDetailed = false; }
        else if (!TelemetryDetailed) TelemetryDetailed = true;
        else ShowTelemetry = false;
        Touch();
    }

    /// <summary>
    /// The pointer is hidden together with the bar, as any video player does. Only over the
    /// picture itself: the picker and the quit dialog are things to click.
    /// </summary>
    public bool CursorHidden => Screen == Screen.App && !ShowBar && BarFade <= 0f && !ConfirmQuit;

    /// <summary>
    /// True when the device was lost because another application holds it, which is the
    /// one cause of a lost device the user can do something about straight away.
    /// </summary>
    public bool DeviceBusy => ForceBusy || (Pump.State == CaptureState.Lost && Pump.HeldElsewhere);

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
    /// How much the picture is enlarged on its way to the window. The number that decides
    /// whether an upscaler is doing anything at all — at 1 or below no filter can add detail.
    /// <para>
    /// The smaller of the two ratios, because the picture is fitted with its aspect kept.
    /// Width alone overstated it on an ultrawide: a 1080p source in a 3440×1440 window was
    /// reported as ×1.79 while it was drawn at ×1.33 between two black bars.
    /// </para>
    /// </summary>
    public double ScaleFactor
    {
        get
        {
            var m = Pump.CurrentMode;
            return m is { Width: > 0, Height: > 0 }
                ? Math.Min(Renderer.Width / (double)m.Width, Renderer.Height / (double)m.Height)
                : 0;
        }
    }

    /// <summary>`2` when integral, `1,33` otherwise — no trailing zeros.</summary>
    public string ScaleLabel
    {
        get
        {
            double s = Math.Round(ScaleFactor, 2);
            return s <= 0 ? "" : Strings.Num(s, Math.Abs(s - Math.Round(s)) < 0.005 ? "0" : "0.##");
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
        s.TelemetryDetailed = TelemetryDetailed;
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
    /// True when the picture is not being enlarged, which makes any upscaler inert however
    /// it is toggled.
    /// </summary>
    public bool ScalerInert => Pump.CurrentMode is { Width: > 0 } && ScaleFactor <= 1.0;

    /// <summary>
    /// The warning worth a badge on the bar, if any — by what it costs, worst first.
    /// Only the two the user can act on: a composed window and a blown budget. A codec in
    /// the path is a choice, and the mode chip already says so in its colour.
    /// </summary>
    public string? Warning
    {
        get
        {
            if (Status == AppStatus.NoSignal) return null;
            if (ForceComposed || Composition == "Composed") return "composed";
            if (Pump.State == CaptureState.Streaming && BudgetMs > 0 && HeadroomMs < 0) return "budget";
            return null;
        }
    }

    public AppStatus Status
    {
        get
        {
            if (ForceNoSignal || ForceBusy || Pump.State is CaptureState.NoSignal or CaptureState.Lost)
                return AppStatus.NoSignal;
            if (Pump.CurrentMode?.HasCodecInPath == true) return AppStatus.Compressed;
            // Composited, dropping frames or out of budget: working, but not as intended.
            if (ForceComposed || Composition == "Composed" || HeadroomMs < 0 || Shown.FramesDropped > 0)
                return AppStatus.Partial;
            return AppStatus.Nominal;
        }
    }

    /// <summary>
    /// What is keeping the status off nominal, worst first. The word alone was true and
    /// unactionable — three unrelated conditions all produce "partial" and it never said
    /// which, so the only way to find out was to read the log.
    /// </summary>
    public IEnumerable<string> StatusReasons()
    {
        if (Pump.State is CaptureState.NoSignal or CaptureState.Lost)
        {
            yield return Strings.T("whyNoSignal");
            yield break;
        }

        // Ordered by what it costs: a composed frame is 8.33 ms at 120 fps, more than
        // everything else on this list together.
        if (ForceComposed || Composition == "Composed") yield return Strings.T("whyComposed");
        if (Pump.CurrentMode?.HasCodecInPath == true) yield return Strings.T("whyCodec");
        if (HeadroomMs < 0) yield return Strings.T("whyBudget");
        if (Shown.FramesDropped > 0) yield return Strings.T("whyDropped");
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
        AppStatus.Nominal => Theme.Success,
        AppStatus.Compressed or AppStatus.Partial => Theme.Caution,
        _ => Theme.TextTertiary
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
