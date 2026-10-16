namespace LyricsBar.Classes.Services;

/// <summary>内存锁定这一拍的健全性判定。/ The memory lock's sanity verdict for this poll.</summary>
public enum MemoryLockSanityVerdict
{
    /// <summary>可信，照常交给仲裁。/ Believable: hand it to the arbiter as usual.</summary>
    Ok = 0,

    /// <summary>新锁定的位置与本地计时相差过大，不像当前曲目的位置。/ A freshly locked position sits too far from the local clock to be this track's position.</summary>
    RejectLockAgreement = 1,

    /// <summary>换歌之后位置迟迟不归零，锁定已经不是当前曲目的位置。/ The position never returned to zero after the track change: the lock is no longer this track's position.</summary>
    RejectTrackReset = 2,
}

/// <summary>
/// 内存锁定的健全性检查：锁到的值"能读、在秒数区间、按 1 倍速前进"还不够——那只能证明它是个计时器，
/// 不能证明它是**当前曲目的播放位置**。
///
/// 为什么需要它（真机日志，2026-10-16 14:00，v1.4.6）：网易云更新后扫描开始锁到一个**全局计时器**——
/// 从当天 12:39:12 起算、严格 1 倍速、内存里有数千份副本（一次锁 4192～5161 个地址，健康锁定只有 37 个）。
/// 于是播放位置变成 4776～4969 秒的荒谬值，**换歌也不归零**：每一首新歌都从同一个递增计数器的当前位置选行，
/// 直接越过所有行尾时间戳——歌词停在最后一行不动、行次与所唱内容对不上。用户报的"匹配不准、不会动了"
/// 就是这一处。原有的失效判据（连续 4 拍读不到候选值）对它完全无效：地址一直都读得到。
/// 本策略用两条曲目本身的性质识别它：
/// ① **新锁定的位置必须与本地计时相差不超过 <see cref="LockAgreementSeconds"/> 秒**。本地计时的基准
/// 挂在最近一次采信的真实位置上，正常偏差只有几秒；app 启动时歌已播到一半这个场景也就几百秒。
/// 而全局计时器与当前曲目毫无关系，一锁上就相差几十分钟——正好落在容差之外。检查只在新锁定
/// （通道从读不到变为读得到）那一拍做：已建立、正在跟进的锁定允许用户拖进度条造成的阶跃。
/// ② **换歌之后位置必须在 <see cref="TrackResetGraceSeconds"/> 秒内落回 <see cref="TrackResetCeilingSeconds"/>
/// 秒以内**。新歌从零开始唱是铁律；全局计时器不会理睬换歌。换歌后的第一首（app 启动时那首）不布防——
/// 启动时歌可能已经播了一大半，位置本来就不是从零开始。
///
/// 判定为拒绝时，调用方应当把这一拍的内存值当作"读不到"（不给仲裁），并请求重新扫描：
/// 宁愿让本地计时顶着（它顶多漂秒级），也不能把计时器当播放位置发布出去。
/// The sanity check on a memory lock: a value that reads fine, sits in the seconds range, and advances at 1x is not
/// enough — that proves it is a timer, not that it is **the current track's playback position**.
///
/// Why it exists (real-hardware log, 2026-10-16 14:00, v1.4.6): after a NetEase update the scan began locking onto a
/// **global timer** — started 12:39:12 that day, strictly 1x, with thousands of copies in memory (one lock grabbed
/// 4192–5161 addresses where a healthy lock has 37). The playback position therefore became an absurd 4776–4969
/// seconds and **never returned to zero on a track change**: every new song picked its line from the same climbing
/// counter and sailed past every line's timestamp — the lyrics froze on the last line and no longer matched what was
/// being sung, which is exactly what the user reported ("matching is off and it stopped moving"). The existing
/// failure test (four consecutive polls with no candidate value) is powerless here: the addresses always read. This
/// policy recognises it by two properties of the track itself: (1) a freshly locked position must sit within
/// LockAgreementSeconds seconds of the local clock, whose baseline is pinned to the last trusted real position (a
/// few seconds of normal error, or a few hundred when the app started mid-song), while a global timer has nothing
/// to do with the current track and differs by minutes the moment it locks; the check runs only on the poll where
/// the channel goes from unreadable to readable, since an established lock may step when the user drags the seek
/// bar; (2) after a track change the position must fall back under TrackResetCeilingSeconds within
/// TrackResetGraceSeconds seconds — a new song starts at zero, and a global timer does not care about track
/// changes. The very first track after app start is not armed: there the song may already be half over, so a
/// non-zero position is normal.
///
/// On a rejection the caller should treat this poll's memory value as unreadable (do not hand it to the arbiter)
/// and request a rescan: letting the local clock stand in (it drifts by seconds at worst) beats publishing a timer
/// as the playback position.
/// </summary>
public sealed class MemoryLockSanityPolicy
{
    /// <summary>新锁定的位置与本地计时允许相差多少秒（约 15 分钟）。正常偏差只有几秒；app 启动时歌已播过半也就是几百秒。</summary>
    public const double LockAgreementSeconds = 900;

