using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Rayxel.Link.Sender;

namespace Rayxel.Link.Control;

/// <summary>
/// The sender's control socket: where its user interfaces - the Avalonia window, the Decky
/// plugin - ask for its state and switch it. A Unix domain socket on Linux and on Windows
/// alike (Windows has had them since 10 1803), in a place only the user can reach, so whoever
/// can connect is already the user. One JSON object per line each way; see
/// <see cref="ControlRequest"/> for the commands.
/// </summary>
public sealed class ControlServer : IDisposable
{
    private readonly SenderService _sender;
    private readonly Action<bool> _quit;
    private readonly string _path;
    private Socket? _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Subscriber> _subscribers = [];
    private readonly object _gate = new();
    private readonly AutoResetEvent _changed = new(false);

    private sealed class Subscriber(StreamWriter writer)
    {
        public readonly StreamWriter Writer = writer;
        public readonly SemaphoreSlim Gate = new(1, 1);
    }

    /// <param name="quit">Asked to end the program; the argument says whether a held capture was accepted as a risk.</param>
    public ControlServer(SenderService sender, Action<bool> quit, string? path = null)
    {
        _sender = sender;
        _quit = quit;
        _path = path ?? ControlPaths.SocketPath();
        // Only noted here: the sender raises this under its own lock, and the pushing is done
        // by a thread of this server's, a little later and at most a few times a second.
        _sender.StatusChanged += () => _changed.Set();
    }

    public bool Start()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // A socket file left behind by a sender that did not end cleanly blocks the bind.
            if (File.Exists(_path)) File.Delete(_path);
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(_path));
            listener.Listen(8);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            _listener = listener;
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
        {
            LinkLog.Error($"control: cannot listen on {_path}: {ex.Message}");
            return false;
        }

        LinkLog.Info($"control: {_path}");
        _ = Task.Run(AcceptLoop);
        _ = Task.Run(PushLoop);
        _ = Task.Run(WatchLoop);
        return true;
    }

    /// <summary>
    /// Puts the socket back if its file goes: a clean-up of a temporary folder takes files
    /// it does not know are in use, and with the file gone nothing can connect any more.
    /// </summary>
    private async Task WatchLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token); }
            catch (OperationCanceledException) { return; }
            if (File.Exists(_path)) continue;
            LinkLog.Warn($"control: {_path} has gone; listening on it afresh");
            try
            {
                var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                listener.Bind(new UnixDomainSocketEndPoint(_path));
                listener.Listen(8);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var old = _listener;
                _listener = listener;
                old?.Dispose();
                _ = Task.Run(AcceptLoop);
            }
            catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
            {
                LinkLog.Warn($"control: cannot listen on {_path} again: {ex.Message}");
            }
        }
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try { client = await _listener!.AcceptAsync(_stop.Token); }
            catch (Exception) when (_stop.IsCancellationRequested) { return; }
            catch (ObjectDisposedException) { return; } // replaced by WatchLoop, which started another
            catch (SocketException) { continue; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(Socket client)
    {
        using var stream = new NetworkStream(client, ownsSocket: true);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        Subscriber? subscriber = null;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(_stop.Token);
                if (line is null) break;
                if (line.Length == 0) continue;

                ControlRequest? request;
                try { request = JsonSerializer.Deserialize(line, ControlJson.Default.ControlRequest); }
                catch (JsonException ex)
                {
                    await Write(writer, null, new ControlResponse { Ok = false, Error = $"not a request: {ex.Message}" });
                    continue;
                }
                if (request is null) continue;

                if (request.Cmd == "subscribe")
                {
                    subscriber ??= new Subscriber(writer);
                    lock (_gate) _subscribers.Add(subscriber);
                    await Write(writer, subscriber, new ControlResponse { Event = "status", Status = _sender.Status() });
                    continue;
                }
                await Write(writer, subscriber, Handle(request));
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            if (subscriber is not null) lock (_gate) _subscribers.Remove(subscriber);
        }
    }

    private ControlResponse Handle(ControlRequest request)
    {
        switch (request.Cmd)
        {
            case "status":
                return Ok();
            case "set_enabled" when request.Value is { } enabled:
                _sender.SetEnabled(enabled);
                return Ok();
            case "set_discoverable" when request.Value is { } discoverable:
                _sender.SetDiscoverable(discoverable);
                return Ok();
            case "set_allow_input" when request.Value is { } allow:
                _sender.SetAllowInput(allow);
                return Ok();
            case "cancel_pairing":
                _sender.CancelPairing();
                return Ok();
            case "forget" when !string.IsNullOrEmpty(request.Id):
                return _sender.Forget(request.Id) ? Ok() : new ControlResponse { Ok = false, Error = "no such viewer" };
            case "get_settings":
                return new ControlResponse { Ok = true, Settings = _sender.Settings };
            case "set_settings" when request.Settings is { } settings:
                _sender.UpdateSettings(settings);
                return new ControlResponse { Ok = true, Settings = _sender.Settings, Status = _sender.Status() };
            case "stop_stream":
                _sender.StopStreamNow();
                return Ok();
            case "quit":
                if (_sender.CaptureHeld && request.Force != true)
                    return new ControlResponse
                    {
                        Ok = false,
                        Error = "a capture is held; ending now can take gamescope, and the game, down with it - send force to end anyway",
                        Status = _sender.Status(),
                    };
                // Answered first: ending the program closes this connection, and the asker
                // should hear yes rather than have the line go dead.
                bool force = request.Force == true;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(200);
                    _quit(force);
                });
                return new ControlResponse { Ok = true };
            default:
                return new ControlResponse { Ok = false, Error = $"unknown or incomplete command '{request.Cmd}'" };
        }

        ControlResponse Ok() => new() { Ok = true, Status = _sender.Status() };
    }

    private static async Task Write(StreamWriter writer, Subscriber? subscriber, ControlResponse response)
    {
        string line = JsonSerializer.Serialize(response, ControlJson.Default.ControlResponse);
        if (subscriber is null)
        {
            await writer.WriteLineAsync(line);
            return;
        }
        await subscriber.Gate.WaitAsync();
        try { await writer.WriteLineAsync(line); }
        finally { subscriber.Gate.Release(); }
    }

    /// <summary>Sends the whole state to every subscriber after a change, at most four times a second.</summary>
    private async Task PushLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            _changed.WaitOne(1000);
            if (_stop.IsCancellationRequested) return;
            List<Subscriber> subscribers;
            lock (_gate) subscribers = _subscribers.ToList();
            if (subscribers.Count > 0)
            {
                var status = new ControlResponse { Event = "status", Status = _sender.Status() };
                foreach (var subscriber in subscribers)
                {
                    try { await Write(subscriber.Writer, subscriber, status); }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                    {
                        lock (_gate) _subscribers.Remove(subscriber);
                    }
                }
            }
            await Task.Delay(250);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _changed.Set();
        _listener?.Dispose();
        try { if (File.Exists(_path)) File.Delete(_path); }
        catch (IOException) { }
    }
}
