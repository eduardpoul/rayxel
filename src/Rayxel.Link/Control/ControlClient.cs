using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Rayxel.Link.Control;

/// <summary>
/// The other end of the sender's control socket, for a user interface: ask for the state,
/// switch things, and follow every change. One JSON object per line each way - the Decky
/// plugin's Python backend speaks the same thing without this class.
/// <para>
/// Requests go one at a time over one connection. <see cref="Subscribe"/> opens a second,
/// over which the sender pushes the whole state whenever it changes, until it is disposed.
/// </para>
/// </summary>
public sealed class ControlClient : IDisposable
{
    private readonly string _path;
    private Socket? _socket;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ControlClient(string? socketPath = null) => _path = socketPath ?? ControlPaths.SocketPath();

    /// <summary>Whether a sender is listening on the socket now.</summary>
    public bool Connected => _socket?.Connected == true;

    private async Task<bool> EnsureConnectedAsync(CancellationToken cancel)
    {
        // A connection the sender has closed still says Connected until it is used; readable
        // with nothing to read is how a closed one shows.
        if (_socket is { Connected: true } socketNow)
        {
            bool closed;
            try { closed = socketNow.Poll(0, SelectMode.SelectRead) && socketNow.Available == 0; }
            catch (SocketException) { closed = true; }
            if (!closed) return true;
        }
        Close();
        if (!File.Exists(_path)) return false;
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_path), cancel);
        }
        catch (SocketException)
        {
            socket.Dispose();
            return false;
        }
        var stream = new NetworkStream(socket, ownsSocket: false);
        _socket = socket;
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        return true;
    }

    /// <summary>Sends one request and waits for its answer. Null when no sender is listening.</summary>
    public async Task<ControlResponse?> SendAsync(ControlRequest request, CancellationToken cancel = default)
    {
        await _gate.WaitAsync(cancel);
        try
        {
            if (!await EnsureConnectedAsync(cancel)) return null;
            try
            {
                await _writer!.WriteLineAsync(JsonSerializer.Serialize(request, ControlJson.Default.ControlRequest).AsMemory(), cancel);
                while (true)
                {
                    string? line = await _reader!.ReadLineAsync(cancel);
                    if (line is null)
                    {
                        Close();
                        return null;
                    }
                    var response = JsonSerializer.Deserialize(line, ControlJson.Default.ControlResponse);
                    if (response is { Event: null }) return response;
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or JsonException)
            {
                Close();
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SenderStatus?> GetStatusAsync(CancellationToken cancel = default) =>
        (await SendAsync(new ControlRequest { Cmd = "status" }, cancel))?.Status;

    public async Task<SenderSettings?> GetSettingsAsync(CancellationToken cancel = default) =>
        (await SendAsync(new ControlRequest { Cmd = "get_settings" }, cancel))?.Settings;

    public Task<ControlResponse?> SetSettingsAsync(SenderSettings settings, CancellationToken cancel = default) =>
        SendAsync(new ControlRequest { Cmd = "set_settings", Settings = settings }, cancel);

    public Task<ControlResponse?> SetEnabledAsync(bool enabled, CancellationToken cancel = default) =>
        SendAsync(new ControlRequest { Cmd = "set_enabled", Value = enabled }, cancel);

    public Task<ControlResponse?> SetDiscoverableAsync(bool discoverable, CancellationToken cancel = default) =>
        SendAsync(new ControlRequest { Cmd = "set_discoverable", Value = discoverable }, cancel);

    public Task<ControlResponse?> SetAllowInputAsync(bool allow, CancellationToken cancel = default) =>
        SendAsync(new ControlRequest { Cmd = "set_allow_input", Value = allow }, cancel);

    public Task<ControlResponse?> CancelPairingAsync(CancellationToken cancel = default) =>
        SendAsync(new ControlRequest { Cmd = "cancel_pairing" }, cancel);

    public Task<ControlResponse?> ForgetAsync(string viewerId, CancellationToken cancel = default) =>
        SendAsync(new ControlRequest { Cmd = "forget", Id = viewerId }, cancel);

    public Task<ControlResponse?> StopStreamAsync(CancellationToken cancel = default) =>
        SendAsync(new ControlRequest { Cmd = "stop_stream" }, cancel);

    public Task<ControlResponse?> QuitAsync(bool force, CancellationToken cancel = default) =>
        SendAsync(new ControlRequest { Cmd = "quit", Force = force }, cancel);

    /// <summary>
    /// Follows the sender's state: <paramref name="onStatus"/> gets the whole state now and on
    /// every change, on a thread of the pool, until the returned object is disposed or the
    /// sender goes away (then <paramref name="onClosed"/>). Null when no sender is listening.
    /// </summary>
    public static async Task<IDisposable?> Subscribe(Action<SenderStatus> onStatus, Action? onClosed = null,
                                                     string? socketPath = null, CancellationToken cancel = default)
    {
        string path = socketPath ?? ControlPaths.SocketPath();
        if (!File.Exists(path)) return null;
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancel);
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }

        var stream = new NetworkStream(socket, ownsSocket: true);
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(new ControlRequest { Cmd = "subscribe" }, ControlJson.Default.ControlRequest));
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            // The sender went away between the connect and the first line.
            stream.Dispose();
            return null;
        }

        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        _ = Task.Run(async () =>
        {
            using var reader = new StreamReader(stream, Encoding.UTF8);
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    string? line = await reader.ReadLineAsync(stop.Token);
                    if (line is null) break;
                    var response = JsonSerializer.Deserialize(line, ControlJson.Default.ControlResponse);
                    if (response?.Status is { } status) onStatus(status);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or JsonException or OperationCanceledException) { }
            finally
            {
                stream.Dispose();
                if (!stop.IsCancellationRequested) onClosed?.Invoke();
            }
        }, CancellationToken.None);

        return new Subscription(stop);
    }

    private sealed class Subscription(CancellationTokenSource stop) : IDisposable
    {
        public void Dispose()
        {
            stop.Cancel();
            stop.Dispose();
        }
    }

    private void Close()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _socket?.Dispose();
        _reader = null;
        _writer = null;
        _socket = null;
    }

    public void Dispose()
    {
        Close();
        _gate.Dispose();
    }
}
