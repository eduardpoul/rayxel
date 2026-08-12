using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Rayxel.Diagnostics;

public enum LogLevel { Trace, Debug, Info, Warn, Error }

/// <summary>
/// Console + file logging. Timestamps come from the same QPC clock the frame
/// telemetry uses, so log lines and frame records can be correlated directly.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly long Origin = Stopwatch.GetTimestamp();
    private static StreamWriter? _file;

    /// <summary>
    /// Info by default. Debug is for someone chasing a problem, and it is a wall of text
    /// per second — the log file is the record, not a monitor.
    /// </summary>
    public static LogLevel MinLevel { get; set; } = LogLevel.Info;

    public static string? FilePath { get; private set; }

    [DllImport("kernel32.dll")] private static extern IntPtr GetConsoleWindow();

    private static bool? _hasConsole;

    /// <summary>
    /// Whether anything would see console output. A windowed build has no console, and
    /// writing colour codes into nothing is pointless. Resolved on first use rather than
    /// in a field initializer, so it cannot depend on whether something touched this class
    /// before <c>--console</c> attached one.
    /// </summary>
    private static bool HasConsole => _hasConsole ??= GetConsoleWindow() != IntPtr.Zero;

    public static void OpenFile(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, $"upscaler-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        _file = new StreamWriter(FilePath, append: false, Encoding.UTF8) { AutoFlush = true };
        Info($"log file: {FilePath}");
    }

    /// <summary>Milliseconds since process start, on the QPC clock.</summary>
    public static double ElapsedMs => (Stopwatch.GetTimestamp() - Origin) * 1000.0 / Stopwatch.Frequency;

    public static void Trace(string m) => Write(LogLevel.Trace, m);
    public static void Debug(string m) => Write(LogLevel.Debug, m);
    public static void Info(string m) => Write(LogLevel.Info, m);
    public static void Warn(string m) => Write(LogLevel.Warn, m);
    public static void Error(string m) => Write(LogLevel.Error, m);

    public static void Error(string m, Exception ex) => Write(LogLevel.Error, $"{m}: {ex.GetType().Name}: {ex.Message}");

    /// <summary>Section header, so a log file can be skimmed for phase boundaries.</summary>
    public static void Section(string title)
    {
        var bar = new string('─', Math.Max(0, 72 - title.Length - 3));
        Raw($"\n── {title} {bar}");
    }

    private static void Write(LogLevel level, string message)
    {
        if (level < MinLevel) return;
        var tag = level switch
        {
            LogLevel.Trace => "trc",
            LogLevel.Debug => "dbg",
            LogLevel.Info => "inf",
            LogLevel.Warn => "WRN",
            LogLevel.Error => "ERR",
            _ => "???"
        };
        Raw($"[{ElapsedMs,10:F3}] {tag} {message}", level);
    }

    /// <summary>Writes a line verbatim to both sinks (used for tables and headers).</summary>
    public static void Raw(string line, LogLevel level = LogLevel.Info)
    {
        lock (Gate)
        {
            if (HasConsole)
            {
                try
                {
                    var prev = Console.ForegroundColor;
                    Console.ForegroundColor = level switch
                    {
                        LogLevel.Trace => ConsoleColor.DarkGray,
                        LogLevel.Debug => ConsoleColor.Gray,
                        LogLevel.Warn => ConsoleColor.Yellow,
                        LogLevel.Error => ConsoleColor.Red,
                        _ => prev
                    };
                    Console.WriteLine(line);
                    Console.ForegroundColor = prev;
                }
                catch (IOException) { /* console went away mid-run; the file is the record */ }
            }

            _file?.WriteLine(line);
        }
    }

    public static void Close()
    {
        lock (Gate)
        {
            _file?.Flush();
            _file?.Dispose();
            _file = null;
        }
    }
}
