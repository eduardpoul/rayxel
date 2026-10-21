// Aliased rather than imported: NAudio.CoreAudioApi has its own CaptureState, and pulling
// the whole namespace in here would make every mention of ours ambiguous.
using DataFlow = NAudio.CoreAudioApi.DataFlow;
using Rayxel.Audio;
using Rayxel.Capture;
using Rayxel.Diagnostics;
using Rayxel.Native;
using Rayxel.Render;
using Rayxel.Settings;
using Rayxel.Ui;

// A windowed build has no console of its own. --console attaches to the one it was
// launched from, which is what makes running it from a terminal useful again.
if (args.Contains("--console")) Win32.AttachParentConsole();
if (args.Contains("--verbose")) Log.MinLevel = LogLevel.Debug;

// Logs go beside the settings, not beside the executable: Program Files is not writable,
// and a tool that cannot write its own log where it was installed is a support call.
Log.OpenFile(Path.Combine(Rayxel.Settings.AppSettings.Directory, "logs"));
Log.Info($"Rayxel {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}");

var settings = AppSettings.Load();
Strings.Use(settings.Language);

string? deviceSelector = null;
string? shotPath = null;
string? forcedScreen = null;
string? forcedMode = null;
int forcedScaler = -1;
bool listOnly = false;
bool usbCheck = false;
double benchSeconds = 0;
bool startWithTelemetry = false;
bool noAutoHdr = false;
bool startBorderless = false;
// Present once per captured frame (the default), or on every swapchain slot as before.
// Kept switchable because "is it smoother?" is a comparison, and a comparison needs both.
bool presentOnArrival = true;
foreach (var arg in args)
{
    if (arg is "--list" or "-l") listOnly = true;
    // Reads the card's own USB descriptors and checks them against what this app
    // concluded from the Windows media types. Read-only; touches no driver.
    else if (arg is "--usb") usbCheck = true;
    else if (arg.StartsWith("--device=", StringComparison.OrdinalIgnoreCase)) deviceSelector = arg[9..];
    else if (arg.StartsWith("--lang=", StringComparison.OrdinalIgnoreCase)) Strings.Use(arg[7..]);
    // Diagnostic: run for a few seconds, write what was rendered to a PNG, exit. Captures
    // the back buffer rather than the screen, so it works when the window is not visible.
    else if (arg.StartsWith("--shot=", StringComparison.OrdinalIgnoreCase)) shotPath = arg[7..];
    else if (arg is "--tele") startWithTelemetry = true;
    // Runs a fixed measurement and exits: warm up, record every frame for N seconds,
    // print percentiles. For comparing one pipeline configuration against another, which
    // a moving average on a HUD cannot do.
    else if (arg.StartsWith("--bench", StringComparison.OrdinalIgnoreCase))
        benchSeconds = arg.Length > 8 && double.TryParse(arg[8..],
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
            out double bs) ? bs : 20;
    // Starts on our own shader instead of following the Windows HDR state, so the driver
    // path can be taken out of the picture when comparing what is actually on screen.
    else if (arg is "--no-auto-hdr") noAutoHdr = true;
    else if (arg is "--borderless") startBorderless = true;
    // Pins the starting mode, as --mode=1280x720@60. A comparison needs a control, and
    // "whatever was remembered from last time" is not one.
    else if (arg.StartsWith("--mode=", StringComparison.OrdinalIgnoreCase)) forcedMode = arg[7..];
    // Pins the scaler for the same reason: --scaler=none|nis|vsr.
    else if (arg.StartsWith("--scaler=", StringComparison.OrdinalIgnoreCase))
        forcedScaler = arg[9..].ToLowerInvariant() switch
        {
            "none" or "off" or "0" => 0,
            "nis" or "1" => 1,
            "vsr" or "rtx" or "2" => 2,
            _ => -1
        };
    // --pacing=arrival|refresh. See the frame wait in RunLoop for what the two mean.
    else if (arg.StartsWith("--pacing=", StringComparison.OrdinalIgnoreCase))
        presentOnArrival = !arg[9..].Equals("refresh", StringComparison.OrdinalIgnoreCase);
    // Forces a screen so the splash and picker can be inspected without racing them.
    else if (arg.StartsWith("--screen=", StringComparison.OrdinalIgnoreCase)) forcedScreen = arg[9..];
    else if (!arg.StartsWith('-')) deviceSelector = arg;
}

Renderer? renderer = null;
FramePump? pump = null;
AppModel? model = null;
DeviceProbe? active = null;
List<MenuEntry> menuEntries = [];
IntPtr hwnd = IntPtr.Zero;
bool quit = false;
bool pendingVsrToggle = false, pendingHdrToggle = false, pendingNisToggle = false, autoHdrPending = false;
bool deferredStatePending = true;
int pendingScaler = -1;
float pendingSharpness = 0f;
CaptureDeviceInfo? pendingDeviceSwitch = null;
WindowKind? pendingWindowKind = null;
bool pendingAudioRestart = false;
WindowMode? windowMode = null;

// The last pointer position seen, so a WM_MOUSEMOVE that Windows sends without the mouse
// having moved — it does, when a window under a still pointer changes — does not bring
// the bar back up by itself.
IntPtr lastPointer = new(-1);
bool cursorWasHidden = false;

// Every device Windows currently offers, refreshed whenever the menu opens so a card
// plugged in mid-session shows up. Separate from what we have actually opened: enumerating
// is free, opening is not, and opening a device is what makes a webcam's light come on.
List<CaptureDeviceInfo> allDevices = [];
Dictionary<string, DeviceProbe> probed = new(StringComparer.OrdinalIgnoreCase);

// The delegate must outlive the window: Win32 keeps the raw pointer, not the object.
Win32.WndProc wndProc = WindowProc;

