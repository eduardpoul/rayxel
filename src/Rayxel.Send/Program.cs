using System.Runtime.InteropServices;
using Rayxel.Link;
using Rayxel.Link.Control;
using Rayxel.Link.Sender;
using Rayxel.Send;

// rayxel-send: sends this machine's picture to a paired Rayxel and plays its controller,
// keyboard and mouse here. Runs until told to end - by its control socket, or by SIGTERM /
// Ctrl+C - and has no window: the Decky plugin and the Avalonia window are its faces.

string? configDirectory = null, framesFile = null, source = null;
int port = 0, framesWidth = 0, framesHeight = 0;
bool verbose = false, probe = false;

foreach (string arg in args)
{
    if (arg is "--version")
    {
        Console.WriteLine(SenderService.Version);
        return 0;
    }
    if (arg is "--help" or "-h")
    {
        Console.WriteLine("""
            rayxel-send - sends this machine's picture to a paired Rayxel

              rayxel-send [--port=N] [--source=NAME] [--config=DIR] [--verbose]
                          [--frames-file=FILE --frames-size=WxH]
              rayxel-send --probe [--config=DIR]

              --port=N          UDP port to listen on, for this run (default: the settings', 45800)
              --source=NAME     what to capture, for this run: a PipeWire node ("gamescope") on
                                Linux, a display's index on Windows
              --config=DIR      where the settings, the key and the paired viewers are kept
              --frames-file=F   send raw NV12 pictures from F in a loop instead of capturing,
                                BT.709 limited range; for tests that leave the screen alone
              --frames-size=WxH the size of each picture in --frames-file
              --verbose         log everything
              --probe           report what this machine offers - the encoders, the pointer,
                                input, the key and the paired viewers - and exit. Opens no
                                capture, so it is safe while a game runs

            Settings, pairing and switching on and off are done from the Rayxel sender
            window or the Decky plugin, which talk to this program on its control socket.
            """);
        return 0;
    }
    if (arg.StartsWith("--port=") && int.TryParse(arg[7..], out int p) && p is > 0 and < 65536) port = p;
    else if (arg.StartsWith("--config=")) configDirectory = arg[9..];
    else if (arg.StartsWith("--source=")) source = arg[9..];
    else if (arg.StartsWith("--frames-file=")) framesFile = arg[14..];
    else if (arg.StartsWith("--frames-size="))
    {
        string[] size = arg[14..].Split('x');
        if (size.Length == 2 && int.TryParse(size[0], out int w) && int.TryParse(size[1], out int h) && w > 0 && h > 0 &&
            w % 2 == 0 && h % 2 == 0)
            (framesWidth, framesHeight) = (w, h);
    }
    else if (arg is "--verbose") verbose = true;
    else if (arg is "--probe") probe = true;
    else
    {
        Console.Error.WriteLine($"unknown argument: {arg}; see --help");
        return 2;
    }
}
if (framesFile is not null && framesWidth == 0)
{
    Console.Error.WriteLine("--frames-file needs --frames-size=WxH (even width and height)");
    return 2;
}

// A plain timestamped line on standard output: systemd's journal and a terminal both take it.
// On Windows also to a file beside the settings, begun afresh each run: started by the
// sender's window it has no console, and its log would otherwise go nowhere. (On Linux the
// journal, or the file the window points its output at, has it.)
var started = Clock.NowNs;
StreamWriter? logFile = null;
if (OperatingSystem.IsWindows())
{
    try
    {
        string directory = configDirectory ?? ControlPaths.ConfigDirectory();
        Directory.CreateDirectory(directory);
        logFile = new StreamWriter(Path.Combine(directory, "rayxel-send.log"), append: false) { AutoFlush = true };
    }
    catch (IOException) { }
}
var logGate = new object();
LinkLog.Minimum = verbose ? LogLevel.Debug : LogLevel.Info;
LinkLog.Sink = (level, text) =>
{
    string tag = level switch { LogLevel.Debug => "dbg", LogLevel.Warn => "WRN", LogLevel.Error => "ERR", _ => "inf" };
    string line = $"[{(Clock.NowNs - started) / 1e9,9:0.000}] {tag} {text}";
    lock (logGate)
    {
        Console.Out.WriteLine(line);
        logFile?.WriteLine(line);
    }
};

