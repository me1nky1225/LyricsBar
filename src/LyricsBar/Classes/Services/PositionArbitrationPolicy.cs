namespace LyricsBar.Classes.Services;

/// <summary>这一拍最终采用的位置来自哪条通道。/ Which channel the position published this poll came from.</summary>
public enum PositionSource
{
    /// <summary>还没有做过任何一次仲裁。/ No arbitration has happened yet.</summary>
    None = 0,

    /// <summary>播放器自己上报的时间轴（SMTC）。/ The player's own reported timeline (SMTC).</summary>
    Player = 1,

    /// <summary>播放器进程内存里的位置（目前只有网易云有这条通道）。/ The position read out of the player's process memory (NetEase only, today).</summary>
    Memory = 2,

    /// <summary>本地计时兜底。/ The local clock standing in.</summary>
    LocalClock = 3,
}

/// <summary>
/// 这一拍"为什么这么选"。它只决定日志怎么写，不参与任何计算 —— 排查"歌词不跟手"全靠这几条日志。
/// Why this poll chose what it chose. It only drives logging and feeds no calculation — these lines are the whole basis for
/// diagnosing "the lyrics do not follow".
/// </summary>
public enum PositionDecisionReason
{
    /// <summary>正常推进，没有异常。/ Normal advance.</summary>
    Normal = 0,

    /// <summary>上报值与内存值一致，用上报值（它带真实的时间轴基准）。/ Reported and memory agree; the reported value wins (it carries a real timeline reference).</summary>
    AgreesWithMemory = 1,

    /// <summary>上报值与内存值相差过大，采用内存值。/ Reported and memory disagree wildly; the memory value wins.</summary>
    DiffersFromMemory = 2,

    /// <summary>上报值疑似塌回开头，本拍先不采纳。/ The reported value looks like a collapse back to the start; not adopted this poll.</summary>
    BackwardCollapsePending = 3,

    /// <summary>同一个"塌回开头"的值连续上报了若干拍，承认它是真实跳转。/ The same "collapse" value has been reported for several polls in a row: accepted as a real jump.</summary>
    BackwardCollapseConfirmed = 4,

    /// <summary>向后跳，但不是塌回开头的形状（按真实跳转采纳）。/ A backward jump that is not the collapse shape (accepted as a real jump).</summary>
    BackwardSeek = 5,

    /// <summary>向前跳（用户拖进度条或播放器自己跳）。/ A forward jump (a seek by the user, or the player itself).</summary>
    ForwardSeek = 6,

    /// <summary>播放器不上报时间轴，只能用本地计时。/ The player reports no timeline, so the local clock is all there is.</summary>
    LocalClockFallback = 7,
}

/// <summary>一拍里三个通道各自报了什么。数值由调用方（<see cref="SmtcMediaReader"/>）读好再交进来，策略本身不碰任何 Win32。</summary>
/// <param name="PlayerReports">播放器这一拍是否报出了可用的时间轴位置。/ Whether the player reported a usable timeline position this poll.</param>
/// <param name="PlayerPosition">播放器上报的位置（秒）。/ The position the player reported, in seconds.</param>
/// <param name="HasMemory">内存通道这一拍是否读到了位置。/ Whether the memory channel produced a position this poll.</param>
/// <param name="MemoryPosition">内存通道读到的位置（秒）。/ The position the memory channel read, in seconds.</param>
/// <param name="LocalClockPosition">本地计时当前的值（秒）。/ What the local clock currently reads, in seconds.</param>
/// <param name="IsPlaying">当前是否正在播放。/ Whether playback is currently running.</param>
public readonly record struct PositionObservation(
    bool PlayerReports,
    double PlayerPosition,
    bool HasMemory,
    double MemoryPosition,
    double LocalClockPosition,
    bool IsPlaying);

/// <summary>仲裁结果：采用哪条通道的值。/ The arbitration result: which channel's value is adopted.</summary>
/// <param name="Source">采用的通道。/ The adopted channel.</param>
/// <param name="Position">采用的播放位置（秒）。/ The adopted playback position, in seconds.</param>
/// <param name="Reason">选择的原因（只用于日志）。/ Why it was chosen (logging only).</param>
public readonly record struct PositionDecision(PositionSource Source, double Position, PositionDecisionReason Reason);