Kernel.TimeBeginPeriod(1);
try
{
    allDevices = CaptureDevice.Enumerate();
    Log.Info($"{allDevices.Count} video capture device(s) present");
    foreach (var d in allDevices)
        Log.Info($"  [{d.Ordinal}] {d.FriendlyName}   {d.ShortId}{(d.IsVirtual ? "   (software device)" : "")}");

    if (allDevices.Count == 0)
    {
        Log.Error("no video capture devices at all. Plug the card in, or check Windows camera privacy settings.");
        return 1;
    }

    if (listOnly)
    {
        foreach (var d in allDevices) Report(Remember(CaptureDevice.Probe(d)));
        return 0;
    }

    if (usbCheck)
    {
        // Deliberately not through Remember: that applies the descriptor corrections, and a
        // check of the detectors against the descriptors is worth nothing once the
        // descriptors have already rewritten what it is checking. This is the raw
        // inference, which is the only thing here that can still be wrong.
        foreach (var d in allDevices.Where(x => !x.IsVirtual))
            CrossCheckUsb(d, CaptureDevice.Probe(d));
        return 0;
    }

    // Last used device, if it is still here. A remembered choice is only a preference:
    // if the card was unplugged or is now busy, fall through to choosing afresh.
    if (deviceSelector is null && settings.DeviceLink is { Length: > 0 } link)
    {
        var remembered = allDevices.FirstOrDefault(d =>
            string.Equals(d.SymbolicLink, link, StringComparison.OrdinalIgnoreCase));
        if (remembered is not null)
        {
            var probe = Remember(CaptureDevice.Probe(remembered));
            if (probe.IsReady) { active = probe; Log.Info($"using remembered device '{remembered.FriendlyName}'"); }
            else Log.Info($"remembered device '{remembered.FriendlyName}' is {probe.Availability.ToString().ToLowerInvariant()}; choosing another");
        }
    }

    // Named device: honour it exactly, including its failures. Guessing past a device the
    // user asked for by name would hide the very problem they are trying to see.
    if (deviceSelector is not null)
    {
        var wanted = CaptureDevice.Resolve(deviceSelector, allDevices);
        if (wanted is null)
        {
            Log.Error($"no device matches '{deviceSelector}'. Run with --list to see what is here.");
            return 1;
        }
        active = Remember(CaptureDevice.Probe(wanted));
    }
    else if (active is null)
    {
        var (chosen, examined) = CaptureDevice.ChooseBest(allDevices);
        foreach (var p in examined) Remember(p);
        active = chosen;
    }

    foreach (var p in probed.Values) Report(p);

    if (active is null || !active.IsReady)
    {
        var busy = probed.Values.Count(p => p.Availability == DeviceAvailability.Busy);
        Log.Error(busy > 0
            ? $"no usable device. {busy} of {probed.Count} are held by another application "
              + "(a browser tab, OBS, Teams) - close it and try again."
            : "no usable device: nothing here exposes a mode this app can render.");
        return 1;
    }

    Log.Info($"using: {active.Device.FriendlyName}  ({active.Device.ShortId})");

    menuEntries = BuildMenuEntries(active.Modes);
    Log.Info($"{menuEntries.Count} renderable modes offered in the menu");

    const int WindowWidth = 1600, WindowHeight = 900;
    hwnd = Win32.CreateAppWindow("Rayxel", wndProc, WindowWidth, WindowHeight,
                                 AppIcon.Large, AppIcon.Small);
    Layout.DisplayHeight = Win32.MonitorHeight(hwnd);
    // A way into the panel that needs no hotkey and draws nothing over the video.
    Win32.AddSystemMenuItem(hwnd, Win32.SC_APP_SETTINGS, Strings.T("params"));
    // A swapchain behind another window is not composed at all, which shows up as
    // "present: None" and makes the composition reading meaningless. Coming up in front
    // is also just what a video window should do.
    Win32.SetForegroundWindow(hwnd);
    Log.Info($"window {WindowWidth}x{WindowHeight} (client)");

    renderer = new Renderer();
    renderer.Initialize(hwnd, WindowWidth, WindowHeight);

    pump = new FramePump(active.Device, renderer);
    pump.AttachD3D(renderer.Device);

    model = new AppModel { Renderer = renderer, Pump = pump };
    model.Devices = allDevices;
    model.Probed = probed;
    model.Active = active;
    model.SelectScaler = want => { pendingScaler = want; model!.Touch(); };
    model.ToggleHdr = () => pendingHdrToggle = true;
    model.SelectMode = (m, fps) => { try { pump!.Select(m, fps); } catch (Exception ex) { Log.Error("mode switch failed", ex); } };
    model.SwitchDevice = d => pendingDeviceSwitch = d;
    model.SetWindowKind = k => pendingWindowKind = k;
    model.Quit = () => quit = true;
    windowMode = new WindowMode(hwnd);
    if (startBorderless) pendingWindowKind = WindowKind.BorderlessFullscreen;
    model.RestartAudio = () => pendingAudioRestart = true;
    model.ShowTelemetry = startWithTelemetry || settings.ShowTelemetry;
    model.TelemetryDetailed = settings.TelemetryDetailed;
    model.OverlayNits = settings.OverlayNits;
    model.VideoNits = settings.VideoNits;
    model.AudioEnabled = settings.AudioEnabled;
    model.AudioOutputId = settings.AudioOutputId;
    if (renderer.Nis is { } nisScaler) nisScaler.Sharpness = settings.NisSharpness;

    // Audio endpoints are listed once here and refreshed when the user opens a combo, so a
    // headset plugged in mid-session shows up without polling WASAPI every frame.
    RefreshAudioEndpoints();
    model.AudioInputId = settings.AudioInputId is { Length: > 0 } saved
                         && model.AudioInputs.Any(e => e.Id == saved)
        ? saved
        : AudioEndpoints.MatchTo(active.Device)?.Id;
    pendingAudioRestart = true;

    if (settings.Borderless) pendingWindowKind = WindowKind.BorderlessFullscreen;
    if (forcedScreen is not null)
        model.Screen = forcedScreen.ToLowerInvariant() switch
        {
            "splash" => Screen.Splash,
            "devices" => Screen.Devices,
            _ => Screen.App
        };
    if (forcedScreen == "nosignal") model.ForceNoSignal = true;
    if (forcedScreen == "busy") model.ForceBusy = true;
    if (forcedScreen == "composed") model.ForceComposed = true;
    // Opens the states that need a click to reach, so they can be checked without one.
    if (forcedScreen == "modes") model.Flyout = Flyout.Mode;
    if (forcedScreen == "scaler") model.Flyout = Flyout.Scaler;
    if (forcedScreen == "more") model.Flyout = Flyout.More;
    if (forcedScreen == "quit") model.ConfirmQuit = true;

    var initial = ForcedMode(active, forcedMode) ?? RememberedMode(active, settings) ?? PickInitialMode(active);
    if (initial is null) { Log.Error("no renderable mode found"); return 1; }
    pump.Select(initial.Mode, initial.Fps);
    ReportFirstFrame(pump);

    // Probing cannot see "in use elsewhere" — that only shows at the first read — so the
    // best-looking device can still turn out to be held by something else. With more than
    // one card present, try the others before settling for a black window. A device the
    // user named is left alone: they asked for that one, and hiding its failure by quietly
    // using a different card would be worse than showing nothing.
    if (pump.State == CaptureState.Lost && deviceSelector is null)
    {
        foreach (var other in allDevices.Where(d => !d.Is(pump.Device) && !d.IsVirtual))
        {
            Log.Warn($"trying '{other.FriendlyName}' instead");
            SwitchDevice(other);
            if (pump.State == CaptureState.Starting) ReportFirstFrame(pump);
            if (pump.State != CaptureState.Lost) break;
        }
    }

    // Follow Windows: if the desktop is already in HDR, start in HDR too. If it is not,
    // the toggle stays unavailable rather than producing a washed-out picture.
    // Compile NIS up front rather than on first use: shader compilation and parsing the
    // coefficient tables are the two things most likely to be wrong, and finding out at
    // startup beats finding out when the user presses a key mid-game.
    if (renderer.Nis?.Initialize(NisHdrMode.None) != true)
        Log.Warn($"NIS unavailable: {renderer.Nis?.LastError ?? "unknown"}");

    // Deferred: the video processor is only built once the first frame arrives, and the
    // HDR extension cannot be set before it exists.
    autoHdrPending = renderer.DisplaySupportsHdr && !noAutoHdr;
    Log.Info(autoHdrPending
        ? "Windows has this display in HDR - will enable HDR output once the first frame lands"
        : "Windows has this display in SDR - HDR toggle disabled");

    Log.Info("Tab settings · T telemetry · M modes · H HDR · 1/2/3 scaler · Esc quit");
    RunLoop();
    return 0;
}
catch (Exception ex)
{
    Log.Error("fatal", ex);
    return 2;
}
finally
{
    // Last write wins over the periodic one: whatever changed in the final seconds is
    // exactly what the user did most recently.
    if (model is not null) { model.CaptureInto(settings); settings.Save(); }
    model?.Audio.Dispose();
    pump?.Dispose();
    renderer?.Dispose();
    if (hwnd != IntPtr.Zero) Win32.DestroyWindow(hwnd);
    AppIcon.Release();
    Kernel.TimeEndPeriod(1);
    GC.KeepAlive(wndProc);
    Log.Info($"log: {Log.FilePath}");
    Log.Close();
}

// Waits out the gap between "the device accepted a media type" and "the device is actually
// producing pictures". Those are not the same event: a card another application is already
// streaming from opens and configures without complaint and only refuses at the first read,
// so this is the earliest moment at which "in use elsewhere" can be known at all.
static void ReportFirstFrame(FramePump pump)
{
    var clock = System.Diagnostics.Stopwatch.StartNew();
    while (clock.ElapsedMilliseconds < 2500 && pump.State == CaptureState.Starting)
        Thread.Sleep(20);

    switch (pump.State)
    {
        case CaptureState.Streaming:
            Log.Info($"first frame after {clock.ElapsedMilliseconds} ms");
            break;
        case CaptureState.Lost:
            Log.Error($"no video: {pump.StateDetail}. " +
                      "Close whatever else is using the card, or pick another device from the menu.");
            break;
        default:
            Log.Warn("no frames yet - the source may be asleep or the HDMI input unplugged. " +
                     "Video will appear on its own when a frame arrives.");
            break;
    }
}

// Re-lists the WASAPI endpoints. Called at start-up and whenever the audio card is shown,
// so a headset plugged in mid-session appears without polling WASAPI every frame.
void RefreshAudioEndpoints()
{
    if (model is null) return;
    model.AudioInputs = AudioEndpoints.List(DataFlow.Capture);
    model.AudioOutputs = AudioEndpoints.List(DataFlow.Render);
}

// The mode from last time, if this device still offers it. Matched on what the user
// actually chose - geometry, rate and format - rather than on the type index, which is a
// position in a list the driver is free to reorder between runs.
static MenuEntry? RememberedMode(DeviceProbe probe, AppSettings settings)
{
    if (settings.ModeWidth <= 0 || settings.ModeFormat is null) return null;

    var match = probe.Renderable.FirstOrDefault(m =>
        m.Width == settings.ModeWidth &&
        m.Height == settings.ModeHeight &&
        m.Format.Name == settings.ModeFormat &&
        settings.ModeFps >= m.MinFps - 0.5 && settings.ModeFps <= m.MaxFps + 0.5);

    if (match is null)
    {
        Log.Info($"remembered mode {settings.ModeWidth}x{settings.ModeHeight} @ {settings.ModeFps:0.##} " +
                 $"{settings.ModeFormat} is not offered by this device; choosing afresh");
        return null;
    }

    Log.Info($"restoring mode {match.Width}x{match.Height} @ {settings.ModeFps:0.##} {match.Format.Name}");
    return new MenuEntry(match, settings.ModeFps);
}

// Caches a probe result under the device's interface path, which is the only key that
// stays put when the enumeration order changes.
//
// Also the one place every probe passes through, so it is where the link speed gets read
// off the device's own USB descriptors. That has to happen before anything shows a mode's
// transfer time, and doing it here means a device swapped mid-session gets it too.
DeviceProbe Remember(DeviceProbe p)
{
    CaptureDevice.ApplyHardwareFacts(p);
    probed[p.Device.SymbolicLink] = p;
    return p;
}

