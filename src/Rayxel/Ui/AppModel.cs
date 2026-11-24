using Rayxel.Audio;
using Rayxel.Capture;
using Rayxel.Render;
using Rayxel.Settings;

namespace Rayxel.Ui;

/// <summary>
/// Where the app is: starting, one of its three pages - where a picture comes from, this computer
/// as a sender, the settings - or the picture itself.
/// </summary>
public enum Screen { Splash, Sources, Transmit, Settings, App }

/// <summary>What is open over the picture: nothing, or the settings panel at its right edge.</summary>
public enum Flyout { None, Settings }

/// <summary>Which of the design's four states the app is in right now.</summary>
public enum AppStatus { Nominal, Compressed, Partial, NoSignal }

/// <summary>
/// One setting inside a technique: a DLSS network, an FSR flavour, a frame multiplier.
/// A name that is a word rather than a designation is given as a string key instead.
/// </summary>
public sealed record TechniqueVariant(string Id, string Label, string? LabelKey = null)
{
    public string Text => LabelKey is null ? Label : Strings.T(LabelKey);
}

/// <summary>
/// A list that is asked for again each time it is read, for variants that are not known
/// when the technique is described - what a library offers on the card it finds.
/// </summary>
public sealed class Live<T>(Func<IReadOnlyList<T>> read) : IReadOnlyList<T>
{
    public T this[int index] => read()[index];
    public int Count => read().Count;
    public IEnumerator<T> GetEnumerator() => read().GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// An upscaler or a frame generator as the interface sees it: what it is called, what it
/// costs, the variants it offers, and whether it can be used right now — with the reason
/// when it cannot.
/// <para>
/// Whatever can change while the app runs is a function, because the answers do: the
/// language, the adapter, a driver that refused. A backend plugs in by filling one of
/// these in; the bar and both flyouts draw whatever the lists hold, and a technique with
/// nothing behind it yet is one whose <see cref="Unavailable"/> says so.
/// </para>
/// </summary>
public sealed class Technique
{
    /// <summary>What selecting it passes on. For an upscaler the number keys are this plus one.</summary>
    public required int Index { get; init; }

    /// <summary>The short name, for the chooser and the bar: "DLSS".</summary>
    public required Func<string> Short { get; init; }

    /// <summary>The full name, as its maker writes it.</summary>
    public required Func<string> Name { get; init; }

    /// <summary>One line on what it does.</summary>
    public required Func<string> Description { get; init; }

    /// <summary>
    /// GPU time per frame in milliseconds, as measured; null when nobody has measured it,
    /// which is shown as n/a rather than guessed.
    /// </summary>
    public double? CostMs { get; init; }

    /// <summary>The cost is a ceiling rather than a figure: "&lt; 0,1 ms".</summary>
    public bool CostIsCeiling { get; init; }

    /// <summary>Null when it can be used; otherwise why not, as a sentence.</summary>
    public Func<string?> Unavailable { get; init; } = () => null;

    /// <summary>String key of what the variants are: "model", "variant", "frames".</summary>
    public string VariantsLabel { get; init; } = "";

    public IReadOnlyList<TechniqueVariant> Variants { get; init; } = [];

    /// <summary>The variant asked for, as the id of one of <see cref="Variants"/>.</summary>
    public Func<string?> Variant { get; init; } = () => null;

    public Action<string> SetVariant { get; init; } = _ => { };

    /// <summary>
    /// The variants are shown but cannot be changed here: they belong to something that is
    /// off. A multiplier beside "no generation" is the case — worth seeing, nothing to set.
    /// </summary>
    public bool VariantsLocked { get; init; }

    /// <summary>
    /// What there is to say about one variant, by its id: shown beside the list while that
    /// row is under the pointer or the arrow keys. Null for nothing.
    /// </summary>
    public Func<string, string?> VariantInfo { get; init; } = _ => null;

