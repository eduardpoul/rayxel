using System.Diagnostics;

namespace Rayxel.Link;

/// <summary>
/// This machine's monotonic clock in nanoseconds: CLOCK_MONOTONIC on Linux, the
/// performance counter on Windows. The two ends of a link compare theirs by PING and PONG,
/// so only differences on one machine ever matter.
/// </summary>
public static class Clock
{
    private static readonly double NsPerTick = 1e9 / Stopwatch.Frequency;

    public static long NowNs => (long)(Stopwatch.GetTimestamp() * NsPerTick);

    /// <summary>Wall-clock time in Unix milliseconds, for what is shown and stored, never for measuring.</summary>
    public static long UnixMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