/// <summary>
/// Puts the device's own descriptors beside what this app concluded from the Windows media
/// types, and says where the two disagree.
/// <para>
/// The whole "this mode claims NV12 but carries JPEG" finding rests on two indirect tests.
/// This is the direct one: the hardware's own format list, which Windows never shows.
/// </para>
/// </summary>
void CrossCheckUsb(CaptureDeviceInfo device, DeviceProbe probe)
{
    Log.Section($"USB descriptors — {device.FriendlyName}");

    var usb = UsbDescriptors.Read(device);
    if (usb is null)
    {
        Log.Warn("  could not read the descriptors for this device");
        return;
    }

    Log.Info($"  {usb.VendorId:x4}:{usb.ProductId:x4}   device is {usb.UsbVersion}, running at {usb.Speed}");
    Log.Info($"  found on {usb.Location}");

    // The link speed took three tries to read correctly, so every source now shows its
    // working. If they disagree the reader can see which one to distrust.
    Log.Info("  link speed, by source:");
    foreach (var line in usb.SpeedEvidence) Log.Info($"    {line}");

    if (usb.DowngradedLink)
        Log.Warn("    the device can do SuperSpeed but is not running there - " +
                 "a USB 2 cable or port would explain it, and would cap real capture at ~60 MB/s");

    if (usb.Endpoints.Count > 0)
    {
        Log.Info("  endpoints:");
        foreach (var ep in usb.Endpoints)
        {
            string burst = ep.Burst >= 0 ? $", burst {ep.Burst + 1}" : "";
            string perInterval = ep.BytesPerInterval > 0 ? $", {ep.BytesPerInterval} bytes/interval" : "";
            Log.Info($"    0x{ep.Address:X2} {(ep.In ? "in " : "out")} {ep.Kind,-12} " +
                     $"{(ep.OnVideoControl ? "VideoControl " : "VideoStreaming")}  " +
                     $"{ep.MaxPacketSize} bytes/packet{burst}{perInterval}");
        }

        // The only way a UVC device can say something it was not asked. Without one it can
        // answer questions and nothing more — no arriving message of any kind can reach the
        // host until the host happens to poll for it.
        bool canInterrupt = usb.Endpoints.Any(e => e.OnVideoControl && e.Kind == "interrupt");
        Log.Info(canInterrupt
            ? "    a VideoControl interrupt endpoint exists - the card can raise events unprompted"
            : "    no VideoControl interrupt endpoint - the card cannot raise an event at all, " +
              "it can only answer what it is asked");
    }

    // The device's whole control surface. Anything not listed here cannot be asked of it
    // at all — and a vendor channel such as HDMI CEC would have to arrive as an extension
    // unit, because UVC has nowhere else to put one.
    if (usb.ControlUnits.Count > 0)
    {
        Log.Info("  control surface (VideoControl interface):");
        foreach (var u in usb.ControlUnits)
        {
            Log.Info($"    {u.Kind,-16} id {u.Id,-3} {u.Detail}");
            if (u.ExtensionGuid is { } g) Log.Info($"                        guid {g}");
            if (u.Controls.Count > 0) Log.Info($"                        {string.Join(", ", u.Controls)}");
        }

        var extensions = usb.ControlUnits.Where(u => u.ExtensionGuid is not null).ToList();
        Log.Info(extensions.Count == 0
            ? "    no extension unit - the device declares no vendor channel at all, so there " +
              "is nothing to send a proprietary command through"
            : $"    {extensions.Count} extension unit(s) - a vendor channel exists, though what " +
              "it carries is undocumented");

        foreach (var xu in extensions) ProbeExtensionUnit(device, xu);
    }

    Log.Info($"  {usb.Formats.Count} format(s) declared by the hardware:");

    foreach (var f in usb.Formats)
    {
        var sizes = f.Frames
            .OrderByDescending(fr => fr.Width * fr.Height)
            .Select(fr => $"{fr.Width}x{fr.Height}@{(fr.Rates.Count > 0 ? fr.Rates[0].ToString("0.##") : "?")}")
            .Take(6);
        Log.Info($"    [{f.Index}] {f.Name,-8} {(f.Compressed ? "compressed" : "uncompressed"),-12} " +
                 $"{f.Frames.Count} frame sizes: {string.Join("  ", sizes)}");
    }

    if (usb.CeilingBytesPerSecond > 0)
    {
        double mib = usb.CeilingBytesPerSecond / (1024 * 1024);
        Log.Info($"  declared ceiling  {mib:0} MiB/s  ({usb.CeilingBytesPerSecond * 8 / 1e9:0.00} Gbit/s)");
        Log.Info($"    from: {usb.CeilingSource}");
        Log.Info($"  measured ceiling  {probe.Link.CeilingMiBs:0} MiB/s  (inferred from the mode list)");

        // The declared figure is what the endpoint can carry, the measured one is the
        // fastest thing actually offered. They should be close; a large gap means one of
        // the two is being read wrong and is worth knowing about.
        double ratio = probe.Link.CeilingMiBs > 0 ? mib / probe.Link.CeilingMiBs : 0;
        Log.Info(ratio is > 0.75 and < 1.35
            ? "    the two agree"
            : $"    they disagree by {ratio:0.00}x - one of the readings is wrong");
    }
    else
    {
        // Not a failure to read: bulk endpoints genuinely declare no reserved rate, so
        // there is no ceiling in the descriptors to compare against.
        Log.Info("  no isochronous endpoint - this card streams over bulk, which reserves " +
                 "no bandwidth and so declares no ceiling");
    }

    // With no declared ceiling the only physical check left is the link itself. The app's
    // bandwidth detector infers a ceiling from the mode list without ever asking the wire;
    // this is the first chance to say whether that inference is even possible.
    if (usb.PracticalBytesPerSecond > 0)
    {
        double wireMiBs = usb.PracticalBytesPerSecond / (1024 * 1024);
        Log.Info($"  the negotiated link carries roughly {wireMiBs:0} MiB/s of payload");
        Log.Info($"  measured ceiling  {probe.Link.CeilingMiBs:0} MiB/s  (inferred from the mode list)");
        Log.Info(probe.Link.CeilingMiBs <= wireMiBs
            ? "    it fits inside the link - the inferred ceiling is physically possible"
            : "    it exceeds the link - something in the mode list cannot be real");
    }

    CrossCheckCounts(usb, probe);
    ReportByResolution(usb, probe);
}

/// <summary>
/// The point of the exercise: is the app flagging the right <em>number</em> of invented
/// media types?
/// <para>
/// Asking whether a flagged mode exists uncompressed in hardware is the wrong question, and
/// answering it produced a badly misleading "39 false positives". Of course it exists — the
/// card offers 1440p60 as NV12 <em>and</em> as MJPEG, so Windows publishes the real NV12,
/// the MJPEG, and the MJPEG decoded back into NV12. Two NV12 entries where the hardware has
/// one. The right question is the surplus: hardware declares one NV12 stream at that
/// resolution and rate, Windows shows two, so exactly one is invented — and the app should
/// flag exactly one.
/// </para>
/// <para>
/// The comparison has to be on fourcc, geometry <em>and</em> rate. Comparing sizes alone
/// would call the 4K60 "NV12" mode genuine when it is exactly the invention being hunted,
/// and lumping the formats together would let the card's I420 stream excuse a second NV12.
/// </para>
/// </summary>
void CrossCheckCounts(UsbVideoInfo usb, DeviceProbe probe)
{
    Log.Info("  cross-check against the hardware's own frame lists:");
    Log.Info("    (the classification here is the two detectors alone - the app itself now " +
             "lets the descriptors overrule them, which would make this agree with itself)");

    int totalWindows = 0, totalGenuine = 0, totalFlagged = 0;
    var over = new List<string>();
    var under = new List<string>();

    // IsUsable, not IsOfferable: the redundant entries are exactly what this is counting,
    // and filtering them out first would make the check agree with itself by construction.
    foreach (var group in probe.Modes.Where(m => m.IsUsable)
                                     .GroupBy(m => (m.Format.Name, m.Width, m.Height, Fps: Math.Round(m.MaxFps, 1))))
    {
        var (name, w, h, fps) = group.Key;

        // How many streams of this exact fourcc the hardware declares at this size and rate.
        // Normally one; zero means nothing uncompressed runs this fast.
        int genuine = usb.Formats.Count(f => !f.Compressed &&
                                             string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase) &&
                                             f.Frames.Any(fr => fr.Width == w && fr.Height == h &&
                                                                fr.Rates.Any(r => Math.Abs(r - fps) < 0.6)));

        int windows = group.Count();
        int invented = Math.Max(0, windows - genuine);
        int flagged = group.Count(m => m.Transport == WireTransport.CompressedDecoded);

        totalWindows += windows;
        totalGenuine += Math.Min(windows, genuine);
        totalFlagged += flagged;

        if (flagged > invented) over.Add($"{w}x{h}@{fps:0.##} {name}");
        if (flagged < invented) under.Add($"{w}x{h}@{fps:0.##} {name}");
    }

    Log.Info($"    {totalWindows} uncompressed-looking mode(s) published by Windows");
    Log.Info($"    {totalGenuine} of them the hardware actually declares - " +
             $"{totalWindows - totalGenuine} must be invented");
    Log.Info($"    the app flagged {totalFlagged}");

    if (over.Count == 0 && under.Count == 0)
        Log.Info("      the count matches everywhere - every flag is accounted for by a real invention");
    if (over.Count > 0)
        Log.Warn($"      over-flagged at {over.Count} rate(s): {string.Join(", ", over.Take(6))}");
    if (under.Count > 0)
        Log.Warn($"      under-flagged at {under.Count} rate(s): {string.Join(", ", under.Take(6))}");

    // What the counting still cannot do. Where hardware has one NV12 and Windows shows two,
    // the surplus is proven but its identity is not: the adjacency rule flags whichever
    // entry precedes the MJPEG, and either of the pair could be the real one.
    Log.Info("    note: where hardware declares one stream and Windows shows two, the count is " +
             "provable but which entry is the fake one is not");
}

