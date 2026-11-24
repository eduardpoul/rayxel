using System.Collections.ObjectModel;
using System.Net.Sockets;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rayxel.Link.Control;

namespace Rayxel.Send.App;

/// <summary>
/// Everything the window shows, kept from the sender's own state. The sender pushes its whole
/// state on every change over a subscription; this only ever draws the latest one, and sends
/// requests over a second connection. While nothing answers on the socket it keeps looking,
/// every couple of seconds, so a sender started by other means is picked up on its own.
/// <para>
/// Everything here runs on the UI thread. The subscription calls back on a pool thread and
/// is posted across.
/// </para>
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryWhileStarting = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan StartGrace = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan NoticeFor = TimeSpan.FromSeconds(8);

    private static readonly IBrush OffBrush = new ImmutableSolidColorBrush(Color.Parse("#9A9A9A"));
    private static readonly IBrush ReadyBrush = new ImmutableSolidColorBrush(Color.Parse("#F2F2F2"));
    private static readonly IBrush DiscoverableBrush = new ImmutableSolidColorBrush(Color.Parse("#60CDFF"));
    private static readonly IBrush PairingBrush = new ImmutableSolidColorBrush(Color.Parse("#FFC857"));
    private static readonly IBrush StreamingBrush = new ImmutableSolidColorBrush(Color.Parse("#6CCB5F"));

    private readonly ControlClient _client = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly DispatcherTimer _clock;
    private IDisposable? _subscription;
    private int _generation;
    private SenderStatus? _status;
    private bool _applying;
    private bool _settingsAsked;
    private DateTime _startingUntil;
    private DateTime _noticeUntil;
    private string _viewersKey = "";
    private string _codecsKey = "";
    private Func<Task>? _confirmed;

    public MainViewModel()
    {
        // Once a second: the discoverable and pairing countdowns, and "last seen" labels.
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => Tick();
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(SettingsForm.IsDirty) or nameof(SettingsForm.IsLoaded))) SaveNote = null;
        };
    }

    // The connection.
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isStarting;
    [ObservableProperty] private string? _startNote;

    // The header.
    [ObservableProperty] private string _senderName = Strings.T("title");
    [ObservableProperty] private string _senderAbout = "";

    // The status card.
    [ObservableProperty] private string _stateWord = "";
    [ObservableProperty] private string _stateDetail = "";
    [ObservableProperty] private IBrush _stateBrush = OffBrush;
    [ObservableProperty] private bool _switchedOn;
    [ObservableProperty] private string? _problem;
    [ObservableProperty] private bool _hasProblem;
    [ObservableProperty] private bool _canDiscover;
    [ObservableProperty] private bool _isDiscoverable;
    [ObservableProperty] private string _discoverText = Strings.T("makeDiscoverable");
    [ObservableProperty] private string? _discoverTip;

    // The pairing card.
    [ObservableProperty] private bool _isPairing;
    [ObservableProperty] private string _pairingTitle = "";
    [ObservableProperty] private bool _hasPin;
    [ObservableProperty] private string _pinText = "";
    [ObservableProperty] private string _pairingLeft = "";

    // The lists.
    public ObservableCollection<PairedViewerItem> Viewers { get; } = [];
    public ObservableCollection<CodecItem> Codecs { get; } = [];
    [ObservableProperty] private bool _hasViewers;
    [ObservableProperty] private bool _hasCodecs;

    // The settings.
    public SettingsForm Settings { get; } = new();
    [ObservableProperty] private string? _saveNote;

    // A line about what just went wrong, for a few seconds.
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private bool _hasNotice;

    // The question asked before forgetting a computer or quitting the sender.
    [ObservableProperty] private bool _confirmVisible;
    [ObservableProperty] private string _confirmTitle = "";
    [ObservableProperty] private string _confirmBody = "";
    [ObservableProperty] private string _confirmOk = "";
    [ObservableProperty] private bool _confirmIsDanger;

    public void Start()
    {
        _clock.Start();
        _ = FollowAsync(_stop.Token);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _clock.Stop();
        _subscription?.Dispose();
        _subscription = null;
        _client.Dispose();
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Keeps a subscription open. Each attempt gets a generation number; anything still
    /// arriving from an older connection after it was lost is ignored, so a late status from
    /// a sender that has since quit cannot bring the window back to life.
    /// </summary>
    private async Task FollowAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            if (_subscription is null)
            {
                int generation = ++_generation;
                IDisposable? subscription = null;
                try
                {
                    subscription = await ControlClient.Subscribe(
                        status => Dispatcher.UIThread.Post(() =>
                        {
                            if (generation == _generation) Apply(status);
                        }),
                        () => Dispatcher.UIThread.Post(() => Lost(generation)),
                        cancel: stop);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException
                                               or UnauthorizedAccessException or OperationCanceledException)
                {
                }

                if (subscription is not null && generation == _generation) _subscription = subscription;
                else subscription?.Dispose();
                if (_subscription is null) ShowNotRunning();
            }

            try
            {
                await Task.Delay(IsStarting ? RetryWhileStarting : RetryEvery, stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Lost(int generation)
    {
        if (generation != _generation) return;
        _generation++;
        _subscription?.Dispose();
        _subscription = null;
        ShowNotRunning();
    }

    private void ShowNotRunning()
    {
        _status = null;
        _settingsAsked = false;
        IsConnected = false;
        ConfirmVisible = false;
        _confirmed = null;
        if (IsStarting && DateTime.UtcNow > _startingUntil)
        {
            IsStarting = false;
            StartNote = Strings.T("startNoAnswer");
        }
    }

    private void Apply(SenderStatus status)
    {
        _status = status;
        if (!IsConnected)
        {
            IsConnected = true;
            IsStarting = false;
            StartNote = null;
        }
        if (!_settingsAsked)
        {
            _settingsAsked = true;
            _ = LoadSettingsAsync();
        }

        _applying = true;
        try
        {
            SwitchedOn = status.Enabled;
        }
        finally
        {
            _applying = false;
        }

        SenderName = string.IsNullOrEmpty(status.Name) ? Strings.T("title") : status.Name;
        SenderAbout = string.Join(" · ", new[]
        {
            status.Model,
            status.Platform,
            string.IsNullOrEmpty(status.Version) ? "" : "v" + status.Version,
        }.Where(part => !string.IsNullOrEmpty(part)));
        Problem = status.Problem;
        HasProblem = !string.IsNullOrEmpty(status.Problem);

        UpdateViewers(status);
        UpdateCodecs(status);
        Refresh();
    }

    private void Tick()
    {
        if (HasNotice && DateTime.UtcNow > _noticeUntil)
        {
            HasNotice = false;
            Notice = null;
        }
        Refresh();
    }

    /// <summary>Everything that depends on the clock as well as on the state.</summary>
    private void Refresh()
    {
        if (_status is not { } s) return;
        long now = NowMs();

        StateWord = Strings.State(s.State);
        StateBrush = s.State switch
        {
            SenderStates.Off => OffBrush,
            SenderStates.Discoverable => DiscoverableBrush,
            SenderStates.Pairing => PairingBrush,
            SenderStates.Streaming => StreamingBrush,
            _ => ReadyBrush,
        };
        StateDetail = Detail(s);

        long left = s.DiscoverableUntilMs is { } until ? until - now : 0;
        IsDiscoverable = left > 0;
        DiscoverText = left > 0 ? Strings.F("discoverableFor", Strings.Countdown(left)) : Strings.T("makeDiscoverable");
        CanDiscover = s.Enabled;
        DiscoverTip = s.Enabled ? null : Strings.T("switchOnFirst");

        if (s.Pairing is { } pairing)
        {
            IsPairing = true;
            PairingTitle = Strings.F("pairWants", pairing.ViewerName);
            HasPin = !string.IsNullOrEmpty(pairing.Pin);
            PinText = HasPin ? Strings.Pin(pairing.Pin!) : "";
            PairingLeft = Strings.F("pairExpires", Strings.Countdown(pairing.ExpiresMs - now));
        }
        else
        {
            IsPairing = false;
            HasPin = false;
            PinText = "";
        }

        string? watching = s.State == SenderStates.Streaming ? s.Viewer?.Id : null;
        foreach (var item in Viewers) item.Update(now, item.Id == watching);
    }

    private string Detail(SenderStatus s)
    {
        switch (s.State)
        {
            case SenderStates.Off:
                return Strings.T("dOff");
            case SenderStates.Discoverable:
                return Strings.T("dDiscoverable");
            case SenderStates.Pairing:
                return Strings.F("dPairing", s.Pairing?.ViewerName ?? "");
            case SenderStates.Streaming:
                break;
            default:
                return s.Viewers.Count == 0 ? Strings.T("dWaitingNone") : Strings.T("dWaiting");
        }

        var parts = new List<string>();
        if (s.Viewer is { } viewer) parts.Add(Strings.F("watching", viewer.Name));
        if (s.Stream is { } stream)
        {
            string codec = Strings.Codec(stream.Codec);
            if (!string.IsNullOrEmpty(stream.AskedCodec) &&
                !string.Equals(stream.AskedCodec, stream.Codec, StringComparison.OrdinalIgnoreCase))
                codec = Strings.F("instead", codec, Strings.Codec(stream.AskedCodec));
            parts.Add(stream.Width > 0 ? $"{codec} {stream.Width}×{stream.Height}" : codec);
            if (stream.Fps > 0) parts.Add($"{Strings.Num(stream.Fps, "0")} {Strings.T("fps")}");
            parts.Add($"{Strings.Num(stream.Mbps, "0.0")} {Strings.T("mbit")}");
            if (stream.EncodeMs > 0) parts.Add(Strings.F("encodeMs", Strings.Num(stream.EncodeMs, "0.0")));
            if (stream.InputActive) parts.Add(Strings.T("inputOn"));
        }
        return string.Join(" · ", parts);
    }

    // A status arrives with every change, a stream's figures included; the lists are only
    // rebuilt when what they show has changed, so they do not flicker under the pointer.
    private void UpdateViewers(SenderStatus s)
    {
        string key = string.Join("\n", s.Viewers.Select(v => $"{v.Id}\t{v.Name}\t{v.LastSeenMs}"));
        if (key == _viewersKey) return;
        _viewersKey = key;
        Viewers.Clear();
        foreach (var viewer in s.Viewers.OrderByDescending(v => v.LastSeenMs))
            Viewers.Add(new PairedViewerItem(viewer, ForgetAsync));
        HasViewers = Viewers.Count > 0;
    }

    private void UpdateCodecs(SenderStatus s)
    {
        string key = string.Join("\n", s.Codecs.Select(c => $"{c.Name}\t{c.Available}\t{c.Why}"));
        if (key == _codecsKey) return;
        _codecsKey = key;
        Codecs.Clear();
        foreach (var codec in s.Codecs) Codecs.Add(CodecItem.From(codec));
        HasCodecs = Codecs.Count > 0;
    }

    private async Task LoadSettingsAsync()
    {
        var response = await Request(client => client.SendAsync(new ControlRequest { Cmd = "get_settings" }));
        if (response?.Settings is { } settings && !Settings.IsDirty) Settings.Load(settings);
    }

    /// <summary>
    /// Sends one request. A connection the sender has since closed answers null once before
    /// the client notices and reconnects, so a null is tried a second time.
    /// </summary>
    private async Task<ControlResponse?> Request(Func<ControlClient, Task<ControlResponse?>> send)
    {
        try
        {
            return await send(_client) ?? await send(_client);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>A request whose failure the user should hear about. A good answer's status is drawn at once.</summary>
    private async Task<ControlResponse?> Ask(Func<ControlClient, Task<ControlResponse?>> send)
    {
        var response = await Request(send);
        if (response is null) ShowNotice(Strings.T("noAnswer"));
        else if (response.Ok != true) ShowNotice(Strings.F("refused", response.Error ?? "?"));
        else if (response.Status is { } status && IsConnected) Apply(status);
        return response;
    }

    private void ShowNotice(string text)
    {
        Notice = text;
        HasNotice = true;
        _noticeUntil = DateTime.UtcNow + NoticeFor;
    }

    partial void OnSwitchedOnChanged(bool value)
    {
        if (_applying || !IsConnected) return;
        _ = SwitchAsync(value);
    }

    // "Off" is set_enabled false, never ending the program: on a Deck the program holds
    // gamescope's capture, and ending it can take the game down with it.
    private async Task SwitchAsync(bool on)
    {
        var response = await Ask(client => client.SetEnabledAsync(on));
        if (response is not { Ok: true } && _status is { } s)
        {
            _applying = true;
            try
            {
                SwitchedOn = s.Enabled;
            }
            finally
            {
                _applying = false;
            }
        }
    }

    [RelayCommand]
    private async Task ToggleDiscoverable()
    {
        bool discoverable = _status?.DiscoverableUntilMs is { } until && until > NowMs();
        await Ask(client => client.SetDiscoverableAsync(!discoverable));
    }

    [RelayCommand]
    private async Task CancelPairing() => await Ask(client => client.CancelPairingAsync());

    private Task ForgetAsync(PairedViewerItem item)
    {
        AskFirst(Strings.F("forgetTitle", item.Name), Strings.T("forgetBody"), Strings.T("forget"), danger: true,
                 async () => await Ask(client => client.ForgetAsync(item.Id)));
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task Save()
    {
        var settings = Settings.Build();
        var response = await Ask(client => client.SetSettingsAsync(settings));
        if (response is not { Ok: true }) return;
        Settings.Load(response.Settings ?? settings);
        SaveNote = Strings.T("saved");
    }

    [RelayCommand]
    private void StartSender()
    {
        string? error = SenderLauncher.Start();
        if (error is not null)
        {
            StartNote = error;
            return;
        }
        IsStarting = true;
        StartNote = Strings.T("starting");
        _startingUntil = DateTime.UtcNow + StartGrace;
    }

    /// <summary>
    /// Ends the sender itself, which "off" does not. While it holds a capture this can take
    /// the compositor and the game down, so then it is said plainly and asked first; a stream
    /// in progress is asked about too. Otherwise it simply goes: Start brings it back.
    /// </summary>
    [RelayCommand]
    private async Task QuitSender()
    {
        if (_status is not { } s) return;
        if (s.CaptureHeld)
            AskQuitDespiteCapture();
        else if (s.State == SenderStates.Streaming)
            AskFirst(Strings.T("quitTitle"), Strings.F("quitStreamingBody", s.Viewer?.Name ?? ""),
                     Strings.T("quitSender"), danger: true, () => QuitAsync(force: false));
        else
            await QuitAsync(force: false);
    }

    private void AskQuitDespiteCapture() =>
        AskFirst(Strings.T("quitCaptureTitle"), Strings.T("quitCaptureBody"), Strings.T("quitAnyway"), danger: true,
                 () => QuitAsync(force: true));

    private async Task QuitAsync(bool force)
    {
        // No answer is the usual outcome: the sender closes the connection as it goes, and
        // the lost subscription then shows the "not running" page by itself.
        var response = await Request(client => client.QuitAsync(force));
        if (response is not { Ok: false }) return;
        if (!force && (response.Status ?? _status) is { CaptureHeld: true })
            AskQuitDespiteCapture(); // a capture was opened between the click and the request
        else
            ShowNotice(Strings.F("refused", response.Error ?? "?"));
    }

    private void AskFirst(string title, string body, string ok, bool danger, Func<Task> action)
    {
        ConfirmTitle = title;
        ConfirmBody = body;
        ConfirmOk = ok;
        ConfirmIsDanger = danger;
        _confirmed = action;
        ConfirmVisible = true;
    }

    [RelayCommand]
    private async Task ConfirmYes()
    {
        var action = _confirmed;
        _confirmed = null;
        ConfirmVisible = false;
        if (action is not null) await action();
    }

    [RelayCommand]
    private void ConfirmNo()
    {
        _confirmed = null;
        ConfirmVisible = false;
    }
}
