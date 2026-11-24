using Rayxel.Diagnostics;

namespace Rayxel.Capture;

/// <summary>
/// Decides how much the Deck is asked to spend, from how the link is coping with what it
/// is spending now.
/// <para>
/// Wi-Fi gives no warning of what it can carry, and what it can carry changes - with the
/// distance to the access point, with whatever else is on the air, with a Deck that wakes
/// up and joins a weaker band. A stream asking for more than the air carries does not get
/// a little worse: its packets queue up behind each other, then are dropped, and every
/// frame with a packet missing is lost along with all the frames after it until the next
/// keyframe. So the bitrate follows the link instead of being fixed.
/// </para>
/// <para>
/// Two things say the link is overrun. Frames are lost; or, before that, they take longer
/// to arrive than they did when the link had room - the queue in front of the radio is
/// growing. Either one brings the bitrate down by a third at once. It goes back up slowly,
/// a step at a time, and only after several seconds in which neither was seen: a link
/// that was overrun a moment ago is the likeliest to be overrun again, and a step down
/// costs a keyframe and a moment of a softer picture, while a step up that is too early
/// costs lost frames. A step up that the link at once turns out not to carry makes the
/// next one wait twice as long, so that a link with a hard limit is not tried against it
/// every few seconds.
/// </para>
/// <para>
/// Everything here runs on the network thread, which is the one that sees the frames.
/// </para>
/// </summary>
internal sealed class BitrateGovernor
{
    /// <summary>Below this a picture of the Deck's size is mush, and lower would not help a link this poor anyway.</summary>
    public const int FloorKbps = 3_000;

    // What a judgement is made over, and how often one is made.
    private const long WindowNs = 1_000_000_000;
    private const long JudgeEveryNs = 250_000_000;

    // After a step down, what arrives for this long was sent, or queued, before the Deck
    // heard of it - and begins with a keyframe, the largest frame there is. None of it is
    // held against the new bitrate. After a step up, only the keyframe is let pass: a link
    // that cannot carry the new figure has to be seen not to at once, before its queue
    // has grown into lost frames.
    private const long SettleAfterDropNs = 1_500_000_000;
    private const long SettleAfterRiseNs = 300_000_000;

    // How long the link has to have been clean before a step up - longer each time a step
    // up was overrun straight away, up to the most - and how long a step up has to hold
    // to count as having worked. And how long the lowest delay seen is remembered as what
    // the link does when it has room.
    private const long CleanBeforeRiseNs = 8_000_000_000;
    private const long MostBeforeRiseNs = 64_000_000_000;
    private const long RiseHeldNs = 10_000_000_000;
    private const long BaselineMemoryNs = 20_000_000_000;

    // Lost frames in one window that count as the link being overrun. One alone happens
    // on a good link too: a single packet that a burst of interference took.
    private const int LostFrames = 2;

    // Lost frames all within this much of each other are an outage, not an overrun: the
    // link was briefly not there - interference, the radio waking from power saving - and
    // a lower bitrate would not have kept it. Spread wider than this, or with a queue
    // behind them, they are the link not carrying what is sent.
    private const long OutageNs = 300_000_000;

    // Frames that take this much longer than the link's best are waiting in a queue.
    private const double QueueMs = 20.0;

    // And this little longer is still a link with room.
    private const double RoomMs = 8.0;

    // On a home network a frame takes a few milliseconds from the Deck, ten or twenty over
    // Wi-Fi. More than this is a queue whatever the link did before - which matters when
    // it has been overrun since the stream began, and there is no "before" to go by.
    private const double MostMs = 50.0;

    // A judgement of delay needs enough frames to take a median of. A still screen sends few.
    private const int FramesToJudge = 10;

    private readonly Queue<(long At, double DelayMs)> _frames = new();
    private readonly Queue<(long At, long Count)> _lost = new();
    private readonly Queue<(long At, double MedianMs)> _medians = new();
    private readonly List<double> _scratch = new(128);

    private long _lastJudged, _lastChange, _cleanSince, _lastRise;
    private long _settleNs = SettleAfterDropNs, _beforeRiseNs = CleanBeforeRiseNs;

    public BitrateGovernor(int startKbps)
    {
        Current = startKbps;
    }

    /// <summary>The bitrate to ask for now, in kbit/s.</summary>
    public int Current { get; private set; }

    /// <summary>
    /// A frame that arrived whole. <paramref name="delayMs"/> is how long it took from the
    /// Deck to here, or NaN while the two clocks have not been compared.
    /// </summary>
    public void Frame(long now, double delayMs) => _frames.Enqueue((now, delayMs));

    /// <summary>Frames that will never arrive whole.</summary>
    public void Lost(long now, long count) => _lost.Enqueue((now, count));