/// <summary>
/// The same comparison laid out one resolution at a time, which is the only form in which
/// the "false positive" story can be checked by eye.
/// <para>
/// Counting matters as much as rate here. Where the hardware offers a resolution both
/// uncompressed and as MJPEG, Windows publishes <em>three</em> entries — the real
/// uncompressed one, the MJPEG, and the MJPEG decoded back into an uncompressed subtype.
/// At most one of the two uncompressed-looking entries can be genuine, so a surplus is
/// proof of invention even when the rate itself is honest.
/// </para>
/// </summary>
void ReportByResolution(UsbVideoInfo usb, DeviceProbe probe)
{
    Log.Info("  resolution by resolution:");

    double MaxRate(bool compressed, int w, int h) =>
        usb.Formats.Where(f => f.Compressed == compressed)
                   .SelectMany(f => f.Frames)
                   .Where(fr => fr.Width == w && fr.Height == h)
                   .SelectMany(fr => fr.Rates)
                   .DefaultIfEmpty(0)
                   .Max();

    // How many streams of this exact fourcc the hardware declares at this size and rate —
    // the most genuine entries Windows could possibly be showing. Matching on the fourcc is
    // what makes it an answer: the card's I420 stream is not an excuse for a second NV12.
    int HardwareCount(string name, int w, int h, double fps) =>
        usb.Formats.Count(f => !f.Compressed &&
                               string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase) &&
                               f.Frames.Any(fr => fr.Width == w && fr.Height == h &&
                                                  fr.Rates.Any(r => Math.Abs(r - fps) < 0.6)));

    var resolutions = probe.Modes
        .Where(m => m.IsUsable)
        .Select(m => (m.Width, m.Height))
        .Distinct()
        .OrderByDescending(r => r.Width * (long)r.Height)
        .ToList();

    foreach (var (w, h) in resolutions)
    {
        double rawMax = MaxRate(compressed: false, w, h);
        double jpegMax = MaxRate(compressed: true, w, h);

        Log.Info($"    {w}x{h}   hardware: uncompressed to {rawMax:0.##} fps, mjpeg to {jpegMax:0.##} fps");

        var offered = probe.Modes
            .Where(m => m.IsUsable && m.Width == w && m.Height == h)
            .GroupBy(m => Math.Round(m.MaxFps, 1))
            .OrderByDescending(g => g.Key);

        foreach (var group in offered)
        {
            double fps = group.Key;
            foreach (var byFormat in group.GroupBy(m => m.Format.Name).OrderBy(g => g.Key))
            {
                int windows = byFormat.Count();
                int genuine = HardwareCount(byFormat.Key, w, h, fps);
                int invented = Math.Max(0, windows - genuine);
                int flagged = byFormat.Count(m => m.Transport == WireTransport.CompressedDecoded);
                string tags = string.Join(" + ", byFormat.Select(m => m.WireDetail));

                string verdict = genuine == 0
                    ? $"hardware has no {byFormat.Key} this fast - all {windows} are decoded MJPEG"
                    : invented == 0
                        ? "hardware has one, Windows shows one - genuine"
                        : $"hardware has {genuine}, Windows shows {windows} - {invented} invented";

                Log.Info($"      @{fps,-5:0.##} {byFormat.Key,-5} [{tags,-20}] flagged {flagged}/{invented}  {verdict}");
            }
        }
    }
}

/// <summary>
/// Whether Windows is routing capture through the frame server process. Absent means on:
/// the value only ever exists because someone turned it off deliberately.
/// </summary>
bool FrameServerEnabled()
{
    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
        @"SOFTWARE\Microsoft\Windows Media Foundation\Platform");
    return key?.GetValue("EnableFrameServerMode") is not int v || v != 0;
}

/// <summary>
/// One fixed measurement window, reported as percentiles rather than an average.
/// <para>
/// Averages are the wrong tool for a pipeline comparison: two configurations can share a
/// mean and differ entirely in the tail, and the tail is what is felt. The number that
/// carries the most here is <b>pipeline delay</b> — the sample's own capture timestamp
/// against the clock when <c>ReadSample</c> returned. Everything between the sensor and us
/// is inside it: the exposure, the USB transfer, any decode, and the hop out of the frame
/// server process into ours.
/// </para>
/// </summary>
void ReportBench(FramePump pump, TimeSpan window, TimeSpan cpu,
                 double[] gpuMs, double[] cpuMs, string scalerPath, string composition)
{
    var (delays, intervals) = pump.Recording();
    var d = delays.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
    var iv = intervals.Where(x => !double.IsNaN(x)).OrderBy(x => x).ToArray();
    var g = gpuMs.Where(x => x > 0).OrderBy(x => x).ToArray();
    var c = cpuMs.Where(x => x > 0).OrderBy(x => x).ToArray();

    static double P(double[] sorted, int pct) => sorted.Length == 0 ? 0
        : sorted[Math.Clamp((int)Math.Round(pct / 100.0 * (sorted.Length - 1)), 0, sorted.Length - 1)];

    Log.Section($"bench — {pump.CurrentMode?.Describe(pump.CurrentFps) ?? "no mode"}");
    Log.Info($"  path              {scalerPath},  present {composition}");
    Log.Info($"  frame server      {(FrameServerEnabled() ? "ON (Windows default)" : "OFF")}");
    Log.Info($"  window            {window.TotalSeconds:0.0} s, {intervals.Length} frames, " +
             $"{intervals.Length / window.TotalSeconds:0.0} fps");
    Log.Info($"  our process CPU   {cpu.TotalMilliseconds / window.TotalSeconds:0.0} ms per second, " +
             $"{cpu.TotalMilliseconds / Math.Max(1, intervals.Length):0.000} ms per frame");

    // How much of the delay is simply the frame crossing the wire. At the top modes this
    // card uses most of the link, so it is not a rounding term - it is nearly all of it,
    // and anything blamed on software has to be what is left after subtracting it.
    if (pump.CurrentMode is { IsCompressed: false } m)
    {
        double mib = m.Format.BytesPerFrame(m.Width, m.Height) / (1024.0 * 1024.0);
        double wireMiBs = (UsbDescriptors.Read(pump.Device)?.PracticalBytesPerSecond ?? 0) / (1024 * 1024);
        Log.Info(wireMiBs > 0
            ? $"  frame on the wire {mib:0.000} MiB, about {mib / wireMiBs * 1000:0.00} ms across a " +
              $"{wireMiBs:0} MiB/s link - the rest of the delay is everything else"
            : $"  frame on the wire {mib:0.000} MiB (link speed unknown)");
    }

    if (d.Length < 30)
        Log.Warn($"  pipeline delay    only {d.Length} trustworthy sample(s) - the source is not " +
                 "stamping on the system clock, so this run cannot be compared");
    else
        Log.Info($"  pipeline delay    min {P(d, 0):0.00}   p50 {P(d, 50):0.00}   p95 {P(d, 95):0.00}   " +
                 $"p99 {P(d, 99):0.00}   max {P(d, 100):0.00} ms   (n={d.Length})");

    if (iv.Length >= 30)
        Log.Info($"  arrival interval  min {P(iv, 0):0.00}   p50 {P(iv, 50):0.00}   p95 {P(iv, 95):0.00}   " +
                 $"p99 {P(iv, 99):0.00}   max {P(iv, 100):0.00} ms");

    if (g.Length >= 30)
        Log.Info($"  our GPU per frame min {P(g, 0):0.00}   p50 {P(g, 50):0.00}   p95 {P(g, 95):0.00}   " +
                 $"p99 {P(g, 99):0.00}   max {P(g, 100):0.00} ms");
    if (c.Length >= 30)
        Log.Info($"  our CPU per frame min {P(c, 0):0.00}   p50 {P(c, 50):0.00}   p95 {P(c, 95):0.00}   " +
                 $"p99 {P(c, 99):0.00}   max {P(c, 100):0.00} ms");

    var s = pump.Snapshot();
    Log.Info($"  dropped {s.FramesDropped}, late {s.FramesLate}");
}

