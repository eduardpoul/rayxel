using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rayxel.Link.Control;

namespace Rayxel.Send.App;

/// <summary>One computer in the paired list. "Last seen" moves with the clock, so it is a property of its own.</summary>
public sealed partial class PairedViewerItem : ObservableObject
{
    public PairedViewerItem(PairedViewer viewer, Func<PairedViewerItem, Task> forget)
    {
        Id = viewer.Id;
        Name = string.IsNullOrEmpty(viewer.Name) ? viewer.Id : viewer.Name;
        LastSeenMs = viewer.LastSeenMs;
        ForgetCommand = new AsyncRelayCommand(() => forget(this));
    }

    public string Id { get; }
    public string Name { get; }
    public long LastSeenMs { get; }
    public IAsyncRelayCommand ForgetCommand { get; }

    [ObservableProperty]
    private string _seen = "";

    public void Update(long nowMs, bool watching) =>
        Seen = watching ? Strings.T("watchingNow") : Strings.LastSeen(LastSeenMs, nowMs);
}

/// <summary>One codec and whether this machine can encode it, with the reason when it cannot.</summary>
public sealed record CodecItem(string Name, string Note, bool Available)
{
    public static CodecItem From(CodecInfo codec) => new(
        Strings.Codec(codec.Name),
        codec.Available ? Strings.T("codecReady") : string.IsNullOrEmpty(codec.Why) ? Strings.T("codecNot") : codec.Why!,
        codec.Available);
}

/// <summary>
/// The settings as the form edits them. Kept apart from what the sender sent, so a status
/// arriving while the user types does not overwrite what they typed, and so the fields the
/// form does not show go back to the sender untouched.
/// </summary>
public sealed partial class SettingsForm : ObservableObject
{
    private SenderSettings _loaded = new();
    private bool _loading;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _allowInput;
    [ObservableProperty] private decimal? _discoverableMinutes;
    [ObservableProperty] private bool _startEnabled;
    [ObservableProperty] private bool _allowUnpaired;
    [ObservableProperty] private string _source = "";
    [ObservableProperty] private decimal? _port;
    [ObservableProperty] private decimal? _bufferFrames;
    [ObservableProperty] private decimal? _maxFrame;
    [ObservableProperty] private decimal? _gop;

    /// <summary>Changed since it was last loaded or saved.</summary>
    [ObservableProperty] private bool _isDirty;

    /// <summary>The sender's settings have arrived at least once; until then there is nothing to edit.</summary>
    [ObservableProperty] private bool _isLoaded;

    public void Load(SenderSettings settings)
    {
        _loading = true;
        try
        {
            _loaded = settings.Clone();
            Name = settings.Name;
            AllowInput = settings.AllowInput;
            DiscoverableMinutes = settings.DiscoverableMinutes;
            StartEnabled = settings.StartEnabled;
            AllowUnpaired = settings.AllowUnpaired;
            Source = settings.Source;
            Port = settings.Port;
            BufferFrames = (decimal)settings.BufferFrames;
            MaxFrame = (decimal)settings.MaxFrame;
            Gop = settings.Gop;
            IsDirty = false;
            IsLoaded = true;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>What to send. A number field left empty keeps the value the sender had.</summary>
    public SenderSettings Build()
    {
        var settings = _loaded.Clone();
        settings.Name = Name.Trim();
        settings.AllowInput = AllowInput;
        settings.DiscoverableMinutes = DiscoverableMinutes is { } minutes ? (int)minutes : settings.DiscoverableMinutes;
        settings.StartEnabled = StartEnabled;
        settings.AllowUnpaired = AllowUnpaired;
        settings.Source = Source.Trim();
        settings.Port = Port is { } port ? (int)port : settings.Port;
        settings.BufferFrames = BufferFrames is { } buffer ? (double)buffer : settings.BufferFrames;
        settings.MaxFrame = MaxFrame is { } max ? (double)max : settings.MaxFrame;
        settings.Gop = Gop is { } gop ? (int)gop : settings.Gop;
        return settings;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is not (nameof(IsDirty) or nameof(IsLoaded)))
            IsDirty = true;
    }
}
