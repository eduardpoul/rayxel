using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rayxel.Link.Security;

/// <summary>One program this one is paired with: what was learnt about it when pairing, and since.</summary>
public sealed class Peer
{
    /// <summary>Its id in hex: the first 16 bytes of the SHA-256 of its public key.</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>"Steam Deck", "PC": what a sender said it is. Empty for a viewer.</summary>
    public string Model { get; set; } = "";

    /// <summary>Its long-term public key, uncompressed P-256, in base64.</summary>
    public string PublicKey { get; set; } = "";

    public long PairedMs { get; set; }
    public long LastSeenMs { get; set; }

    /// <summary>Where it was last heard from, "address:port"; what a viewer tries when finding it fails.</summary>
    public string LastAddress { get; set; } = "";

    [JsonIgnore]
    public byte[] PublicKeyBytes => Convert.FromBase64String(PublicKey);

    [JsonIgnore]
    public byte[] IdBytes => Identity.FromHex(Id) ?? [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(List<Peer>))]
internal sealed partial class PeerJson : JsonSerializerContext;

/// <summary>
/// The programs this one is paired with, kept in a JSON file: a viewer keeps its senders,
/// a sender its viewers. Thread-safe; saved after every change.
/// </summary>
public sealed class PeerStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private List<Peer> _peers = [];

    public PeerStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
                _peers = JsonSerializer.Deserialize(File.ReadAllText(path), PeerJson.Default.ListPeer) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            LinkLog.Warn($"pairings: {path} could not be read ({ex.Message}); starting with none");
            try { File.Move(path, path + ".unreadable", true); } catch (IOException) { }
        }
    }

    public event Action? Changed;

    public IReadOnlyList<Peer> All
    {
        get { lock (_gate) return _peers.ToList(); }
    }

    public Peer? Find(ReadOnlySpan<byte> id) => Find(Identity.Hex(id));

    public Peer? Find(string id)
    {
        lock (_gate) return _peers.FirstOrDefault(p => p.Id == id);
    }

    /// <summary>Adds a peer, or replaces the one with the same id - pairing again with the same program renews it.</summary>
    public void Put(Peer peer)
    {
        lock (_gate)
        {
            _peers.RemoveAll(p => p.Id == peer.Id);
            _peers.Add(peer);
            Save();
        }
        Changed?.Invoke();
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _peers.RemoveAll(p => p.Id == id) > 0;
            if (removed) Save();
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>Notes that a peer has been heard from, and where. Saved only now and then: it changes with every session.</summary>
    public void Seen(string id, string address)
    {
        lock (_gate)
        {
            var peer = _peers.FirstOrDefault(p => p.Id == id);
            if (peer is null) return;
            bool moved = peer.LastAddress != address;
            bool stale = Clock.UnixMs - peer.LastSeenMs > 60_000;
            peer.LastSeenMs = Clock.UnixMs;
            peer.LastAddress = address;
            if (moved || stale) Save();
        }
    }

    public void Rename(string id, string name)
    {
        lock (_gate)
        {
            var peer = _peers.FirstOrDefault(p => p.Id == id);
            if (peer is null || peer.Name == name) return;
            peer.Name = name;
            Save();
        }
        Changed?.Invoke();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            // Public keys only, but the list says who may connect: kept as private as the key.
            Identity.WritePrivate(_path, JsonSerializer.SerializeToUtf8Bytes(_peers, PeerJson.Default.ListPeer));
        }
        catch (IOException ex)
        {
            LinkLog.Warn($"pairings: {_path} could not be written: {ex.Message}");
        }
    }
}