/// <summary>
/// Asks a vendor extension unit what each of its controls is: readable, writable, how long,
/// and what it holds right now.
/// <para>
/// The descriptor says how many controls exist and nothing else. What they mean stays the
/// vendor's business, but shape is evidence — a sixteen-byte control that reads back a
/// plausible message frame is a different kind of thing from a one-byte flag, and neither
/// needs a datasheet to notice.
/// </para>
/// <para>Only GET is ever issued. Writing blind into an undocumented control is how an EDID
/// gets overwritten, and nothing here needs to write to learn anything.</para>
/// </summary>
void ProbeExtensionUnit(CaptureDeviceInfo device, UsbControlUnit xu)
{
    if (xu.ExtensionGuid is not { } guid) return;

    int count = Math.Max(xu.UnnamedControls, 1);
    Log.Info($"  probing extension unit {guid}, {count} control(s) - reads only:");

    var controls = ExtensionUnit.Probe(device, guid, count);
    if (controls is null) { Log.Warn("    the unit could not be reached"); return; }

    // Several reads, seconds apart, at the lengths already found. A control holding
    // configuration returns the same bytes every time; one reporting live state does not;
    // and one returning a different value on every single read is a third thing again —
    // a window onto something rather than a value. Without this they all look alike.
    var rounds = new List<List<XuControl>> { controls };
    for (int i = 0; i < 4; i++)
    {
        System.Threading.Thread.Sleep(700);
        if (ExtensionUnit.Reread(device, guid, controls) is { } round) rounds.Add(round);
    }
    var again = rounds.Count > 1 ? rounds[^1] : null;

    int answered = 0, live = 0;
    foreach (var c in controls)
    {
        string access = (c.CanGet ? "get" : "---") + "/" + (c.CanSet ? "set" : "---");
        if (c.Length == 0)
        {
            Log.Info($"    #{c.Selector,-3} {access}  {c.Error ?? "no answer"}");
            continue;
        }

        answered++;

        // How many distinct values across every round. One means fixed; a few means it
        // reports something that moves; as many as there were reads means the control is
        // not a value at all.
        int distinct = rounds
            .Select(r => r.FirstOrDefault(x => x.Selector == c.Selector)?.Current ?? [])
            .Select(Convert.ToHexString)
            .Distinct()
            .Count();
        bool changed = distinct > 1;
        if (changed) live++;
        string churn = distinct == 1 ? "    "
            : distinct >= rounds.Count ? "NEW " : "LIVE";

        // Long buffers are dumped truncated: the first 48 bytes carry any header worth
        // recognising, and 256 bytes of zeroes across a log line teaches nothing.
        var shown = c.Current.AsSpan(0, Math.Min(48, c.Current.Length));
        var hex = Convert.ToHexString(shown);
        var ascii = new string(shown.ToArray().Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.').ToArray());
        bool allZero = c.Current.All(b => b == 0);

        Log.Info($"    #{c.Selector,-3} {access}  {c.Length,4} byte(s) {churn} {distinct}/{rounds.Count}  " +
                 (allZero ? "all zero" : $"{hex}{(c.Current.Length > 48 ? "..." : "")}  |{ascii}|"));
    }

    Log.Info($"    {answered} of {controls.Count} answered over {rounds.Count} reads; " +
             $"{live} returned more than one value");
    if (answered == 0)
        Log.Warn("    nothing answered - the unit is declared but not reachable through KS, " +
                 "which usually means Windows needs a registry mapping for this GUID");
}

/// <summary>Everything one device turned out to be, in the log and on stdout.</summary>
void Report(DeviceProbe p)
{
    var d = p.Device;
    Log.Section($"[{d.Ordinal}] {d.FriendlyName}");
    Log.Info($"  path   {d.ShortId}{(d.IsVirtual ? "   (software device - its mode list describes no wire)" : "")}");

    if (!p.IsReady)
    {
        Log.Warn($"  state  {p.Availability.ToString().ToLowerInvariant()} - {p.Problem}");
        return;
    }

    int renderable = p.Renderable.Count();
    Log.Info($"  state  ready - {p.Modes.Count} modes, {renderable} renderable");

    int hidden = p.Modes.Count(m => m.IsRedundant);
    if (hidden > 0)
        Log.Info($"  hidden {hidden} codec mode(s) that duplicate an uncompressed twin exactly - " +
                 $"{p.Modes.Count(m => m.IsOfferable)} offered");
    Log.Info($"  wire   {p.Count(WireTransport.Raw)} raw / " +
             $"{p.Count(WireTransport.CompressedDecoded)} compressed-but-labelled-raw / " +
             $"{p.Count(WireTransport.Compressed)} compressed");

    // A software device reports whatever its author felt like; running bandwidth arithmetic
    // on it produces a confident number about a wire that does not exist.
    Log.Info(d.IsVirtual ? "  link   n/a for a software device" : $"  link   {p.Link.Describe()}");

    if (p.BandwidthOnlyDetections > 0)
        Log.Warn($"  infer  {p.BandwidthOnlyDetections} mode(s) claim to be uncompressed but do not fit " +
                 $"the {p.Link.CeilingMiBs:0} MiB/s link - treated as compressed on that basis alone");

    // Where the hardware speaks for itself there is nothing left to infer, so it is worth
    // saying how much of the classification is fact and how much is still reasoning.
    if (p.DescriptorsRead)
    {
        Log.Info($"  descr  {p.DescriptorBacked} of {p.Modes.Count} mode(s) settled by the device's own descriptors");
        if (p.DescriptorCorrections > 0)
            Log.Warn($"         {p.DescriptorCorrections} mode(s) were reclassified - the detectors " +
                     "had them wrong and the hardware overruled them");
    }

    if (p.WireMiBs > 0)
        Log.Info($"  carry  {p.WireMiBs:0} MiB/s" +
                 (Math.Abs(p.WireMiBs - p.Link.CeilingMiBs) > 1
                     ? $" (the inferred ceiling of {p.Link.CeilingMiBs:0} is the fastest mode offered, not the wire)"
                     : ""));

    if (p.BestRaw is { } best)
        Log.Info($"  best   {best.Width}x{best.Height} @ {best.MaxFps:0.##} {best.Format.Name} " +
                 $"[{best.WireDetail}]  {best.MegabytesPerSecond(best.MaxFps):0} MiB/s, " +
                 $"{best.WireMs(p.WireMiBs):0.0} ms on the wire");
    else
        Log.Warn("  best   no uncompressed renderable mode - every option has a codec in the path");

    // 10-bit is not a free quality setting: P010 is 3 bytes a pixel against NV12's 1.5, so
    // it is twice the frame and twice the wire, and measured it also turns a clean delivery
    // into a hitching one - p99 of 73 ms against 11.5 at the same size and rate.
    if (p.SupportsTenBit && p.Renderable.FirstOrDefault(m => m.Format.IsHighBitDepth) is { } ten)
        Log.Info($"  10-bit yes - HDR capture possible, at {ten.WireMs(p.WireMiBs):0.0} ms on the wire " +
                 $"for {ten.Width}x{ten.Height}, twice the 8-bit cost");
    else
        Log.Info("  10-bit no - SDR capture only");
}

void RunLoop()
{
    var logClock = System.Diagnostics.Stopwatch.StartNew();
    var frameClock = System.Diagnostics.Stopwatch.StartNew();
    var sampleClock = System.Diagnostics.Stopwatch.StartNew();
    var retryClock = System.Diagnostics.Stopwatch.StartNew();
    var benchClock = System.Diagnostics.Stopwatch.StartNew();
    bool benchRecording = false;
    TimeSpan benchCpuStart = TimeSpan.Zero;
    // Our own per-present cost, sampled beside the capture delay so the whole budget can
    // be read off one report instead of two.
    var benchGpu = new List<double>();
    var benchCpu = new List<double>();
    var audioRetryClock = System.Diagnostics.Stopwatch.StartNew();
    var displayClock = System.Diagnostics.Stopwatch.StartNew();
    var saveClock = System.Diagnostics.Stopwatch.StartNew();
    string lastSavedJson = settings.ToJson();
    long presented = 0, lastLoggedPresent = 0;
    long lastTimedArrival = 0, newFramesPresented = 0, lastLoggedNewFrames = 0;
    double appCpuMs = 0;
    // Frames that took us longer than a whole frame period to get from arrival to Present.
    // The card's own late frames are counted by the pump; these are ours, and the two
    // have to be told apart before anyone can say whose stutter it is.
    long slowFrames = 0;
    // What the display actually did between two heartbeats, from DXGI's own counters.
    uint lastSyncRefresh = 0;
    long lastSyncQpc = 0;
    Log.Info(presentOnArrival
        ? "pacing: one present per captured frame - with VRR the display refreshes when a frame arrives"
        : "pacing: present on every swapchain slot, stale frames included (--pacing=refresh)");
    // Seeded from the pump rather than from Idle, so the first frame is not announced as
    // a recovery from a problem that never happened.
    var lastState = pump!.State;

    while (!quit)
    {
        if (!Win32.PumpMessages()) break;

        // Requests from the message pump and from the UI are applied here, on the thread
        // that owns the device, and never allowed to escape as an exception.
        if (pendingDeviceSwitch is { } target)
        {
            pendingDeviceSwitch = null;
            try { SwitchDevice(target); } catch (Exception ex) { Log.Error("device switch failed", ex); }
        }
        if (pendingAudioRestart)
        {
            pendingAudioRestart = false;
            try
            {
                model!.Audio.Stop();
                if (model.AudioEnabled) model.Audio.Start(model.AudioInputId, model.AudioOutputId);
            }
            catch (Exception ex) { Log.Error("audio restart failed", ex); }
        }
        if (pendingWindowKind is { } wantKind)
        {
            pendingWindowKind = null;
            // Logged either side, because the whole reason this control exists is to find
            // out whether it moves the swapchain onto an overlay plane. Skipped before the
            // first Present, when DXGI has no statistics to give and would only say so.
            if (presented > 0) Log.Info($"present before: {renderer!.DescribeCompositionMode()}");
            try { windowMode!.Set(wantKind); model!.WindowKind = windowMode.Kind; }
            catch (Exception ex) { Log.Error("window mode switch failed", ex); }
        }
        if (pendingScaler >= 0)
        {
            int want = pendingScaler; pendingScaler = -1;
            try { SelectScaler(want); } catch (Exception ex) { Log.Error("scaler switch failed", ex); }
        }
        if (pendingVsrToggle) { pendingVsrToggle = false; try { ToggleVsr(); } catch (Exception ex) { Log.Error("VSR toggle failed", ex); } }
        if (pendingHdrToggle) { pendingHdrToggle = false; try { ToggleHdr(); } catch (Exception ex) { Log.Error("HDR toggle failed", ex); } }
        if (pendingNisToggle) { pendingNisToggle = false; try { ToggleNis(); } catch (Exception ex) { Log.Error("NIS toggle failed", ex); } }
        if (pendingSharpness != 0f && renderer!.Nis is { } nis)
        {
            nis.Sharpness = Math.Clamp(nis.Sharpness + pendingSharpness, 0f, 1f);
            pendingSharpness = 0f;
            Log.Info($"NIS sharpness {nis.Sharpness:0.0}");
        }

        // Wait for the swapchain first so we never queue ahead of the display, then for a
        // frame from the card.
        //
        // The frame wait used to give up after 8 ms. The card delivers every 8.33 ms at
        // 120 fps, so it gave up just before most frames, presented the old one, and then
        // the new one a fraction of a millisecond later: about 180 presents a second for
        // 120 pictures. On a VRR display every present is a refresh, so the screen was
        // refreshed at uneven intervals and the second present tore into the first.
        //
        // Now it presents once per captured frame, and with VRR the display refreshes
        // exactly when a picture arrives. With nothing arriving — no signal, the picker,
        // a mode switch — the interface still has to move, so it falls back to ~60 Hz.
        // Input does not need a present of its own: it waits at most a frame for the next
        // picture, which carries the redrawn overlay with it.
        renderer!.WaitForNextFrame(8);
        if (presentOnArrival)
            pump!.FrameReady.WaitOne(pump.State == CaptureState.Streaming ? 50 : 16);
        else
            pump!.FrameReady.WaitOne(8);

        // Time our own segment: from the frame landing in our process to Present returning.
        // Only frames we have not presented before count — we present faster than the
        // capture rate, and re-presenting a stale frame would inflate the figure.
        long arrival = System.Threading.Volatile.Read(ref pump.LastArrivalQpc);
        bool isNewFrame = arrival != lastTimedArrival;

        float dt = (float)frameClock.Elapsed.TotalSeconds;
        frameClock.Restart();

        model!.AppCpuMs = appCpuMs;
        model.GpuMs = renderer.LastGpuMs;
        if (benchRecording) { benchGpu.Add(renderer.LastGpuMs); benchCpu.Add(appCpuMs); }
        renderer.OverlayNits = model.OverlayNits;
        renderer.VideoNits = model.VideoNits;
        // A pending capture holds the panel open: otherwise the idle timer hides it before
        // the shot lands and every capture shows an empty frame.
        if (shotPath is not null) model.PanelActivity();

        AppShell.Tick(model, dt * 1000.0, advance: forcedScreen is null);

        // The pointer goes when the bar goes. WM_SETCURSOR keeps it hidden once the bar
        // is away, but it only arrives when the mouse moves — which brings the bar back —
        // so the moment of hiding has to be acted on here. Only while the pointer is over
        // our own client area: the cursor is shared, and blanking it over another window
        // would be someone else's problem to explain.
        if (model.CursorHidden && !cursorWasHidden && PointerOverClient())
            Win32.SetCursor(IntPtr.Zero);
        cursorWasHidden = model.CursorHidden;

        // The graph keeps its 5 Hz trace, but the numbers beside it are only replaced once
        // a second. Repainting them at frame rate left every trailing digit permanently in
        // motion, which reads as instability rather than as measurement.
        if (sampleClock.ElapsedMilliseconds >= 200)
        {
            sampleClock.Restart();
            model.History.Push((float)(appCpuMs + renderer.LastGpuMs));
        }
        if (displayClock.ElapsedMilliseconds >= 1000)
        {
            displayClock.Restart();
            model.SampleForDisplay(renderer.DescribeCompositionMode());
        }

        if (renderer.BeginUi(dt)) AppShell.Draw(model, renderer.Ui!.Fonts);

        renderer.Render();
        renderer.Present();
        presented++;

        // Deferred until the video processor exists, which only happens once a frame has
        // been through it. Both the remembered scaler and HDR need it.
        // Gated on the processor, not on HDR: an SDR display still has a scaler to restore.
        if (deferredStatePending && renderer.Processor is { OutputWidth: > 0 })
        {
            deferredStatePending = false;
            // --scaler wins over what was remembered: a measurement needs the scaler it
            // asked for, not the one left over from the last session.
            int wantScaler = forcedScaler >= 0 ? forcedScaler : settings.Scaler;
            if (wantScaler != 0 || forcedScaler == 0)
            {
                try { SelectScaler(wantScaler); }
                catch (Exception ex) { Log.Error("restoring the scaler failed", ex); }
            }
            // Only the driver path can produce HDR, so a remembered NIS choice takes it away.
            if (autoHdrPending && settings.Hdr && !renderer.UseNis)
            {
                try { ToggleHdr(); } catch (Exception ex) { Log.Error("auto HDR failed", ex); }
            }
        }

        // Audio drifts because capture and playback run off different clocks. Checked
        // every frame; it does nothing at all until the buffer has actually grown.
        model!.Audio.TrimDrift();

        // Sound is expected to come back on its own. The HDMI audio endpoint disappears
        // whenever the console sleeps and returns when it wakes, and a one-shot attempt at
        // start-up leaves the user with silence and no way back short of a restart.
        if (model.AudioEnabled && !model.Audio.Running && audioRetryClock.ElapsedMilliseconds >= 5000)
        {
            audioRetryClock.Restart();
            try
            {
                RefreshAudioEndpoints();
                // Re-match rather than trusting the remembered id: after a sleep the
                // endpoint often comes back under a different one.
                if (model.AudioInputId is null || model.AudioInputs.All(e => e.Id != model.AudioInputId))
                    model.AudioInputId = AudioEndpoints.MatchTo(pump.Device)?.Id;

                if (model.AudioInputId is not null) model.Audio.Start(model.AudioInputId, model.AudioOutputId);
            }
            catch (Exception ex) { Log.Debug($"audio retry failed: {ex.Message}"); }
        }
        else if (model.Audio.Running) audioRetryClock.Restart();

        if (isNewFrame && arrival != 0)
        {
            lastTimedArrival = arrival;
            newFramesPresented++;
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - arrival) * 1000.0
                        / System.Diagnostics.Stopwatch.Frequency;
            if (ms is > 0 and < 200) appCpuMs = appCpuMs == 0 ? ms : appCpuMs * 0.9 + ms * 0.1;
            if (pump.CurrentFps > 0 && ms > 1000.0 / pump.CurrentFps && ms < 200) slowFrames++;
        }

        // A lost device does not come back on its own — the read loop has stopped — so it
        // is retried on the cadence the no-signal card promises. "No signal" is different
        // and needs nothing: the reader is alive and recovers by itself when frames resume.
        if (pump.State == CaptureState.Lost && retryClock.ElapsedMilliseconds >= 3000)
        {
            retryClock.Restart();
            Log.Info($"retrying '{pump.Device.FriendlyName}'");
            try { SwitchDevice(pump.Device); } catch (Exception ex) { Log.Error("retry failed", ex); }
        }
        else if (pump.State != CaptureState.Lost) retryClock.Restart();

        // The video simply freezing on the last frame is the one failure a user cannot
        // diagnose from the picture, so it gets said out loud the moment it happens.
        var state = pump.State;
        if (state != lastState)
        {
            lastState = state;
            if (state == CaptureState.NoSignal)
                Log.Warn($"no signal from '{pump.Device.FriendlyName}' - source asleep, unplugged, or changing mode");
            else if (state == CaptureState.Lost)
                Log.Error($"device lost: {pump.StateDetail ?? "unknown"}");
            else if (state == CaptureState.Streaming)
                Log.Info("signal restored");
        }

        // Diagnostic capture: give the pipeline a few seconds to settle into a steady
        // state, grab one frame, and stop.
        if (shotPath is not null && presented > 400)
        {
            renderer.SaveBackBuffer(shotPath);
            shotPath = null;
            quit = true;
        }

        // Fixed measurement, then exit. The warm-up is not politeness: the first second
        // after a mode starts is the device locking on and the driver settling, and
        // averaging that in would blame or flatter whatever is being compared.
        if (benchSeconds > 0)
        {
            if (!benchRecording && pump.State == CaptureState.Streaming && presented > 300)
            {
                benchRecording = true;
                pump.StartRecording(500_000);
                benchCpuStart = System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime;
                benchClock.Restart();
                Log.Info($"bench: warm, recording for {benchSeconds:0.#} s");
            }
            else if (benchRecording && benchClock.Elapsed.TotalSeconds >= benchSeconds)
            {
                string path = renderer.NisEnabled ? "NIS"
                    : renderer.Processor?.SuperResolutionEnabled == true ? "RTX VSR"
                    : "no scaler";
                if (renderer.Processor?.HdrEnabled == true) path += " + RTX HDR";

                ReportBench(pump, benchClock.Elapsed,
                            System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime - benchCpuStart,
                            [.. benchGpu], [.. benchCpu], path, renderer.DescribeCompositionMode());
                quit = true;
            }
        }

        // Settings are compared rather than tracked. Threading a "changed" flag through
        // every control is a thing to forget in one place and then wonder about; taking a
        // snapshot and writing only when it differs cannot drift out of sync with reality.
        if (saveClock.ElapsedMilliseconds >= (model.SettingsDirty ? 400 : 4000))
        {
            saveClock.Restart();
            model.SettingsDirty = false;
            model.CaptureInto(settings);
            var json = settings.ToJson();
            if (json != lastSavedJson) { settings.Save(); lastSavedJson = json; }
        }

        // Heartbeat: the evidence that a mode switch actually took effect, independent
        // of anything drawn on screen. Present rate and capture rate are logged
        // separately because they are allowed to differ.
        if (logClock.ElapsedMilliseconds >= 1000)
        {
            double seconds = logClock.Elapsed.TotalSeconds;
            logClock.Restart();
            var s = pump.Snapshot();
            var m = pump.CurrentMode;
            double presentFps = (presented - lastLoggedPresent) / seconds;
            double uniqueFps = (newFramesPresented - lastLoggedNewFrames) / seconds;
            lastLoggedPresent = presented;
            lastLoggedNewFrames = newFramesPresented;

            double budgetMs = pump.CurrentFps > 0 ? 1000.0 / pump.CurrentFps : 0;

            // DXGI's vblank count over the last second. Logged, not shown: on a 175 Hz
            // panel with VRR on it read 175.0 in every run, whether we presented 120 or 157
            // times a second, and nothing documents whether this counter follows a variable
            // refresh at all. It cannot tell whether VRR engaged, so it does not get to
            // look as though it can; NVIDIA's G-SYNC indicator or the monitor's own
            // refresh readout can.
            uint syncRefresh = renderer!.LastSyncRefreshCount;
            long syncQpc = renderer.LastScanoutQpc;
            double vblankHz = lastSyncQpc != 0 && syncQpc > lastSyncQpc && syncRefresh > lastSyncRefresh
                ? (syncRefresh - lastSyncRefresh) / ((syncQpc - lastSyncQpc) / (double)System.Diagnostics.Stopwatch.Frequency)
                : 0;
            lastSyncRefresh = syncRefresh;
            lastSyncQpc = syncQpc;

            var age = s.PipelineDelayValid && budgetMs > 0
                ? $"{s.PipelineDelayMs,5:0.0}ms/{s.PipelineDelayMs / budgetMs:0.00}fr"
                : "        n/a";

            Log.Info(
                $"#{m?.TypeIndex,-3} {m?.Width}x{m?.Height} {pump.ActiveSubtype,-5} " +
                $"{FramePump.DescribeTransport(m!),-38} " +
                $"budget {budgetMs,5:0.00}ms  age {age}  " +
                $"cpu {appCpuMs,5:0.00}  gpu {renderer!.LastGpuMs,5:0.00}  " +
                $"free {budgetMs - appCpuMs - renderer.LastGpuMs,5:0.00}ms  " +
                $"capture {s.MeasuredFps,6:0.0}fps  present {presentFps,6:0.0}fps (unique {uniqueFps,5:0.0}, " +
                $"x{(uniqueFps > 0 ? presentFps / uniqueFps : 0),4:0.00})  " +
                $"vblank {(vblankHz > 0 ? $"{vblankHz,5:0.0}Hz" : "  n/a")}  " +
                $"late {s.FramesLate} slow {slowFrames} dropped {s.FramesDropped}  {model.Composition}");
        }
    }
}