/// <summary>
/// 位置仲裁：在"播放器上报的时间轴""播放器进程内存里的位置""本地计时"三条通道之间挑一条。
///
/// 为什么需要它：真机日志（2026-10-15/16）证明网易云上报的 SMTC 时间轴会**周期性塌回 0–1.5 秒**
/// （同一时刻内存通道读到 288.2 秒，上报值却在 0.4 / 3.4 / 49.2 / 33.5 之间乱跳），
/// 而原来的规则是"只要上报值 &gt; 0 就无条件采信"，于是位置被整段采纳、歌词被拽回第一行再追回来 ——
/// 这就是用户反复说的"擦亮反复跳、有时不能依次擦亮"。84 次位置跳变里 74 次是向后跳且**全部落在 3 秒以内**，
/// 正是"塌回开头"的形状。
/// 本策略按三条规则堵住它：
/// ① 内存通道可用时必须与上报值互相印证，两者相差超过 <see cref="AgreementToleranceSeconds"/> 秒就采用内存值
/// （内存值是直接从播放器进程里读的，SMTC 是聚合层）；
/// ② 只有上报值可用时，**向后跳且落回开头附近**（上一拍高于 <see cref="CollapseCeilingSeconds"/>，
/// 落点不高于它）不立刻采纳，先按本地计时顶着，直到同一个落点连续上报 <see cref="BackwardConfirmationPolls"/> 拍
/// 才承认它是真实跳转 —— 脏值通常只出现一拍，真实拖动会一直停在那里；
/// ③ 向前跳与其它位置的向后跳（真seek）一律照旧立即采纳：那是用户自己干的事，挡它反而更奇怪。
/// 需要说明的是：延迟采纳只影响"向后塌回开头"这一种形状，代价是用户真的把进度拖回开头时，
/// 画面最多滞后 <see cref="BackwardConfirmationPolls"/> 拍（约 0.75 秒）才跟上。
///
/// Chooses between the player's reported timeline, the position read out of the player's process memory, and the local clock.
/// Real-hardware logs (2026-10-15/16) proved NetEase's SMTC timeline **periodically collapses back to 0–1.5 s** (at one instant
/// the memory channel read 288.2 s while the reported value bounced between 0.4 / 3.4 / 49.2 / 33.5), and the old rule was
/// "trust any reported value above zero", so the position was adopted wholesale and the lyrics were dragged back to the first
/// line and then chased forward again — exactly the "the wipe keeps jumping / sometimes does not light up in order" the user
/// kept reporting. 74 of 84 position jumps were backwards and **every one of them landed within three seconds**: the collapse
/// shape. Three rules close it: (1) whenever the memory channel is available the two must corroborate each other, and a gap
/// beyond <see cref="AgreementToleranceSeconds"/> seconds hands the win to memory (a direct process read, where SMTC is an
/// aggregation layer); (2) with only the reported value available, a backward jump that lands near the start (previous poll
/// above <see cref="CollapseCeilingSeconds"/>, landing at or below it) is not adopted at once — the local clock holds until the
/// same landing value has been reported for <see cref="BackwardConfirmationPolls"/> consecutive polls, because a dirty value
/// shows up for one poll while a real drag stays put; (3) forward jumps and other backward jumps are adopted immediately as
/// before, since those are the user's own doing. The cost of (2) is that a genuine drag back to the very beginning lags by up to
/// <see cref="BackwardConfirmationPolls"/> polls (about 0.75 s).
/// </summary>
public sealed class PositionArbitrationPolicy
{
    /// <summary>上报值与内存值相差不超过这个秒数就算一致，此时用上报值（它带真实的时间轴基准时刻）。</summary>
    public const double AgreementToleranceSeconds = 3;

    /// <summary>小于这个幅度的变化不算跳变，只是 250 毫秒轮询的正常抖动。/ Changes below this are ordinary jitter, not a jump.</summary>
    public const double DiscontinuitySeconds = 3;

    /// <summary>"塌回开头"的判定上界：落点不超过这个秒数才算这个形状。/ The upper bound of the collapse shape.</summary>
    public const double CollapseCeilingSeconds = 5;

    /// <summary>同一个落点连续被上报多少拍才承认它是真的（3 拍 ≈ 0.75 秒）。/ Consecutive polls before a collapse candidate is believed.</summary>
    public const int BackwardConfirmationPolls = 3;

    /// <summary>连续两拍的上报值相差不超过这个秒数就算"还停在同一个落点"。/ How close two polls must be to count as the same landing spot.</summary>
    public const double ConfirmationToleranceSeconds = 1.5;

    private string _trackKey = string.Empty;
    private double _adopted = double.NaN;
    private double _candidate = double.NaN;
    private int _candidatePolls;

    /// <summary>上一拍采用的位置（换歌后为 <see cref="double.NaN"/>）。/ The position adopted last poll (<see cref="double.NaN"/> after a track change).</summary>
    public double AdoptedPosition => _adopted;

