using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;
using Rayxel.Diagnostics;
using Rayxel.Link.Control;

namespace Rayxel.Capture;

/// <summary>
/// This computer as a sender: the program that captures and sends, rayxel-send, which lives
/// beside Rayxel.exe and runs on its own, and what the Transmit page knows of it.
/// <para>
/// The sending is not done in this process. Capturing and encoding the desktop has no place
/// in a frame loop that is measured in fractions of a millisecond, the stream should go on
/// when this window closes, and the same program runs on a Steam Deck with no window at all.
/// So this only watches it over its control socket - one subscription, the state pushed on
/// every change - and sends it what the page asks for.
/// </para>
/// </summary>
internal sealed class SenderLink : IDisposable
{
    public const string ExecutableName = "rayxel-send.exe";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Rayxel Send";

    private readonly CancellationTokenSource _stop = new();
    private volatile SenderStatus? _status;
    private volatile string? _error;
    private long _startedAtMs;

    /// <summary>The sender's state as it last said it; null while it is not running or not answering.</summary>
    public SenderStatus? Status => _status;

    /// <summary>What went wrong with the last thing asked, in a sentence for the page; null when nothing did.</summary>
    public string? Error => _error;

    /// <summary>True for a few seconds after it was started from here and has not answered yet.</summary>
    public bool Starting => _status is null && Environment.TickCount64 - Interlocked.Read(ref _startedAtMs) < 8000;

    public static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, ExecutableName);

    /// <summary>Whether this build has the sender beside it at all.</summary>
    public static bool Present => File.Exists(ExecutablePath);

    public SenderLink()
    {
        Interlocked.Exchange(ref _startedAtMs, -1_000_000);
        _ = Task.Run(Watch);
    }

    /// <summary>
    /// Keeps a subscription open while the sender runs, and asks again every two seconds
    /// while it does not: started by hand, by Windows at sign-in or from this page, it shows
    /// up by itself.
    /// </summary>
    private async Task Watch()
    {
        while (!_stop.IsCancellationRequested)
        {
            using var closed = new SemaphoreSlim(0);
            IDisposable? subscription = null;
            try
            {
                subscription = await ControlClient.Subscribe(s => _status = s, () => closed.Release(), cancel: _stop.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Debug($"transmit: no sender to watch: {ex.Message}");
            }

            if (subscription is not null)
            {
                try { await closed.WaitAsync(_stop.Token); }
                catch (OperationCanceledException) { }
                subscription.Dispose();
            }
            _status = null;

            try { await Task.Delay(2000, _stop.Token); }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// Sends one request on a connection of its own, off the frame loop. A refusal is kept
    /// for the page to say; a good answer's state is taken at once rather than at the next push.
    /// </summary>
    public void Send(Func<ControlClient, Task<ControlResponse?>> request)
    {
        _error = null;
        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new ControlClient();
                var answer = await request(client);
                if (answer is null) _error = Ui.Strings.T("txNoAnswer");
                else if (answer.Ok != true) _error = answer.Error ?? Ui.Strings.T("txNoAnswer");
                else if (answer.Status is { } status) _status = status;
            }
            catch (Exception ex)
            {
                _error = ex.Message;
            }
        });
    }

    /// <summary>
    /// Starts rayxel-send from beside this program, cut loose from it: it has no window,
    /// keeps its own log, and goes on when Rayxel closes. Null when it was started,
    /// otherwise what to tell the user.
    /// </summary>
    public string? StartEngine()
    {
        if (!Present) return string.Format(Ui.Strings.T("txNoEngine"), ExecutableName);
        try
        {
            using var process = Process.Start(new ProcessStartInfo(ExecutablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory
            });
            if (process is null) return string.Format(Ui.Strings.T("txStartFailed"), ExecutableName);
            Interlocked.Exchange(ref _startedAtMs, Environment.TickCount64);
            Log.Info($"transmit: started {ExecutablePath} (pid {process.Id})");
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return string.Format(Ui.Strings.T("txStartFailed"), ex.Message);
        }
    }

    /// <summary>
    /// Whether Windows starts the sender at sign-in, without Rayxel: an entry in the user's own
    /// Run key, which Settings → Apps → Startup lists and can switch off as well.
    /// </summary>
    public static bool Autostart
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(RunValue) is string;
            }
            catch (Exception) { return false; }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value) key.SetValue(RunValue, $"\"{ExecutablePath}\"");
                else key.DeleteValue(RunValue, throwOnMissingValue: false);
                Log.Info($"transmit: start with Windows {(value ? "on" : "off")}");
            }
            catch (Exception ex)
            {
                Log.Warn($"transmit: could not change start with Windows: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
