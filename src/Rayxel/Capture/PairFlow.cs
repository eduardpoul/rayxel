using Rayxel.Diagnostics;
using Rayxel.Link.Discovery;
using Rayxel.Link.Security;

namespace Rayxel.Capture;

/// <summary>
/// Pairing with a sender, as the dialog walks through it: look for senders open to pairing,
/// the user picks one, the keys are exchanged and the sender shows a PIN, the user types it
/// here, and the sender is kept in <see cref="NetLinks.Store"/>. The network work happens on
/// the pool; the dialog reads the state each frame and calls the methods from the UI thread.
/// </summary>
internal sealed class PairFlow : IDisposable
{
    public enum Stages { Scanning, Connecting, EnterPin, Confirming, Done, Failed }

    private readonly CancellationTokenSource _cancel = new();
    private readonly object _gate = new();
    private List<FoundSender> _found = [];
    private PairingClient? _client;
    private volatile Stages _stage = Stages.Scanning;

    public PairFlow() => Scan();

    /// <summary>A flow standing at the PIN, with nothing on the network behind it: for --screen=pair-pin, to look at.</summary>
    public static PairFlow AtPin(string senderName, string typed)
    {
        var flow = new PairFlow(scan: false) { SenderName = senderName, Pin = typed };
        flow._stage = Stages.EnterPin;
        return flow;
    }

    private PairFlow(bool scan)
    {
        if (scan) Scan();
    }

    public Stages Stage => _stage;

    public IReadOnlyList<FoundSender> Found
    {
        get { lock (_gate) return _found.ToList(); }
    }

    /// <summary>The sender being paired with, once one is picked.</summary>
    public string SenderName { get; private set; } = "";

    public string? Error { get; private set; }

    /// <summary>The last PIN typed did not match; the user may try again.</summary>
    public bool WrongPin { get; private set; }

    /// <summary>
    /// Counts the attempts at the PIN. The dialog gives its input a new identity with each,
    /// which is how ImGui is made to forget the digits of the one before.
    /// </summary>
    public int Attempt { get; private set; }

    /// <summary>What the user has typed so far. Only the UI thread touches it.</summary>
    public string Pin = "";

    /// <summary>The sender as kept, once paired.</summary>
    public Peer? Result { get; private set; }

    /// <summary>Set by the dialog once it has acted on <see cref="Result"/>, so it does so once.</summary>
    public bool Delivered;

    /// <summary>Esc was pressed: the dialog closes on its next frame, as if Cancel had been.</summary>
    public volatile bool CloseRequested;

    private void Scan()
    {
        _stage = Stages.Scanning;
        var cancel = _cancel.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                using var discovery = new DiscoveryClient(NetLinks.Identity.Id);
                while (!cancel.IsCancellationRequested && _stage == Stages.Scanning)
                {
                    var found = await discovery.ScanAsync(TimeSpan.FromMilliseconds(1200), cancel: cancel);
                    lock (_gate) _found = found.ToList();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Warn($"pairing: looking for senders failed: {ex.Message}");
                Fail(ex.Message);
            }
        }, cancel);
    }

    /// <summary>Starts pairing with a sender from the list.</summary>
    public void Choose(FoundSender sender)
    {
        SenderName = sender.Name;
        _stage = Stages.Connecting;
        var cancel = _cancel.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                _client?.Dispose();
                _client = new PairingClient(NetLinks.Identity, NetLinks.ViewerName, sender.EndPoint);
                var offer = await _client.StartAsync(cancel);
                SenderName = offer.SenderName;
                Pin = "";
                WrongPin = false;
                _stage = Stages.EnterPin;
                Log.Info($"pairing: {offer.SenderName} ({offer.Model}) is showing its PIN");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is PairingException or System.Net.Sockets.SocketException)
            {
                Fail(ex.Message);
            }
        }, cancel);
    }

    /// <summary>Checks the PIN typed against the exchange, and if it matches, finishes the pairing.</summary>
    public void Submit()
    {
        if (_client is not { } client || _stage != Stages.EnterPin) return;
        string typed = Pin;
        _stage = Stages.Confirming;
        var cancel = _cancel.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                if (!await client.ConfirmAsync(typed, cancel))
                {
                    WrongPin = true;
                    Attempt++;
                    Pin = "";
                    _stage = Stages.EnterPin;
                    return;
                }
                Result = client.Result;
                NetLinks.Store.Put(Result!);
                Log.Info($"pairing: paired with {Result!.Name} ({Result.Id})");
                _stage = Stages.Done;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is PairingException or System.Net.Sockets.SocketException)
            {
                Fail(ex.Message);
            }
        }, cancel);
    }

    /// <summary>Back to the list after a failure.</summary>
    public void Retry()
    {
        _client?.Cancel();
        _client?.Dispose();
        _client = null;
        Error = null;
        Scan();
    }

    private void Fail(string message)
    {
        Error = message;
        _stage = Stages.Failed;
        Log.Warn($"pairing: {message}");
    }

    /// <summary>Stops wherever it is; a sender showing a PIN is told to stop showing it.</summary>
    public void Dispose()
    {
        if (_stage is Stages.Connecting or Stages.EnterPin or Stages.Confirming) _client?.Cancel();
        _cancel.Cancel();
        _client?.Dispose();
        _cancel.Dispose();
    }
}