    /// <summary>正在被观察的"塌回开头"候选值，没有候选时为 <see cref="double.NaN"/>。/ The collapse candidate under observation, or <see cref="double.NaN"/>.</summary>
    public double CandidatePosition => _candidate;

    /// <summary>忘掉当前曲目的历史，用于曲目切换或测试。/ Forgets the current track's history (track change or testing).</summary>
    public void Reset()
    {
        _trackKey = string.Empty;
        _adopted = double.NaN;
        ClearCandidate();
    }

    /// <summary>
    /// 仲裁一拍。<paramref name="trackKey"/> 与上一次不同时清空历史 —— 换歌时位置回到开头是正常的，
    /// 不该被当成"塌回开头"。
    /// Arbitrates one poll. A <paramref name="trackKey"/> differing from the previous one clears the history: returning to the
    /// start on a track change is normal and must not read as a collapse.
    /// </summary>
    /// <param name="trackKey">曲目身份（来源 + 曲名 + 歌手）。/ The track identity (source + title + artist).</param>
    /// <param name="observation">这一拍三条通道的值。/ This poll's three channel values.</param>
    public PositionDecision Decide(string trackKey, in PositionObservation observation)
    {
        if (!string.Equals(trackKey, _trackKey, StringComparison.Ordinal))
        {
            _trackKey = trackKey;
            _adopted = double.NaN;
            ClearCandidate();
        }

        // ① 内存通道可用：它和上报值必须互相印证，不一致时以内存为准。
        // The memory channel is available: it has to corroborate the reported value, and wins when they disagree.
        if (observation.HasMemory && double.IsFinite(observation.MemoryPosition))
        {
            if (!observation.PlayerReports)
                return Adopt(PositionSource.Memory, observation.MemoryPosition, PositionDecisionReason.Normal);

            if (Math.Abs(observation.PlayerPosition - observation.MemoryPosition) <= AgreementToleranceSeconds)
                return Adopt(PositionSource.Player, observation.PlayerPosition, PositionDecisionReason.AgreesWithMemory);

            return Adopt(PositionSource.Memory, observation.MemoryPosition, PositionDecisionReason.DiffersFromMemory);
        }

        // ② 只有上报值可用。
        if (observation.PlayerReports && double.IsFinite(observation.PlayerPosition))
        {
            if (!observation.IsPlaying || !double.IsFinite(_adopted))
                return Adopt(PositionSource.Player, observation.PlayerPosition, PositionDecisionReason.Normal);

            var delta = observation.PlayerPosition - _adopted;
            if (delta >= -DiscontinuitySeconds)
            {
                ClearCandidate();
                return Adopt(
                    PositionSource.Player,
                    observation.PlayerPosition,
                    delta > DiscontinuitySeconds ? PositionDecisionReason.ForwardSeek : PositionDecisionReason.Normal);
            }

            // 向后跳。落回开头附近才是那个"脏值"形状，先顶着不采纳。
            // A backward jump. Only the "lands near the start" shape is the dirty value, and it is held off.
            if (_adopted > CollapseCeilingSeconds && observation.PlayerPosition <= CollapseCeilingSeconds)
            {
                if (double.IsNaN(_candidate) ||
                    Math.Abs(observation.PlayerPosition - _candidate) > ConfirmationToleranceSeconds)
                {
                    _candidate = observation.PlayerPosition;
                    _candidatePolls = 1;
                    return Adopt(PositionSource.LocalClock, observation.LocalClockPosition, PositionDecisionReason.BackwardCollapsePending);
                }

                _candidatePolls++;
                if (_candidatePolls < BackwardConfirmationPolls)
                    return Adopt(PositionSource.LocalClock, observation.LocalClockPosition, PositionDecisionReason.BackwardCollapsePending);

                ClearCandidate();
                return Adopt(PositionSource.Player, observation.PlayerPosition, PositionDecisionReason.BackwardCollapseConfirmed);
            }

            // 其它向后跳：用户拖回去了，按真实跳转采纳。
            // Any other backward jump: the user dragged back, so it is a real jump and is adopted.
            ClearCandidate();
            return Adopt(PositionSource.Player, observation.PlayerPosition, PositionDecisionReason.BackwardSeek);
        }

        // ③ 两条都没有：本地计时。
        ClearCandidate();
        return Adopt(PositionSource.LocalClock, observation.LocalClockPosition, PositionDecisionReason.LocalClockFallback);
    }

    private PositionDecision Adopt(PositionSource source, double position, PositionDecisionReason reason)
    {
        if (double.IsFinite(position))
            _adopted = position;

        return new PositionDecision(source, position, reason);
    }

    private void ClearCandidate()
    {
        _candidate = double.NaN;
        _candidatePolls = 0;
    }
}