#if WINDOWS_SENDER
Rayxel.Send.Windows.WindowsPlatform.ProtectKeysWithDpapi();
ISenderPlatform platform = new Rayxel.Send.Windows.WindowsPlatform();
#else
ISenderPlatform platform = new Rayxel.Send.Linux.LinuxPlatform();
#endif

if (probe) return await Probe(platform, configDirectory);

// One sender per user: a second would only fight the first for the port and the socket.
using (var asking = new ControlClient())
{
    if (await asking.GetStatusAsync() is { } running)
    {
        LinkLog.Info($"a sender is already running here ('{running.Name}', {running.State}); not starting another");
        return 0;
    }
}
if (framesFile is not null) platform = new FileSourcePlatform(platform, framesFile, framesWidth, framesHeight);

using var sender = new SenderService(platform, configDirectory);
sender.Override(settings =>
{
    if (port > 0) settings.Port = port;
    if (source is not null) settings.Source = source;
});

var quit = new ManualResetEventSlim(false);
using var control = new ControlServer(sender, force =>
{
    LinkLog.Info(force ? "asked to end, capture or no capture" : "asked to end");
    quit.Set();
});

if (!sender.Start()) return 1;
if (!control.Start()) LinkLog.Warn("control: no user interface can reach this sender; it runs on as it is");

// Ending is the one moment a held capture is let go, which gamescope may not survive; it is
// still what SIGTERM means, so systemd and Ctrl+C get what they ask for.
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    quit.Set();
};
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    quit.Set();
});

quit.Wait();
if (sender.CaptureHeld) LinkLog.Info("ending: letting the capture go");
return 0;

// What this machine offers, without opening a capture: a capture opened and closed while a
// game runs is the one thing here that can take the game down.
static async Task<int> Probe(ISenderPlatform platform, string? configDirectory)
{
    string directory = configDirectory ?? ControlPaths.ConfigDirectory();
    Console.WriteLine($"rayxel-send {SenderService.Version} probe");
    Console.WriteLine($"platform     : {platform.Name}, seen by viewers as '{platform.DefaultDeviceName}' ({platform.Model})");
    Console.WriteLine($"source       : '{platform.DefaultSource}' unless the settings name another");
    Console.WriteLine($"settings     : {directory}");
    Console.WriteLine($"control      : {ControlPaths.SocketPath()}");

    using (var client = new ControlClient())
        Console.WriteLine(await client.GetStatusAsync() is { } running
            ? $"running      : yes - '{running.Name}', {running.State}{(running.CaptureHeld ? ", holding a capture" : "")}"
            : "running      : no");

    string keyPath = Path.Combine(directory, "identity.key");
    if (File.Exists(keyPath))
    {
        using var identity = Rayxel.Link.Security.Identity.LoadOrCreate(keyPath);
        Console.WriteLine($"key          : {identity.IdText}");
    }
    else Console.WriteLine("key          : none yet - made when the sender first starts");
    var viewers = new Rayxel.Link.Security.PeerStore(Path.Combine(directory, "viewers.json")).All;
    Console.WriteLine($"paired with  : {(viewers.Count == 0 ? "nobody yet" : string.Join(", ", viewers.Select(v => v.Name)))}");

    for (int i = 0; i < Wire.CodecCount; i++)
    {
        var codec = (Wire.Codec)i;
        bool can = platform.CanEncode(codec, out string why);
        Console.WriteLine($"{Wire.CodecName(codec),-13}: {(can ? "can be sent" : "no, " + why)}");
    }

    using (var cursor = platform.CreateCursor())
        Console.WriteLine($"pointer      : {(cursor is null ? "not drawn here" : "drawn into the picture when one shows")}");

    // Whether input could be played here, without making the devices: on Linux that is
    // being allowed to write /dev/uinput.
    if (OperatingSystem.IsLinux())
    {
        bool uinput;
        try { using var device = File.OpenWrite("/dev/uinput"); uinput = true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { uinput = false; }
        Console.WriteLine($"input        : {(uinput ? "/dev/uinput can be written - gamepad, keyboard and mouse" : "/dev/uinput cannot be written by this user")}");
    }
    else Console.WriteLine("input        : keyboard and mouse through SendInput; no virtual gamepad (needs ViGEmBus)");
    return 0;
}