    /// <summary>
    /// What is actually running, by the name of one of <see cref="Variants"/>, when that
    /// can differ from what was asked for. The menu has no row for it: the variant's own
    /// box shows it. When something overrode the choice, <c>Overridden</c> says what did
    /// and how to undo it, as sentences — the box then cannot be set, since nothing chosen
    /// in it would be obeyed, and those sentences are written under it.
    /// </summary>
    public Func<(string Text, string? Overridden)?> Running { get; init; } = () => null;

    public bool Usable => Unavailable() is null;

    /// <summary>The cost as the interface writes it: "0 ms", "&lt; 0,1 ms", "+4,1 ms", or n/a.</summary>
    public string CostText => CostMs switch
    {
        null => "n/a",
        0 => Strings.Ms(0, "0"),
        { } ms when CostIsCeiling => "< " + Strings.Ms(ms, "0.0"),
        { } ms => "+" + Strings.Ms(ms, "0.0")
    };
}

/// <summary>One stage of a frame's delay, in the order the frame meets them.</summary>
public readonly record struct DelayPart(string LabelKey, double Ms);

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

    /// <summary>
    /// Pictures the screen showed over the last second: the real frames, and with frame
    /// generation the generated ones too. Set once a second by the frame loop.
    /// </summary>
    public double PicturesFps;

    /// <summary>The same, for what a frame's age is made of; null unless the source is on the network.</summary>
    public NetStats? ShownNet;
    public double ShownCpuMs;
    public double ShownGpuMs;
    public string Composition = "?";