    /// <summary>
    /// Looks at the last second, at most four times a second, and returns true when
    /// <see cref="Current"/> has changed. <paramref name="ceilingKbps"/> is the most it may
    /// ask for.
    /// </summary>
    public bool Judge(long now, int ceilingKbps)
    {
        if (Current > ceilingKbps)
        {
            _settleNs = SettleAfterDropNs;
            return Change(now, ceilingKbps, "the most it may ask for is lower now");
        }
        if (now - _lastJudged < JudgeEveryNs) return false;
        _lastJudged = now;
        if (_lastChange == 0) _lastChange = _cleanSince = now;

        long from = now - WindowNs;
        while (_frames.Count > 0 && _frames.Peek().At < from) _frames.Dequeue();
        while (_lost.Count > 0 && _lost.Peek().At < from) _lost.Dequeue();
        while (_medians.Count > 0 && _medians.Peek().At < now - BaselineMemoryNs) _medians.Dequeue();

        // Only what was sent at the bitrate now asked for is evidence about it.
        bool settled = now - _lastChange >= _settleNs;
        long since = Math.Max(from, _lastChange + _settleNs);

        // A step up that has held long enough has worked, and the next may come sooner.
        if (_lastRise != 0 && now - _lastRise >= RiseHeldNs)
        {
            _beforeRiseNs = Math.Max(CleanBeforeRiseNs, _beforeRiseNs / 2);
            _lastRise = 0;
        }

        long lost = 0, firstLost = 0, lastLost = 0;
        foreach (var (at, count) in _lost)
        {
            if (at < since) continue;
            if (lost == 0) firstLost = at;
            lastLost = at;
            lost += count;
        }
        bool outage = lastLost - firstLost <= OutageNs;

        _scratch.Clear();
        int arrived = 0;
        foreach (var (at, delay) in _frames)
        {
            if (at < since) continue;
            arrived++;
            if (!double.IsNaN(delay)) _scratch.Add(delay);
        }

        double median = double.NaN;
        if (_scratch.Count >= FramesToJudge)
        {
            _scratch.Sort();
            median = _scratch[_scratch.Count / 2];
            _medians.Enqueue((now, median));
        }

        double baseline = double.PositiveInfinity;
        foreach (var (_, m) in _medians) baseline = Math.Min(baseline, m);
        bool delayKnown = !double.IsNaN(median) && !double.IsInfinity(baseline);

        if (!settled) return false;

        // Overrun: frames lost over more than a moment, or with a queue behind them;
        // nothing arriving whole at all while something was; or frames waiting in a queue
        // that was not there when the link had room.
        bool queued = delayKnown && (median > baseline + RoomMs || median > MostMs);
        string? overrun = lost >= LostFrames && (!outage || queued)
                ? $"{lost} frames lost in the last second, " +
                  (outage ? "with a queue" : $"over {(lastLost - firstLost) / 1e6:0} ms")
            : lost > 0 && arrived == 0 ? "nothing arrived whole in the last second"
            : delayKnown && (median > baseline + QueueMs || median > MostMs)
                ? median > baseline + QueueMs
                    ? $"frames take {median:0} ms against {baseline:0} ms when the link has room"
                    : $"frames take {median:0} ms, which only a queue explains"
            : null;

        if (overrun is not null)
        {
            _cleanSince = now;
            // The link could not take the last step up: the next waits longer.
            if (_lastRise != 0)
            {
                _beforeRiseNs = Math.Min(MostBeforeRiseNs, _beforeRiseNs * 2);
                _lastRise = 0;
            }
            int lower = Math.Max(FloorKbps, Round(Current * 0.65));
            _settleNs = SettleAfterDropNs;
            return lower < Current && Change(now, lower, overrun);
        }

        // An outage is not held against the link, but it is not a clean second either.
        bool clean = lost == 0 && !queued;
        if (!clean)
        {
            _cleanSince = now;
            return false;
        }

        if (Current >= ceilingKbps || now - Math.Max(_cleanSince, _lastChange) < _beforeRiseNs) return false;
        int higher = Math.Min(ceilingKbps, Round(Current + Math.Max(1_000, Current * 0.15)));
        string why = $"{(now - Math.Max(_cleanSince, _lastChange)) / 1e9:0} s without a lost frame or a queue";
        _settleNs = SettleAfterRiseNs;
        if (!Change(now, higher, why)) return false;
        _lastRise = now;
        return true;
    }

    private bool Change(long now, int kbps, string why)
    {
        if (kbps == Current) return false;
        Log.Info($"bitrate: {Current / 1000.0:0.#} -> {kbps / 1000.0:0.#} Mbit/s ({why})");
        Current = kbps;
        _lastChange = _cleanSince = now;
        return true;
    }

    // In half-megabit steps: finer than that changes nothing anyone could see.
    private static int Round(double kbps) => (int)Math.Round(kbps / 500.0) * 500;
}