/// <summary>
/// The profile the whole project aims at: no codec in the path, fastest, then largest.
/// A card with nothing raw still gets opened — a compressed picture beats no picture, and
/// the readout says plainly which one is on screen.
/// </summary>
/// <summary>
/// Starts on a named mode, as <c>1280x720@60</c>. Only for measurement: comparing two runs
/// means pinning the mode, and "whatever was remembered" is not a control.
/// </summary>
static MenuEntry? ForcedMode(DeviceProbe probe, string? spec)
{
    if (string.IsNullOrWhiteSpace(spec)) return null;

    // Optional format suffix, as 1920x1080@60:P010. Needed because two formats share a
    // geometry and rate and differ enormously in bytes - which is the point of measuring.
    string? want = null;
    if (spec.Split(':') is [var head, var fmt]) { spec = head; want = fmt; }

    var at = spec.Split('@');
    var wh = at[0].Split('x', 'X');
    if (wh.Length != 2 || !int.TryParse(wh[0], out int w) || !int.TryParse(wh[1], out int h))
    {
        Log.Warn($"--mode={spec}: expected WIDTHxHEIGHT[@FPS]");
        return null;
    }
    double fps = at.Length > 1 && double.TryParse(at[1], System.Globalization.NumberStyles.Float,
                                                  System.Globalization.CultureInfo.InvariantCulture,
                                                  out double f) ? f : 0;

    // Uncompressed first: a measurement of the pipeline wants the path without a decoder
    // in it, and both entries exist at most sizes.
    var match = probe.Renderable
        .Where(m => m.Width == w && m.Height == h && m.IsOfferable)
        .Where(m => fps <= 0 || (fps >= m.MinFps - 0.5 && fps <= m.MaxFps + 0.5))
        .Where(m => want is null || m.Format.Name.Equals(want, StringComparison.OrdinalIgnoreCase))
        .OrderBy(m => m.HasCodecInPath ? 1 : 0)
        .ThenByDescending(m => m.MaxFps)
        .FirstOrDefault();

    if (match is null)
    {
        Log.Warn($"--mode={spec}: no renderable mode matches");
        return null;
    }

    double pick = fps > 0 ? fps : match.MaxFps;
    Log.Info($"--mode={spec} -> #{match.TypeIndex} {match.Width}x{match.Height} @ {pick:0.##} " +
             $"{match.Format.Name} [{match.WireDetail}]");
    return new MenuEntry(match, pick);
}

