using System.Security.Cryptography;
using Rayxel.Diagnostics;
using Rayxel.Link;
using Rayxel.Link.Security;

namespace Rayxel.Capture;

/// <summary>
/// This viewer's pairings: its own key, made once and kept under DPAPI so that only this
/// Windows account can read it, and the senders it is paired with, in links.json beside the
/// settings. The link library's log goes into the viewer's own.
/// </summary>
internal static class NetLinks
{
    private static readonly object Gate = new();
    private static Identity? _identity;
    private static PeerStore? _store;

    private static void Prepare()
    {
        KeyProtection.Protect = bytes => ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        KeyProtection.Unprotect = bytes => ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        LinkLog.Sink = (level, text) =>
        {
            switch (level)
            {
                case Rayxel.Link.LogLevel.Debug: Log.Debug($"link: {text}"); break;
                case Rayxel.Link.LogLevel.Warn: Log.Warn($"link: {text}"); break;
                case Rayxel.Link.LogLevel.Error: Log.Error($"link: {text}"); break;
                default: Log.Info($"link: {text}"); break;
            }
        };
    }

    public static Identity Identity
    {
        get
        {
            lock (Gate)
            {
                if (_identity is not null) return _identity;
                Prepare();
                _identity = Identity.LoadOrCreate(Path.Combine(Settings.AppSettings.Directory, "identity.key"));
                return _identity;
            }
        }
    }

    public static PeerStore Store
    {
        get
        {
            lock (Gate)
            {
                if (_store is not null) return _store;
                Prepare();
                _store = new PeerStore(Path.Combine(Settings.AppSettings.Directory, "links.json"));
                return _store;
            }
        }
    }

    /// <summary>What a sender is told this viewer is called when pairing: the computer's name.</summary>
    public static string ViewerName => Environment.MachineName;
}
