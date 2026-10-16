namespace LyricsBar.Classes.Services;

/// <summary>本地计时顶替的这一拍该继续顶还是冻住。/ Whether the local clock keeps standing in or freezes this poll.</summary>
public enum LocalClockStandInVerdict
{
    /// <summary>继续顶替。/ Keep standing in.</summary>
    Continue = 0,

    /// <summary>顶得太久了，冻结。/ Stood in for too long: freeze.</summary>
    Freeze = 1,
}

/// <summary>
/// 本地计时的顶替上限：连续顶替超过 <see cref="MaximumStandInSeconds"/> 秒，就冻结播放位置，而不是让秒表继续跑偏。
///
/// 为什么需要它：本地计时是最后的兜底，但它与真实位置之间没有任何反馈——用户在音乐软件里拖进度条、播放器
/// 自己跳章节，系统接口都看不见。真实通道（上报值/内存）在时，每次采信都会把计时的基准重新钉到真实位置上
/// （见 <c>SmtcMediaReader</c> 的 <c>_localClockBaselineSeconds</c>），偏差被限制在一两次轮询之间；可一旦两条
/// 真实通道都长时间不可用（内存通道扫描失败——真机日志里一天 28 次——而上报值又是脏的或干脆没有），秒表会
/// 无限期地偏离真实播放位置，歌词看似在走、其实越走越偏。上游 AF Media Bar 的对应做法是"读不到就冻结 3 秒"
/// （<c>NetEaseMemoryContinuity</c>），本项目没有那个 3 秒窗口，改成这条上限：顶替满 30 秒就把位置钉在
/// **最近一次采信的真实位置**上，等真实通道恢复再解除——冻结的歌词至少不再撒谎，而恢复时会重新钉基线。
///
/// 只统计"正在播放时的顶替"：暂停期间位置本来就不动，顶替没有偏差可积累，一暂停就清零。
/// The cap on how long the local clock may stand in: past <see cref="MaximumStandInSeconds"/> seconds of continuous
/// stand-in the position freezes instead of letting the stopwatch drift on. The local clock is the last fallback, but
/// nothing feeds back into it — the user dragging the seek bar or the player skipping a chapter is invisible to the
/// system interfaces. While a real channel (reported value / memory) is available, every adoption re-pins the clock's
/// baseline to the real position (see _localClockBaselineSeconds in SmtcMediaReader), so the error stays within a poll
/// or two; but when both real channels are unavailable for a long stretch (the memory channel failing to scan — 28 times
/// in a single day of real-hardware logs — while the reported value is dirty or absent), the stopwatch drifts from the
/// real position indefinitely: the lyrics look like they are moving but move further and further away. Upstream AF Media
/// Bar's answer is "freeze for three seconds when reads fail" (NetEaseMemoryContinuity); this project has no such
/// window, so instead there is this cap: after thirty seconds the position is pinned at the **last position adopted from
/// a real channel** until a real channel returns, which re-pins the baseline. Frozen lyrics at least stop lying.
///
/// Only stand-in while playing counts: while paused the position is not supposed to move, so there is no error to
/// accumulate and the counter clears on every pause.
/// </summary>
public sealed class LocalClockStandInGuard
{
    /// <summary>连续顶替多少秒就冻结。真机场景里这个时长足够等到一次内存重扫（1–2 秒）或一次上报恢复。</summary>
    public const double MaximumStandInSeconds = 30;

    private double _standInSeconds;

    /// <summary>当前这一轮连续顶替累计的秒数。/ Seconds accumulated in the current continuous stand-in.</summary>
    public double StandInSeconds => _standInSeconds;

    /// <summary>清零，用于顶替被真实通道接走、用户手动复位或测试。/ Clears the counter (a real channel took over, a manual reset, testing).</summary>
    public void Reset() => _standInSeconds = 0;

    /// <summary>
    /// 喂进一拍，返回本拍该继续顶还是冻住。
    /// Feeds one poll in and returns whether to keep standing in or freeze.
    /// </summary>
    /// <param name="isStandingIn">这一拍采用的位置是否来自本地计时。/ Whether this poll's adopted position came from the local clock.</param>
    /// <param name="isPlaying">当前是否正在播放。/ Whether playback is currently running.</param>
    /// <param name="elapsedSeconds">距上一拍过了多少秒。/ Seconds since the previous poll.</param>
    public LocalClockStandInVerdict Observe(bool isStandingIn, bool isPlaying, double elapsedSeconds)
    {
        if (!isStandingIn || !isPlaying)
        {
            _standInSeconds = 0;
            return LocalClockStandInVerdict.Continue;
        }

        _standInSeconds += Math.Max(0, elapsedSeconds);
        return _standInSeconds >= MaximumStandInSeconds
            ? LocalClockStandInVerdict.Freeze
            : LocalClockStandInVerdict.Continue;
    }
}
