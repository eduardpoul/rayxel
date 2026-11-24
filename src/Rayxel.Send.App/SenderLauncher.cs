using System.ComponentModel;
using System.Diagnostics;
using Rayxel.Link.Control;

namespace Rayxel.Send.App;

/// <summary>
/// Starts the sender, rayxel-send, from this app's own folder, cut loose from the window so
/// that closing the window, or the terminal it was started from, leaves the sender running.
/// </summary>
internal static class SenderLauncher
{
    public static string ExecutableName => OperatingSystem.IsWindows() ? "rayxel-send.exe" : "rayxel-send";

    public static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, ExecutableName);

    /// <summary>Starts it; null when it was started, otherwise what to tell the user.</summary>
    public static string? Start()
    {
        string path = ExecutablePath;
        if (!File.Exists(path)) return Strings.F("noDaemon", ExecutableName);
        try
        {
            using var process = Process.Start(StartInfo(path));
            return process is null ? Strings.F("startFailed", ExecutableName) : null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            return Strings.F("startFailed", ex.Message);
        }
    }

    private static ProcessStartInfo StartInfo(string path)
    {
        string folder = Path.GetDirectoryName(path)!;
        if (OperatingSystem.IsWindows())
        {
            // A console program, so without this a console window would open for it. Its
            // output then goes nowhere; the sender keeps no log file of its own on Windows yet.
            return new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
        }

        // On Linux a child shares the window's session, and a terminal closing takes the
        // whole session with it. setsid -f gives the sender a session of its own and returns
        // at once. Its output, which is its log, goes to a file beside its settings rather
        // than to whatever terminal this app happened to be started from.
        string? setsid = new[] { "/usr/bin/setsid", "/bin/setsid" }.FirstOrDefault(File.Exists);
        if (setsid is null)
            return new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = folder };

        string log = Path.Combine(ControlPaths.ConfigDirectory(), "rayxel-send.log");
        try
        {
            Directory.CreateDirectory(ControlPaths.ConfigDirectory());
        }
        catch (IOException)
        {
            log = "/dev/null";
        }
        catch (UnauthorizedAccessException)
        {
            log = "/dev/null";
        }

        var info = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, WorkingDirectory = folder };
        // Paths go in as the script's own arguments, never pasted into its text, so a space
        // or a quote in a folder name cannot change what runs.
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("exec \"$0\" -f \"$1\" </dev/null >\"$2\" 2>&1");
        info.ArgumentList.Add(setsid);
        info.ArgumentList.Add(path);
        info.ArgumentList.Add(log);
        return info;
    }
}
