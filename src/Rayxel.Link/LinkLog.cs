namespace Rayxel.Link;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Where the core says what it is doing. It owns no file and no console: each program
/// points <see cref="Sink"/> at its own log - the viewer at its rolling file, the sender at
/// standard output, which systemd's journal takes. Until then lines go to standard output.
/// </summary>
public static class LinkLog
{
    public static Action<LogLevel, string> Sink { get; set; } =
        (level, text) => Console.Out.WriteLine($"{Tag(level)} {text}");

    public static LogLevel Minimum { get; set; } = LogLevel.Info;

    public static void Debug(string text) => Write(LogLevel.Debug, text);
    public static void Info(string text) => Write(LogLevel.Info, text);
    public static void Warn(string text) => Write(LogLevel.Warn, text);
    public static void Error(string text) => Write(LogLevel.Error, text);

    private static void Write(LogLevel level, string text)
    {
        if (level < Minimum) return;
        try { Sink(level, text); }
        catch { /* a log that cannot be written must not take the program down with it */ }
    }

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Debug => "dbg",
        LogLevel.Warn => "WRN",
        LogLevel.Error => "ERR",
        _ => "inf"
    };
}
