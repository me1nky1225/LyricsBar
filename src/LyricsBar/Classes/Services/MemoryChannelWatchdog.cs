namespace LyricsBar.Classes.Services;

/// <summary>内存通道这一拍的健康判定。/ The memory channel's health verdict for this poll.</summary>
public enum MemoryChannelVerdict
{
    /// <summary>没问题，继续用。/ Healthy, keep using it.</summary>
    Ok = 0,

    /// <summary>锁到的地址不对劲，作废重扫。/ The locked addresses are wrong: drop them and rescan.</summary>
    Invalidate = 1,
}

/// <summary>
/// 网易云内存通道的看门狗：锁定之后，它读到的位置必须与本地计时**同步推进**。
///
/// 为什么需要它：扫描环节已经验证过"约按 1 倍速前进了两次"，但那只证明锁定的那一刻锁对了。真机日志
/// （2026-10-15/16）里这条通道一天之内重锁 35 次、锁定的地址数在 3–113 之间乱跳，而"通道已失效"一次都没
/// 打过——地址读到值、值也落在秒数区间里，现有的失效判据（连续 4 拍读不到候选值）就永远不触发，锁错了也
/// 一直用下去。本看门狗补上这一层：以 <see cref="WindowSeconds"/> 秒为窗口比较两条通道的**推进量**——
/// 真实位置与本地计时都按 1 倍速走，推进量之差却持续超过 <see cref="RateToleranceSeconds"/> 秒，只可能是
/// 锁到的东西不对（卡死不动、按别的速率漂移）。判定用推进量而不是绝对值，因此"内存与本地计时相差一大截"
/// 这种仲裁每拍都在处理的正常现象不会误伤；用户拖进度条时两条一起跳，也不会误判。
///
/// 只在看得到播放状态时判定：暂停期间位置本来就不该动，卡死与暂停分不开，交给暂停恢复后的新窗口。
/// 通道读不到值（正在重扫）时同样不判定——重扫是服务自己的失败路径在管的事。
/// The watchdog for NetEase's memory channel: once locked, the position it reads has to advance in step with the local
/// clock. The scan already verifies "advanced at about 1x twice", but that only proves the lock was right at the moment
/// it was taken. Real-hardware logs (2026-10-15/16) show 35 re-locks in one day with the locked address count bouncing
/// between 3 and 113, while "channel invalidated" never fired once: the addresses keep reading, the values keep landing
/// in the seconds range, so the existing failure test (four consecutive polls with no candidate) never triggers and a
/// wrong lock is used forever. This watchdog adds that layer: over a <see cref="WindowSeconds"/> window it compares how
/// far the two channels *advanced* — the real position and the local clock both move at 1x, so a persistent gap beyond
/// <see cref="RateToleranceSeconds"/> seconds can only mean the lock is wrong (stuck, or drifting at another rate).
/// Advances rather than absolute values are compared, so the "memory and clock far apart" case the arbiter handles every
/// poll is not affected, and a user drag moves both together without tripping anything.
///
/// It only judges while playback state is visible: while paused the position is not supposed to move and "stuck" cannot
/// be told from "paused", so the verdict waits for a fresh window after the resume. When the channel reads no value
/// (mid-rescan) it does not judge either — the service's own failure path owns that.
/// </summary>
public sealed class MemoryChannelWatchdog
{
    /// <summary>观察窗口的长度（秒）。窗口内两条通道的推进量之差持续超限才作废，单个脏值干扰不了一个窗口。</summary>
    public const double WindowSeconds = 10;

    /// <summary>一个窗口内两条通道推进量允许相差多少秒。1 倍速下健康通道的差在 1 秒以内，卡死/倍速漂移是满窗口的差。</summary>
    public const double RateToleranceSeconds = 5;

    private string _trackKey = string.Empty;
    private bool _windowOpen;
    private double _windowElapsed;
    private double _memoryAtWindowStart;
    private double _clockAtWindowStart;

    /// <summary>忘掉当前曲目的观察历史，用于曲目切换、本地计时复位或测试。/ Forgets the current track's history (track change, clock reset, testing).</summary>
    public void Reset()
    {
        _trackKey = string.Empty;
        _windowOpen = false;
        _windowElapsed = 0;
        _memoryAtWindowStart = 0;
        _clockAtWindowStart = 0;
    }

    /// <summary>
    /// 喂进一拍观察值，返回本拍的健康判定。
    /// Feeds one poll's observation in and returns this poll's health verdict.
    /// </summary>
    /// <param name="trackKey">曲目身份（来源 + 曲名 + 歌手）。/ The track identity (source + title + artist).</param>
    /// <param name="hasMemory">内存通道这一拍是否读到了位置。/ Whether the memory channel produced a position this poll.</param>
    /// <param name="memoryPosition">内存通道读到的位置（秒）。/ The position the memory channel read, in seconds.</param>
    /// <param name="localClockPosition">本地计时当前的值（秒）。/ What the local clock currently reads, in seconds.</param>
    /// <param name="isPlaying">当前是否正在播放。/ Whether playback is currently running.</param>
    /// <param name="elapsedSeconds">距上一拍过了多少秒。/ Seconds since the previous poll.</param>
    public MemoryChannelVerdict Observe(
        string trackKey,
        bool hasMemory,
        double memoryPosition,
        double localClockPosition,
        bool isPlaying,
        double elapsedSeconds)
    {
        var usable = isPlaying &&
                     hasMemory &&
                     double.IsFinite(memoryPosition) &&
                     double.IsFinite(localClockPosition);

        // 暂停、通道正在重扫：都不是下结论的时候，窗口作废等下一次。
        // A pause or a mid-rescan channel: neither is a moment to judge, so the window is dropped.
        if (!usable)
        {
            _trackKey = trackKey;
            _windowOpen = false;
            _windowElapsed = 0;
            return MemoryChannelVerdict.Ok;
        }

        // 换歌、或窗口还没开（上一段不可观察刚结束）：以当前值为基线**立刻**开窗。不能"这一拍只关窗、下一拍才开"——
        // 那样每个窗口都要少算一拍，且换歌后第一拍不产生任何观察。
        // A track change, or no open window yet (the previous unobservable stretch just ended): open the window **now**
        // with the current values as the baseline. It must not merely close this poll and open on the next one — that
        // would cost every window a poll and observe nothing on the first poll after a track change.
        if (!_windowOpen || !string.Equals(trackKey, _trackKey, StringComparison.Ordinal))
        {
            _trackKey = trackKey;
            _windowOpen = true;
            _windowElapsed = 0;
            _memoryAtWindowStart = memoryPosition;
            _clockAtWindowStart = localClockPosition;
            return MemoryChannelVerdict.Ok;
        }

        _windowElapsed += Math.Max(0, elapsedSeconds);
        if (_windowElapsed < WindowSeconds)
            return MemoryChannelVerdict.Ok;

        // 窗口到头，结算。无论判什么都被重新开窗：作废之后要重新观察新锁，健康之后也要继续观察。
        // The window is up. It is re-opened either way: after an invalidation the fresh lock needs watching, and a
        // healthy channel needs watching too.
        var memoryAdvance = memoryPosition - _memoryAtWindowStart;
        var clockAdvance = localClockPosition - _clockAtWindowStart;
        _windowOpen = false;
        _windowElapsed = 0;

        return Math.Abs(memoryAdvance - clockAdvance) > RateToleranceSeconds
            ? MemoryChannelVerdict.Invalidate
            : MemoryChannelVerdict.Ok;
    }
}