    /// <summary>Takes the values the readout will show until the next tick.</summary>
    public void SampleForDisplay(string composition)
    {
        Shown = Pump.Snapshot();
        ShownNet = Pump.Network;
        ShownCpuMs = AppCpuMs;
        ShownGpuMs = GpuMs;
        Composition = composition;
        SampleDelay();
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

    /// <summary>Diagnostic only: the plates over the picture stay up, for looking at them without moving the pointer.</summary>
    public bool KeepPlates;

    public Flyout Flyout;
    public float OverlayNits = 230f;

    /// <summary>Level our own shader encodes the video at for a PQ swapchain.</summary>
    public float VideoNits = 200f;
    public double SplashElapsedMs;

    /// <summary>
    /// The device was named on the command line. The picker is then not shown at start-up:
    /// it would be asking which device to use of someone who has just said.
    /// </summary>
    public bool DeviceNamed;

    /// <summary>
    /// The window opened with no usable device: the pump stands on a stand-in that is never
    /// opened, and the device picker is where to go - to choose one, or pair a Steam Deck.
    /// </summary>
    public bool NoDevice;

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

    /// <summary>Pairing with a Steam Deck under way: its dialog is up while this is set.</summary>
    internal PairFlow? Pairing;

    /// <summary>This computer as a sender: the program that sends, watched over its control socket.</summary>
    internal SenderLink Sender { get; } = new();

    /// <summary>A sender was just paired: it joins the device list and is switched to.</summary>
    internal Action<Rayxel.Link.Security.Peer> Paired = _ => { };

    /// <summary>Lists the devices again, after a pairing was made or forgotten.</summary>
    public Action RefreshDevices = () => { };

    /// <summary>A paired sender whose forgetting waits for a yes: its dialog is up while this is set.</summary>
    internal Rayxel.Link.Security.Peer? ConfirmForget;

    /// <summary>
    /// Forgets a paired sender: its key goes and it leaves the device list. Being watched, it
    /// stops, and the device picker comes up.
    /// </summary>
    internal Action<Rayxel.Link.Security.Peer> Forget = _ => { };

    /// <summary>Diagnostic only: a sender's refusal card - forgot this computer, busy, off - without the sender refusing.</summary>
    public Rayxel.Link.Wire.RefuseReason? ForceRefusal;

    /// <summary>Why the sender being watched turns this computer away, if it does.</summary>
    public Rayxel.Link.Wire.RefuseReason? Refusal => ForceRefusal ?? Pump.Refusal;

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
        s.Upscaler = ScalerIndex;
        s.FrameGen = FrameGenIndex;
        s.FrameMultiplier = FrameMultiplierId;
        // The switch has two meanings and each keeps its own setting.
        if (!Renderer.SourceIsHdr) s.Hdr = HdrOn;
        s.HdrSourceAsIs = Renderer.HdrAsIs;
        s.SourceRange = Renderer.SourceRange;
        s.DlssModel = (int)(Renderer.Dlss?.Model ?? (DlssModel)s.DlssModel);
        if (Renderer.Fsr is { } fsr) { s.FsrVersion = fsr.Version; s.FsrModel = (int)fsr.Model; }
        s.OverlayNits = OverlayNits;
        s.NetAdaptive = NetDevice.Adaptive;
        s.NetBitrateKbps = NetDevice.BitrateKbps;
        s.NetCodec = AppSettings.CodecKey(NetDevice.Codec);
        s.NetPyroWaveKbps = NetDevice.PyroWaveKbps;
        s.VideoNits = VideoNits;
        s.ShowTelemetry = ShowTelemetry;
        s.TelemetryDetailed = TelemetryDetailed;
        s.Borderless = WindowKind != Native.WindowKind.Windowed;
        if (!NoDevice) s.DeviceLink = Pump.Device.SymbolicLink;

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

    /// <summary>The upscaler that has the picture: 0 = none, 1 = DLSS, 2 = FSR, 3 = RTX VSR.</summary>
    public int ScalerIndex => Renderer.UseFsr ? 2 : Renderer.UseDlss ? 1 : Renderer.UseVsr ? 3 : 0;

    // ── upscalers and frame generators ───────────────────────────────────────────

    // Measured on this machine with --bench rather than taken from a vendor sheet, taking
    // 1280x800 to 2304x1440 on an RTX 5080, as what a frame costs more than our own blit's
    // 0.62 ms. DLSS with its model L, and the motion estimate it cannot do without, add
    // 1.1 ms between them. FSR 4.1.2 with its full network and the same estimate adds 1.6;
    // its lighter network 0.8, and FSR 3.1 2.9 - not for its work, which is the least of
    // the three, but because that is light enough for the driver to drop the card into a
    // lower power state, where it settles. FSR is listed at the first of these, which is
    // what runs unless another is picked. Shown as fixed figures, because a cost that
    // moves while it is being read cannot be compared with the one beside it.
    // RTX VSR was measured the same way before it was taken out, at 4.1 ms, its quality
    // level being whatever the NVIDIA app is set to.
    private const double DlssCostMs = 1.1, FsrCostMs = 1.6, VsrCostMs = 4.1;

    private List<Technique>? _upscalers, _frameGenerators;

    /// <summary>
    /// Every upscaler the interface offers, in the order of <see cref="ScalerIndex"/> and of
    /// the keys 1 to 3. One that cannot be used stays in the list and says why.
    /// </summary>
    public IReadOnlyList<Technique> Upscalers => _upscalers ??=
    [
        new()
        {
            Index = 0,
            Short = () => Strings.T("upOff"),
            Name = () => Strings.T("pNone"),
            Description = () => Strings.T("dNone"),
            CostMs = 0
        },
        new()
        {
            Index = 1,
            Short = () => "DLSS",
            Name = () => "DLSS Super Resolution",
            Description = () => Strings.T("dDlss"),
            CostMs = DlssCostMs,
            Unavailable = () => Renderer.Dlss is { Possible: true } ? null : Strings.Sentence(Strings.T("naVendor")),
            VariantsLabel = "tvModel",
            // Newest first. There is no "whichever DLSS likes": left to itself it takes K
            // for a video, the oldest of them.
            Variants = [new(nameof(DlssModel.L), "L"), new(nameof(DlssModel.M), "M"), new(nameof(DlssModel.K), "K")],
            Variant = () => Renderer.Dlss?.Model.ToString(),
            VariantInfo = id => Strings.T("dlssInfo" + id),
            SetVariant = id =>
            {
                if (Renderer.Dlss is not { } dlss || !Enum.TryParse<DlssModel>(id, out var asked)) return;
                dlss.Model = asked;
                Touch();
            },
            // Not always what was asked for: the driver's own "DLSS override" names a
            // network for every program and wins.
            Running = () => Renderer.Dlss is { ModelInUse: { } letter } dlss
                ? (letter, dlss.ModelForced ? DlssOverrideAdvice() : null) : null
        },
        new()
        {
            Index = 2,
            Short = () => "FSR",
            Name = () => "FidelityFX Super Resolution",
            Description = () => Strings.T("dFsr"),
            CostMs = FsrCostMs,
            Unavailable = () => Renderer.Fsr is { Possible: true } ? null
                : FsrScaler.Folder is null ? Strings.T("naFsr") : Strings.Sentence(Strings.T("naVendor")),
            VariantsLabel = "tvVariant",
            // What AMD's library has for this card, which is only known once it is loaded.
            Variants = new Live<TechniqueVariant>(FsrVariants),
            Variant = () => Renderer.Fsr?.Chosen?.Label,
            VariantInfo = id => Renderer.Fsr?.Choices.FirstOrDefault(c => c.Label == id) is { Label: not null } choice
                ? Strings.T(choice.Model switch
                {
                    FsrModel.Full => "fsrInfoFull",
                    FsrModel.Lite => "fsrInfoLite",
                    _ => choice.Label.StartsWith('4') ? "fsrInfoOwn" : "fsrInfo3"
                })
                : null,
            SetVariant = id =>
            {
                if (Renderer.Fsr is not { } fsr || fsr.Choices.FirstOrDefault(c => c.Label == id) is not { Label: not null } choice) return;
                fsr.Ask(choice);
                Touch();
            },
            // While one is being built there is nothing running to name.
            Running = () => Renderer.Fsr is { Building: false, InUse: { } running } ? (running, null) : null
        },
        new()
        {
            // The driver's own upscaler for video, on its video processor. No variants: its
            // quality level is set in the NVIDIA app.
            Index = 3,
            Short = () => Strings.T("pVsr"),
            Name = () => "RTX Video Super Resolution",
            Description = () => Strings.T("dVsr"),
            CostMs = VsrCostMs,
            Unavailable = () => Renderer.Processor is not { Available: true, SuperResolutionSupported: true }
                ? Strings.Sentence(Strings.T("naVendor"))
                : Renderer.SourceIsHdr ? Strings.T("naVsrHdr")
                : Renderer.FrameGenEnabled ? Strings.T("naVsrFg")
                : null
        }
    ];

    private readonly List<TechniqueVariant> _fsrVariants = [];

    /// <summary>FSR's choices as the interface's variants, made again only when the choices have changed.</summary>
    private IReadOnlyList<TechniqueVariant> FsrVariants()
    {
        var choices = Renderer.Fsr?.Choices ?? [];
        bool same = choices.Count == _fsrVariants.Count;
        for (int i = 0; same && i < choices.Count; i++) same = choices[i].Label == _fsrVariants[i].Id;
        if (!same)
        {
            _fsrVariants.Clear();
            foreach (var choice in choices) _fsrVariants.Add(new TechniqueVariant(choice.Label, choice.Label));
        }
        return _fsrVariants;
    }

    private long _dlssDllLooked = -10_000;
    private bool _dlssDllBeside;

    /// <summary>
    /// Why the DLSS model cannot be chosen while the driver's override names one, and where
    /// that is switched off. With no nvngx_dlss.dll beside this program there is a step
    /// before it: the driver lends its own copy only while the override is on, so turning
    /// it off first would take DLSS away altogether. Looked for at most every two seconds;
    /// the file can be put there while the menu is open.
    /// </summary>
    private string DlssOverrideAdvice()
    {
        long now = Environment.TickCount64;
        if (now - _dlssDllLooked > 2000)
        {
            _dlssDllLooked = now;
            _dlssDllBeside = File.Exists(Path.Combine(AppContext.BaseDirectory, "nvngx_dlss.dll"));
        }
        return _dlssDllBeside
            ? Strings.T("dlssForcedInfo")
            : Strings.T("dlssForcedInfo") + ". " + Strings.T("dlssForcedNoDll");
    }

    /// <summary>The upscaler that is running.</summary>
    public Technique Upscaler => Upscalers[Math.Clamp(ScalerIndex, 0, Upscalers.Count - 1)];

    /// <summary>
    /// Drops open the list of techniques in whichever of the two flyouts is up, once, with
    /// the arrow keys already on this row; -1 for not at all. For a key that asked for a
    /// technique that cannot be used — its row is where it says why — and for capturing the
    /// list without a click.
    /// </summary>
    public int OpenTechniqueAt = -1;

    /// <summary>Diagnostic only: the same for the list of the chosen technique's variants.</summary>
    public int OpenVariantAt = -1;

    /// <summary>Diagnostic only: shows the technique row's info tooltip without a pointer over it.</summary>
    public bool ShowTechniqueInfo;

    /// <summary>Diagnostic only: the same for the row of the chosen technique's variants.</summary>
    public bool ShowVariantInfo;

    /// <summary>
    /// Frame generation, built as the upscalers are: the multipliers are its variants. DLSS
    /// Frame Generation runs on a Direct3D 12 device beside the program's own; FSR's has
    /// nothing behind it yet and says so.
    /// </summary>
    public IReadOnlyList<Technique> FrameGenerators => _frameGenerators ??=
    [
        new()
        {
            Index = 0,
            Short = () => Strings.T("upOff"),
            Name = () => Strings.T("fgOffName"),
            Description = () => Strings.T("fgOffD"),
            CostMs = 0,
            // The multiplier is on show while generation is off, as a game's menu keeps a
            // dependent setting in view: greyed, because there is nothing for it to set.
            VariantsLabel = "tvFrames",
            Variants = FrameMultipliers,
            Variant = () => FrameMultiplierId,
            VariantInfo = FrameGenFacts,
            VariantsLocked = true
        },
        new()
        {
            Index = 1,
            Short = () => "DLSS FG",
            Name = () => "DLSS Frame Generation",
            Description = () => Strings.T("dFgDlss"),
            CostMs = DlssFgCostMs,
            Unavailable = DlssFrameGenUnavailable,
            VariantsLabel = "tvFrames",
            Variants = FrameMultipliers,
            Variant = () => FrameMultiplierId,
            VariantInfo = FrameGenFacts,
            SetVariant = id => { FrameMultiplierId = id; Touch(); },
            // "auto" comes to a number of its own, which the box shows while it runs.
            Running = () => Renderer.UseFrameGen && FrameMultiplierId == "auto"
                ? (FrameMultipliers.First(v => v.Id == FrameGenOutput("auto").Factor.ToString()).Text, null)
                : null
        },
        new()
        {
            Index = 2,
            Short = () => "FSR FG",
            Name = () => "FSR Frame Generation",
            Description = () => Strings.T("dFgFsr"),
            Unavailable = () => Strings.T("naFrameGen"),
            VariantsLabel = "tvFrames",
            Variants = FrameMultipliers,
            Variant = () => FrameMultiplierId,
            VariantInfo = FrameGenFacts
        }
    ];

    /// <summary>
    /// How many frames to show for each one that arrives. "auto" is the largest multiple
    /// the display can still show: generating past its refresh rate only costs.
    /// </summary>
    private static readonly TechniqueVariant[] FrameMultipliers =
        [new("auto", "", "dlssAuto"), new("2", "×2"), new("3", "×3"), new("4", "×4")];

    /// <summary>The generator asked for: 0 none, 1 DLSS Frame Generation.</summary>
    public int FrameGenIndex => Renderer.FrameGenEnabled ? 1 : 0;

    // What generating the frames between two real ones costs the GPU, measured with --bench
    // on an RTX 5080 behind DLSS taking 1280x800 to 2304x1440: see DlssFrameGen.
    private const double DlssFgCostMs = 1.9;

    /// <summary>
    /// Why DLSS Frame Generation cannot be chosen, as a sentence; null when it can. What the
    /// generator says of itself is in English, for the log; what a user can act on - the
    /// card, the DLL beside the program, an HDR path it cannot join - is said in their language.
    /// </summary>
    private string? DlssFrameGenUnavailable()
    {
        if (Renderer.FrameGen is not { } fg) return Strings.Sentence(Strings.T("naVendor"));
        if (fg.Unsupported is { } why)
            return why.Contains("NVIDIA") ? Strings.Sentence(Strings.T("naVendor"))
                : why.Contains("nvngx_dlssg") ? Strings.T("fgNoDll")
                : why;
        if (Renderer.PassingHdrThrough) return Strings.T("fgNoHdrAsIs");
        if (Renderer.UseProcessor) return Strings.T("fgNoRtxHdr");
        return fg.LastError;
    }

    public Technique FrameGenerator => FrameGenerators[FrameGenIndex];

    /// <summary>Asks for a generator by its index. Nothing listens until one exists.</summary>
    public Action<int> SelectFrameGen = _ => { };

    /// <summary>The multiplier asked for: "auto", "2", "3" or "4". See <see cref="FrameGenOutput"/>.</summary>
    public string FrameMultiplierId = "2";

    /// <summary>
    /// Refresh rate of the monitor the window is on, as Windows has it set; 0 when unknown.
    /// What a frame generator's output is worth is decided by this: frames past it are
    /// generated and never shown.
    /// </summary>
    public int DisplayHz;

    /// <summary>
    /// What a multiplier comes to on this source and this display: the frames shown per one
    /// that arrives, and the rate that makes. "auto" resolves to the largest multiple the
    /// display can show, and to 2 when the display's rate is not known.
    /// </summary>
    public (int Factor, double Hz) FrameGenOutput(string multiplierId)
    {
        double source = Pump.CurrentFps;
        int factor = int.TryParse(multiplierId, out int asked) ? asked
            : DisplayHz > 0 && source > 0 ? Math.Clamp((int)(DisplayHz / source), 2, 4) : 2;
        return (factor, source * factor);
    }

    /// <summary>
    /// The least a frame generator can add to the delay, in milliseconds: it draws frames
    /// between two real ones, so a real frame has to wait for the next one to exist and
    /// then for the generated ones shown ahead of it. Arithmetic, not a measurement, and
    /// labelled as a floor wherever it is shown; the GPU time comes on top and is unknown.
    /// </summary>
    public double FrameGenDelayFloorMs(int factor) =>
        Pump.CurrentFps > 0 && factor > 1 ? 1000.0 / Pump.CurrentFps * (factor - 1) / factor : 0;

    /// <summary>
    /// What a multiplier comes to: the rate it makes against the display's own, and the
    /// least it adds to the delay. Both are arithmetic and worded as such; the GPU time
    /// nobody has measured, and it says that too.
    /// </summary>
    private string? FrameGenFacts(string multiplierId)
    {
        if (Pump.CurrentFps <= 0) return null;

        var (factor, hz) = FrameGenOutput(multiplierId);
        string text = $"{Strings.Num(Pump.CurrentFps, "0.##")} → {Strings.Num(hz, "0.##")} {Strings.T("hz")}";
        if (DisplayHz > 0) text += " · " + string.Format(Strings.T("fgDisplay"), DisplayHz);
        // Frames past the display's own rate are generated and never shown.
        if (DisplayHz > 0 && hz > DisplayHz + 0.5) text += "\n" + string.Format(Strings.T("fgPast"), DisplayHz);
        return text + "\n" + Strings.Sentence(
            string.Format(Strings.T("fgCostV"), Strings.Ms(FrameGenDelayFloorMs(factor), "0.0")));
    }

    // ── delay ────────────────────────────────────────────────────────────────────

    private readonly DelayPart[] _delayParts = new DelayPart[5];
    private int _delayPartCount;

    /// <summary>
    /// A frame's delay as one figure, from the held sample: everything between the picture
    /// being taken at the source and our handing it to the display that this machine can
    /// measure. NaN when a part of it is not known — then there is no total to state.
    /// <para>
    /// Over the network that is the sender's encoder, the network and the decoder here; from
    /// a capture card it is the frame's age when Windows hands it over. Our own processing
    /// is added to both. What it leaves out is what nothing here can see: the console's own
    /// rendering, a capture card's internal delay, and the monitor.
    /// </para>
    /// </summary>
    public double DelayMs { get; private set; } = double.NaN;

    /// <summary>What <see cref="DelayMs"/> is made of, in order. Empty when it is unknown.</summary>
    public ReadOnlySpan<DelayPart> DelayParts => _delayParts.AsSpan(0, _delayPartCount);

    private void SampleDelay()
    {
        _delayPartCount = 0;
        DelayMs = double.NaN;
        if (Pump.State != CaptureState.Streaming) return;

        double ours = ShownCpuMs + ShownGpuMs;
        if (ShownNet is { } net)
        {
            if (!net.ClockKnown || !net.DecodeKnown) return;
            _delayParts[_delayPartCount++] = new("tEnc", net.EncodeMs);
            _delayParts[_delayPartCount++] = new("tNet", net.NetworkMs);
            _delayParts[_delayPartCount++] = new("tDec", net.DecodeMs);
        }
        else
        {
            if (!Shown.PipelineDelayValid) return;
            _delayParts[_delayPartCount++] = new("tCapture", Shown.PipelineDelayMs);
        }
        _delayParts[_delayPartCount++] = new("tWork", ours);
        // Generated frames are shown ahead of the real one, which waits for them.
        if (Renderer.UseFrameGen && Renderer.FrameGenDelayMs > 0)
            _delayParts[_delayPartCount++] = new("tFg", Renderer.FrameGenDelayMs);

        double total = 0;
        foreach (var part in DelayParts) total += part.Ms;
        DelayMs = total;
    }

    /// <summary>Frames that never reached the screen, whoever lost them: the network or us.</summary>
    public long FramesLost => Shown.FramesDropped + (ShownNet?.FramesLost ?? 0);

    /// <summary>
    /// The HDR switch's position. For an SDR source: RTX Video HDR is making an HDR
    /// picture of it. For an HDR source: it is going to the display as it is.
    /// </summary>
    public bool HdrOn => Renderer.SourceIsHdr ? Renderer.PassingHdrThrough : Renderer.Processor?.HdrEnabled == true;

    public bool DriverPathAvailable => Renderer.Processor is { Available: true };

    /// <summary>
    /// Whether SDR->HDR can be reached at all, and why not when it cannot. The design
    /// insists disabled controls explain themselves rather than merely grey out.
    /// </summary>
    public (bool Can, string Reason) HdrReachable()
    {
        if (Renderer.SourceIsHdr)
        {
            if (!Renderer.DisplaySupportsHdr) return (false, Strings.T("hdrSrcNoDisplay"));
            if (Renderer.UseDlss || Renderer.UseFsr) return (false, Strings.T("hdrSrcScaler"));
            return (true, Renderer.HdrAsIs ? Strings.T("hdrSrcAsIs") : Strings.T("hdrSrcToSdr"));
        }
        if (!DriverPathAvailable) return (false, Strings.T("hdrVendor"));
        if (!Renderer.DisplaySupportsHdr) return (false, Strings.T("hdrNa"));
        if (Renderer.UseDlss || Renderer.UseFsr) return (false, Strings.T("hdrPath"));
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