    /// <summary>换歌之后位置应落到的上限（秒）。新歌从零开始，留点余量给播放器切态的瞬间。</summary>
    public const double TrackResetCeilingSeconds = 20;

    /// <summary>换歌之后给位置归零的宽限（秒）。内存位置约每秒更新一次，三拍足够它换过来。</summary>
    public const double TrackResetGraceSeconds = 3;

    /// <summary>换歌后通道正在重扫时最多等多久（秒），超时放弃布防——重扫是服务自己的失败路径在管的事。</summary>
    public const double TrackResetMaxWaitSeconds = 10;

    private bool _channelWasAvailable;
    private bool _trackResetArmed;
    private double _trackResetElapsed;

    /// <summary>忘掉全部状态，用于手动复位本地计时或测试。/ Forgets all state (a manual clock reset, or testing).</summary>
    public void Reset()
    {
        _channelWasAvailable = false;
        _trackResetArmed = false;
        _trackResetElapsed = 0;
    }

    /// <summary>
    /// 喂进一拍观察值，返回本拍的健全性判定。
    /// Feeds one poll's observation in and returns this poll's sanity verdict.
    /// </summary>
    /// <param name="armTrackReset">这一拍是否要布防"换歌后位置归零"检查（换歌且不是 app 启动后的第一首）。/ Whether to arm the "position returns to zero after the track change" check (a track change that is not the first track after app start).</param>
    /// <param name="hasMemory">内存通道这一拍是否读到了位置。/ Whether the memory channel produced a position this poll.</param>
    /// <param name="memoryPosition">内存通道读到的位置（秒）。/ The position the memory channel read, in seconds.</param>
    /// <param name="localClockPosition">本地计时当前的值（秒）。/ What the local clock currently reads, in seconds.</param>
    /// <param name="isPlaying">当前是否正在播放。/ Whether playback is currently running.</param>
    /// <param name="elapsedSeconds">距上一拍过了多少秒。/ Seconds since the previous poll.</param>
    public MemoryLockSanityVerdict Observe(
        bool armTrackReset,
        bool hasMemory,
        double memoryPosition,
        double localClockPosition,
        bool isPlaying,
        double elapsedSeconds)
    {
        if (armTrackReset)
        {
            _trackResetArmed = true;
            _trackResetElapsed = 0;
        }

        // ① 新锁定（从读不到变为读得到）：与本地计时相差过大就不是当前曲目的位置。
        // A fresh lock (unreadable to readable): too far from the local clock to be this track's position.
        var becameAvailable = hasMemory && !_channelWasAvailable;
        _channelWasAvailable = hasMemory;

        if (becameAvailable &&
            double.IsFinite(memoryPosition) &&
            double.IsFinite(localClockPosition) &&
            Math.Abs(memoryPosition - localClockPosition) > LockAgreementSeconds)
        {
            return MemoryLockSanityVerdict.RejectLockAgreement;
        }

        // ② 换歌后位置不归零。
        // The position never returned to zero after the track change.
        if (!_trackResetArmed)
            return MemoryLockSanityVerdict.Ok;

        _trackResetElapsed += Math.Max(0, elapsedSeconds);

        // 落回来了：布防解除。暂停中不判定（切歌边界上播放器可能还没把位置换过来），等恢复。
        // It came back down: disarm. No verdict while paused (at a track boundary the player may not have swapped the
        // position yet), so wait for the resume.
        if (hasMemory && double.IsFinite(memoryPosition) && memoryPosition <= TrackResetCeilingSeconds)
        {
            _trackResetArmed = false;
            return MemoryLockSanityVerdict.Ok;
        }

        if (!isPlaying || _trackResetElapsed < TrackResetGraceSeconds)
            return MemoryLockSanityVerdict.Ok;

        if (hasMemory && double.IsFinite(memoryPosition) && memoryPosition > TrackResetCeilingSeconds)
        {
            _trackResetArmed = false;
            return MemoryLockSanityVerdict.RejectTrackReset;
        }

        // 等太久通道都还没回来：放弃布防，重扫是服务自己的失败路径在管。
        // Waited too long with no channel: drop the arming; the service's own failure path owns the rescan.
        if (_trackResetElapsed >= TrackResetMaxWaitSeconds)
            _trackResetArmed = false;

        return MemoryLockSanityVerdict.Ok;
    }
}