static MenuEntry? PickInitialMode(DeviceProbe probe)
{
    var best = probe.BestRaw ?? probe.Renderable
        .OrderByDescending(m => m.MaxFps)
        .ThenByDescending(m => m.Width * m.Height)
        .FirstOrDefault();
    return best is null ? null : new MenuEntry(best, best.MaxFps);
}

static List<MenuEntry> BuildMenuEntries(List<CaptureMode> modes)
{
    // Only formats we can actually put on screen. The raw/compressed comparison survives
    // this filter intact: every compressed mode still has a decoded twin, so raw and
    // compressed can be A/B'd at identical output format and resolution.
    var entries = new List<MenuEntry>();
    foreach (var m in modes.Where(m => m.IsOfferable)
                           .OrderByDescending(m => m.Width * m.Height)
                           .ThenByDescending(m => m.MaxFps))
        entries.Add(new MenuEntry(m, m.MaxFps));
    return entries;
}

/// <summary>
/// Probes and moves to another device. Probing afresh rather than trusting a cached result
/// matters: the usual reason a device was unusable is that a browser tab had it, and the
/// usual reason the user is picking it now is that they just closed the tab.
/// </summary>
void SwitchDevice(CaptureDeviceInfo target)
{
    // Release ours first. Two capture sources open at once is exactly the contention this
    // is trying to escape, and on a shared USB controller it can cost the other one frames.
    pump!.Stop();

    var fresh = Remember(CaptureDevice.Probe(target));
    if (!fresh.IsReady)
    {
        Log.Warn($"cannot switch to '{target.FriendlyName}': {fresh.Problem}");
        // Put the old device back the way it was, or the user is left with a frozen window.
        if (active?.IsReady == true && PickInitialMode(active) is { } restore)
            pump.Select(restore.Mode, restore.Fps);
        return;
    }

    var entry = PickInitialMode(fresh);
    if (entry is null) { Log.Warn($"'{target.FriendlyName}' has no renderable mode"); return; }

    pump.SwitchTo(fresh.Device);
    active = fresh;
    if (model is not null) model.Active = fresh;
    menuEntries = BuildMenuEntries(fresh.Modes);
    Report(fresh);
    pump.Select(entry.Mode, entry.Fps);
}

/// <summary>
/// The Win32 mode menu. Kept as a fallback for the case where ImGui failed to start —
/// the app is still usable then, just plainer.
/// </summary>
void ShowModeMenu(int screenX, int screenY)
{
    var menu = Win32.CreatePopupMenu();
    var submenus = new List<IntPtr>();

    // Re-enumerated on every open, so a card plugged in mid-session is simply there.
    try { allDevices = CaptureDevice.Enumerate(); if (model is not null) model.Devices = allDevices; }
    catch (Exception ex) { Log.Warn($"device re-enumeration failed: {ex.Message}"); }

    if (allDevices.Count > 1)
    {
        var devMenu = Win32.CreatePopupMenu();
        submenus.Add(devMenu);
        for (int i = 0; i < allDevices.Count; i++)
        {
            var d = allDevices[i];
            // Never opened means never opened: showing a guess here would be a claim we
            // have not earned, and opening every device to fill in a menu is rude.
            var state = probed.TryGetValue(d.SymbolicLink, out var p)
                ? p.Availability switch
                {
                    DeviceAvailability.Ready => $"{p.Renderable.Count()} modes, {p.Link.CeilingMiBs:0} MiB/s",
                    DeviceAvailability.Busy => "in use elsewhere",
                    DeviceAvailability.Unusable => "nothing renderable",
                    _ => "unavailable"
                }
                : d.IsVirtual ? "software device, not checked" : "not checked yet";

            uint flags = Win32.MF_STRING;
            if (pump?.Device.Is(d) == true) flags |= Win32.MF_CHECKED;
            Win32.AppendMenuW(devMenu, flags, (UIntPtr)(9100 + i), $"{d.FriendlyName}   ({state})");
        }
        Win32.AppendMenuW(menu, Win32.MF_POPUP, (UIntPtr)(long)devMenu, "Capture device");
        Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, UIntPtr.Zero, null);
    }

    foreach (var group in menuEntries.GroupBy(e => (e.Mode.Width, e.Mode.Height))
                                     .OrderByDescending(g => g.Key.Width * g.Key.Height))
    {
        var sub = Win32.CreatePopupMenu();
        submenus.Add(sub);

        foreach (var e in group.OrderByDescending(e => e.Fps))
        {
            int id = menuEntries.IndexOf(e) + 1;
            var m = e.Mode;
            var bw = m.Transport == WireTransport.Raw ? $"{m.MegabytesPerSecond(e.Fps):0} MiB/s" : "compressed";
            var label = $"{e.Fps,6:0.##} fps   {m.Format.Name,-6} {m.WireDetail,-8} {bw}";

            uint flags = Win32.MF_STRING;
            if (pump?.CurrentMode?.TypeIndex == m.TypeIndex && Math.Abs(pump.CurrentFps - e.Fps) < 0.5)
                flags |= Win32.MF_CHECKED;

            Win32.AppendMenuW(sub, flags, (UIntPtr)id, label);
        }

        var raw = group.Count(e => e.Mode.Transport == WireTransport.Raw);
        Win32.AppendMenuW(menu, Win32.MF_POPUP, (UIntPtr)(long)sub,
            $"{group.Key.Width}x{group.Key.Height}   ({raw} raw / {group.Count() - raw} compressed)");
    }

    Win32.AppendMenuW(menu, Win32.MF_SEPARATOR, UIntPtr.Zero, null);
    Win32.AppendMenuW(menu, Win32.MF_STRING, (UIntPtr)9999, "Quit");

    Win32.SetForegroundWindow(hwnd);
    int chosen = Win32.TrackPopupMenu(menu,
        Win32.TPM_LEFTALIGN | Win32.TPM_TOPALIGN | Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON,
        screenX, screenY, 0, hwnd, IntPtr.Zero);

    foreach (var s in submenus) Win32.DestroyMenu(s);
    Win32.DestroyMenu(menu);

    if (chosen == 9999) { quit = true; return; }
    if (chosen is >= 9100 and < 9200)
    {
        // Queued like every other device-touching action: opening a capture source from
        // inside a menu callback runs driver work on the message pump.
        int i = chosen - 9100;
        if (i < allDevices.Count && pump?.Device.Is(allDevices[i]) != true)
            pendingDeviceSwitch = allDevices[i];
        return;
    }
    if (chosen >= 1 && chosen <= menuEntries.Count)
    {
        var e = menuEntries[chosen - 1];
        try { pump!.Select(e.Mode, e.Fps); }
        catch (Exception ex) { Log.Error($"switch to #{e.Mode.TypeIndex} failed", ex); }
    }
}

IntPtr WindowProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
{
    // Any exception escaping here crosses a native callback boundary and terminates the
    // process instead of unwinding. Nothing in a window message is worth that.
    try { return WindowProcCore(h, msg, wParam, lParam); }
    catch (Exception ex)
    {
        Log.Error($"window message {msg:X} handler failed", ex);
        return Win32.DefWindowProcW(h, msg, wParam, lParam);
    }
}

IntPtr WindowProcCore(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
{
    if (renderer?.Ui is { Available: true } ui)
    {
        // WM_SETCURSOR is the only place a cursor change sticks; Windows resets it on
        // every move, so ImGui's requested cursor has to be re-applied from here.
        const int HTCLIENT = 1;
        if (msg == Win32.WM_SETCURSOR && Win32.LoWord(lParam) == HTCLIENT && model is { CursorHidden: true })
        {
            Win32.SetCursor(IntPtr.Zero);
            return 1;
        }
        if (msg == Win32.WM_SETCURSOR && Win32.LoWord(lParam) == HTCLIENT && ui.WantsMouse)
        {
            ImGuiInput.ApplyCursor(ui);
            return 1;
        }

        // A click that landed on a panel has been consumed and must not also reach the
        // video underneath, or the right-click menu opens behind the settings window.
        if (ImGuiInput.Handle(ui, h, msg, wParam, lParam)) return IntPtr.Zero;
    }

    switch (msg)
    {
        case Win32.WM_CLOSE:
            // The title-bar X asks too, so there is exactly one way out of the app and it
            // always confirms. Asking twice goes through, so nobody is ever trapped.
            if (model is { ConfirmQuit: false })
            {
                model.ConfirmQuit = true;
                return IntPtr.Zero;
            }
            quit = true;
            Win32.PostQuitMessage(0);
            return IntPtr.Zero;

        case Win32.WM_DESTROY:
            quit = true;
            Win32.PostQuitMessage(0);
            return IntPtr.Zero;

        case Win32.WM_SIZE:
            Layout.DisplayHeight = Win32.MonitorHeight(h);
            renderer?.Resize(Win32.LoWord(lParam), Win32.HiWord(lParam));
            return IntPtr.Zero;

        // The interface is sized for the monitor the window is on, so it has to know when
        // the window has been dragged to another one, or the display itself has changed.
        case Win32.WM_MOVE:
        case Win32.WM_DISPLAYCHANGE:
            Layout.DisplayHeight = Win32.MonitorHeight(h);
            break;

        // The window's own menu, from a right-click on the title bar or Alt+Space. With the
        // corner gear gone this is the only way back to the panel that does not need a
        // hotkey to be known first. Windows uses the low four bits of wParam itself, hence
        // the mask.
        case Win32.WM_SYSCOMMAND when ((int)wParam & 0xFFF0) == Win32.SC_APP_SETTINGS:
            if (model is not null) { model.Flyout = Flyout.None; model.ToggleFlyout(Flyout.More); }
            return IntPtr.Zero;

        // Any real movement of the pointer over the picture brings the bar up for five
        // seconds. A gamepad never sends this, so playing never does.
        case Win32.WM_MOUSEMOVE:
            if (model is not null && lParam != lastPointer)
            {
                bool wasHidden = model.CursorHidden;
                lastPointer = lParam;
                model.PointerMoved();
                // WM_SETCURSOR for this same movement came first and blanked it, so the
                // pointer would stay invisible until the next move without this.
                if (wasHidden) Win32.SetCursor(Win32.LoadCursorW(IntPtr.Zero, Win32.IDC_ARROW));
            }
            return IntPtr.Zero;

        case Win32.WM_KEYDOWN:
            HandleKey((int)wParam, h);
            return IntPtr.Zero;

        case Win32.WM_RBUTTONDOWN:
            OpenMenuAt(h, Win32.LoWord(lParam), Win32.HiWord(lParam));
            return IntPtr.Zero;
    }
    return Win32.DefWindowProcW(h, msg, wParam, lParam);
}

// The hotkeys the design specifies. Device work is queued rather than done here: touching
// D3D from inside a window callback runs driver work on the message pump.
void HandleKey(int vk, IntPtr h)
{
    // A keypress is someone using the app, so the panel's idle timer starts over.
    model?.PanelActivity();

    switch (vk)
    {
        case Win32.VK_ESCAPE:
            // Esc never closes the app. It backs out of the innermost thing that is open,
            // and once nothing is nested it toggles the bar: same key in, same key out.
            if (model is { ConfirmQuit: true }) { model.ConfirmQuit = false; break; }
            if (model is not null && model.Flyout != Flyout.None) { model.Flyout = Flyout.None; break; }
            // From the picker it goes back to the picture, if there is one to go back to.
            if (model is { Screen: Screen.Devices } && pump?.State == CaptureState.Streaming) { model.Screen = Screen.App; break; }
            if (model is not null) ToggleBar(model);
            break;

        case 0x09: if (model is not null) ToggleBar(model); break;   // Tab
        case 'T': model?.CycleTelemetry(); break;
        case 'M': model?.ToggleFlyout(Flyout.Mode); break;
        case 'H': pendingHdrToggle = true; break;
        // F11 walks the three window modes, in the order the panel lists them.
        case 0x7A: pendingWindowKind = windowMode?.Kind switch
            {
                WindowKind.Windowed => WindowKind.BorderlessFullscreen,
                WindowKind.BorderlessFullscreen => WindowKind.Fullscreen,
                _ => WindowKind.Windowed
            }; break;

        case '1': pendingScaler = 0; break;
        case '2': pendingScaler = 1; break;
        case '3': pendingScaler = 2; break;

        // The original keys, kept because they are in muscle memory by now.
        case 'V': pendingVsrToggle = true; break;
        case 'N': pendingNisToggle = true; break;
        case Win32.VK_F1: OpenMenuAt(h, 24, 24); break;
        // Sharpness at 0 isolates what the scaler itself does, with no USM on top.
        case 0xDB: pendingSharpness = -0.1f; break; // [
        case 0xDD: pendingSharpness = +0.1f; break; // ]
    }
}

static void ToggleBar(AppModel model)
{
    model.ShowBar = !model.ShowBar;
    model.PanelActivity();
}

/// <summary>Whether the pointer is over this window's client area right now.</summary>
bool PointerOverClient()
{
    if (!Win32.GetCursorPos(out var pt) || Win32.WindowFromPoint(pt) != hwnd) return false;
    var origin = new Win32.POINT();
    Win32.ClientToScreen(hwnd, ref origin);
    Win32.GetClientRect(hwnd, out var client);
    return pt.X >= origin.X && pt.Y >= origin.Y
        && pt.X < origin.X + client.Right && pt.Y < origin.Y + client.Bottom;
}

/// <summary>Applies a radio choice through the toggles, which enforce the mutual exclusion.</summary>
void SelectScaler(int want)
{
    var p = renderer!.Processor;
    switch (want)
    {
        case 0:
            if (renderer.NisEnabled) ToggleNis();
            if (p?.SuperResolutionEnabled == true) ToggleVsr();
            break;
        case 1:
            if (!renderer.NisEnabled) ToggleNis();
            break;
        default:
            if (p?.SuperResolutionEnabled != true) ToggleVsr();
            break;
    }
}

void ToggleVsr()
{
    var p = renderer?.Processor;
    if (p is null || !p.Available) { Log.Warn("VSR unavailable: no D3D11 video device"); return; }
    // Only one upscaler owns the frame; enabling the driver one stands NIS down.
    if (!p.SuperResolutionEnabled && renderer!.NisEnabled)
    {
        renderer.NisEnabled = false;
        Log.Info("NIS off: RTX VSR takes the scaling path");
    }
    p.ToggleSuperResolution();
    Log.Info($"VSR now {(p.SuperResolutionEnabled ? "ON" : "OFF")}" +
             (p.LastError is null ? "" : $" ({p.LastError})"));
}

/// <summary>
/// NIS and the driver filters both want to own the scale, and only one path can run.
/// Turning NIS on therefore switches the driver path off rather than leaving a toggle
/// that silently does nothing - the previous behaviour, which was simply a bug.
/// </summary>
void ToggleNis()
{
    if (renderer is null) return;
    bool want = !renderer.NisEnabled;

    if (want && renderer.UseProcessor)
    {
        var p = renderer.Processor!;
        if (p.SuperResolutionEnabled) { p.ToggleSuperResolution(); Log.Info("RTX VSR off: NIS takes the scaling path"); }
        if (p.HdrEnabled) { renderer.SetHdrOutput(false); p.ToggleHdr(); Log.Info("RTX Video HDR off: it requires the driver path"); }
    }

    renderer.NisEnabled = want;
    Log.Info($"NIS now {(want ? "ON" : "OFF")}");
}

void ToggleHdr()
{
    var p = renderer?.Processor;
    if (p is null || !p.Available) { Log.Warn("RTX HDR unavailable: no D3D11 video device"); return; }
    if (!renderer!.DisplaySupportsHdr)
    {
        Log.Warn("RTX HDR unavailable: Windows has this display in SDR mode. " +
                 "Turn HDR on in Windows display settings and restart.");
        return;
    }

    bool want = !p.HdrEnabled;
    // The swapchain colour space and the driver filter have to move together: PQ output
    // with the filter off looks washed out, the filter on in sRGB output does nothing.
    if (!renderer.SetHdrOutput(want)) return;
    p.ToggleHdr();
    if (p.HdrEnabled != want) renderer.SetHdrOutput(p.HdrEnabled);

    Log.Info($"RTX Video HDR now {(p.HdrEnabled ? "ON" : "OFF")}" +
             (p.LastError is null ? "" : $" ({p.LastError})"));
}

void OpenMenuAt(IntPtr h, int clientX, int clientY)
{
    var pt = new Win32.POINT { X = clientX, Y = clientY };
    Win32.ClientToScreen(h, ref pt);
    ShowModeMenu(pt.X, pt.Y);
}

/// <summary>One selectable entry: a media type plus the rate to pin it to.</summary>
internal sealed record MenuEntry(CaptureMode Mode, double Fps);
