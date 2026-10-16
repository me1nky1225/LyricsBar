using System.Diagnostics;
using System.Runtime.InteropServices;
using LyricsBar.Classes.Interop;
using LyricsBar.Classes.Utils;

namespace LyricsBar.Classes.Services;

/// <summary>
/// 网易云音乐的内存播放位置通道。2026-10-14 实测：网易云上报的 SMTC 时间轴 Start/End/Position/MinSeek/MaxSeek
/// 几乎全是 0、LastUpdatedTime 是 1601-01-01；2026-10-15/16 的真机日志进一步表明它**并不是恒零**，而是会周期性
/// 塌回 0–1.5 秒（同一时刻本通道读到 288.2 秒，上报值却在 0.4 / 3.4 / 49.2 / 33.5 之间乱跳）——所以播放位置
/// 不能靠 SMTC，而它确实存在于 <c>cloudmusic.exe</c> 的私有内存里，是一串以秒为单位、约每秒更新一次、按 1 倍速
/// 前进的 double，而且**跟着用户拖动进度条一起跳**。本项目不采用上游那种"按网易云版本记偏移量"的做法
/// （版本一更新就失效），而是每次启动时现场扫描：
/// <list type="number">
///   <item>把私有内存里所有"看起来像秒数"的 double 收集起来；</item>
///   <item>隔两秒再扫一遍，只留"恰好前进了这两秒"的地址；</item>
///   <item>把数值彼此接近（同一份位置的多个副本）的地址聚成一簇，再隔两秒单独复核这十几个地址；</item>
///   <item>之后每 400 毫秒读一次这十几个地址，取中位数附近最靠前的那个值当位置。</item>
/// </list>
/// 扫描约 1-2 秒，之后每次轮询只是十几次 8 字节读取，代价可以忽略。读不到（权限被拒、网易云没运行、版本换成了
/// 别的位置表示）就返回 false，调用方退回本地计时——这个通道是"锦上添花"，不允许它把歌词栏搞坏。
/// 锁定之后还有 <see cref="MemoryChannelWatchdog"/> 兜底：锁定值必须与本地计时同步推进，持续不符就作废重扫
/// （"读得到值"不等于"锁对了地址"，真机日志里 address 数在 3–113 之间乱跳、而失效判据一次都没触发过）。
/// <para>
/// v1.4.8 起扫描降为**兜底**，首选是 <see cref="NeteaseSignatureChannel"/>：在 <c>cloudmusic.dll</c> 的 <c>.text</c>
/// 里按上游那两条特征码直接读出播放器自己的位置、时长、状态（`tools/SignatureProbe` 已实测命中，见
/// <c>HANDOFF-NETEASE-SYNC.md</c> §3.8）。上面这套扫描分不出"当前播放位置"和"随便一个按 1 倍速前进的计时器"
/// ——真机日志里它锁到过一个从当天 12:39 起算的全局计时器（位置 4800 秒、换歌不归零、几千个副本），特征码通道
/// 没有这个歧义，而且它还能给出真实时长。特征码失效（网易云升级、读失败累计）时自动退回本文件这条扫描通道。
/// From v1.4.8 the scan is the **fallback** and <see cref="NeteaseSignatureChannel"/> comes first: it reads the player's
/// own position, duration and status straight out of cloudmusic.dll's .text through upstream's two signatures (measured
/// to match, see HANDOFF-NETEASE-SYNC.md §3.8). The scan above cannot tell "the current playback position" from "any
/// timer advancing at 1x" — the real-hardware log shows it locking a global timer started at 12:39 that day (position
/// 4800 s, never reset on a track change, thousands of copies) — while the signature channel has no such ambiguity and
/// reports the real duration too. When the signatures fail (a NetEase update, too many read failures) the scan channel
/// in this file takes over again.
/// </para>
/// The in-memory playback-position channel for NetEase Cloud Music. Measured 2026-10-14: the SMTC timeline NetEase
/// reports is almost entirely zero (Start/End/Position/MinSeek/MaxSeek all 0, LastUpdatedTime 1601-01-01); the
/// 2026-10-15/16 real-hardware logs further show it is **not** permanently zero — it periodically collapses back to
/// 0–1.5 s (at one instant this channel read 288.2 s while the reported value bounced between 0.4 / 3.4 / 49.2 /
/// 33.5) — so the playback position cannot come from SMTC. It really does live in cloudmusic.exe's private memory as a
/// seconds-valued double updated about once a second at 1x speed, and it **follows the user dragging the seek bar**.
/// This project rejects the upstream approach of recording per-NetEase-version offsets (one update breaks it) and
/// instead scans for it on every start: collect every double that looks like a second count, rescan two seconds later
/// keeping only the addresses that advanced by exactly those two seconds, cluster the ones that agree with each other,
/// re-verify that handful two seconds after that, and from then on read those dozen addresses every 400 ms and take the
/// leading value near their median. The scan costs a second or two; each later poll is a dozen eight-byte reads.
/// Anything unreadable (rights refused, NetEase not running, a version storing the position differently) returns false
/// and the caller falls back to the local clock: this channel is a bonus and is never allowed to break the bar. Past
/// the lock there is also MemoryChannelWatchdog as a backstop: the locked value has to advance in step with the local
/// clock, and a persistent disagreement means a rescan — "it reads values" is not the same as "it locked the right
/// addresses" (the address count bounced between 3 and 113 in the real-hardware logs while the failure test never
/// fired once).
/// </summary>
internal sealed class NeteasePositionService : IDisposable
{
    /// <summary>网易云的主进程名。它是 CEF 外壳，播放位置就在这个进程里。</summary>
    private const string PlayerProcessName = "cloudmusic";

    /// <summary>最多依次尝试几个网易云进程（取私有内存最大的几个）。</summary>
    private const int MaximumProcessCandidates = 3;

    /// <summary>单块区域超过这个大小就跳过：位置不会藏在一块几 GB 的预留堆里。</summary>
    private const long MaximumRegionBytes = 64L * 1024 * 1024;

    /// <summary>扫描时的读取块大小。</summary>
    private const int ScanBufferBytes = 4 * 1024 * 1024;

    /// <summary>两遍扫描之间的间隔：太短则"前进了多少"分辨不出来，太长则用户得干等。</summary>
    private const int ScanGapMilliseconds = 2000;

    /// <summary>锁定之后的轮询间隔。位置本身约每秒更新一次，400 毫秒足够顺滑。</summary>
    private const int PollIntervalMilliseconds = 400;

    /// <summary>
    /// 特征码通道的轮询间隔。它每拍只读几个字段，可以比扫描通道密得多：那个 double 是连续量（真机实测一秒里
    /// 前进 1.000 秒、小数位在变），100 毫秒一拍意味着"擦亮"几乎跟着真实播放位置走，不再依赖外推。
    /// Poll interval for the signature channel. It reads a handful of fields per poll, so it can be far denser than the
    /// scan channel: that double is continuous (measured to advance by exactly 1.000 s over one second, with the
    /// fractional digits moving), so a 100 ms poll makes the wipe follow the real playback position instead of leaning on
    /// extrapolation.
    /// </summary>
    private const int SignaturePollIntervalMilliseconds = 100;

    /// <summary>连续这么多次轮询都读不到候选值，就认为地址失效（换版本、进程重启），重新扫描。</summary>
    private const int MaximumPollFailures = 4;

    /// <summary>两次扫描之间的最短间隔，失败时避免把自己扫成 CPU 风扇。</summary>
    private const int RescanCooldownMilliseconds = 20_000;

    /// <summary>
    /// 地址失效（多半是换歌）到允许重新扫描之间的等待。换歌是正常事件，不该吃失败退避那份 20 秒冷却——否则每换一首歌
    /// 都要先靠本地计时撑二十来秒才能对回真实进度。
    /// How long to wait before re-scanning once the locked addresses stop reading. A track change is a normal event and must
    /// not inherit the 20-second back-off meant for failed scans: that would leave every new song on the local clock for
    /// twenty-odd seconds before the real position is picked up again.
    /// </summary>
    private const int RescanAfterLossMilliseconds = 1_500;

    /// <summary>这么久没有网易云在放，就松开进程句柄；下次需要时重新扫描。</summary>
    private const int IdleShutdownMilliseconds = 10 * 60 * 1000;

    private const double MinimumPositionSeconds = 0.2;
    private const double MaximumPositionSeconds = 6 * 60 * 60;

    /// <summary>
    /// 第二遍筛选的容差。这一遍只是粗筛——两遍扫描的速度不可能完全一致，靠得住的判据是第三遍的单点复核，
    /// 所以这里宁可放宽一点，别把真身筛掉。
    /// Tolerance for the second pass. That pass only pre-filters: two scans can never run at exactly the same speed and the
    /// trustworthy test is the third pass's point-by-point re-check, so this one is deliberately loose rather than risk
    /// dropping the real position.
    /// </summary>
    private const double ScanAdvanceToleranceSeconds = 1.4;

    /// <summary>
    /// 第三遍复核的容差。复核时这一簇的地址是紧接着一个个读的，彼此间隔只差几微秒，所以这里可以卡得很紧。
    /// Tolerance for the third pass's re-check. Those addresses are read one after another within microseconds of each other,
    /// so this one can be tight.
    /// </summary>
    private const double ConfirmAdvanceToleranceSeconds = 0.45;

    /// <summary>同一份位置的多个副本之间允许相差多少秒。/ How far apart copies of the same position may sit.</summary>
    private const double ClusterToleranceSeconds = 2.5;

    /// <summary>一簇至少要这么多地址才认。/ Minimum addresses in a cluster before it is believed.</summary>
    private const int MinimumClusterSize = 3;

    /// <summary>复核几簇就够。最大的那簇通常就是位置，多备两簇只是防止偶然的成簇把它挤下去。</summary>
    private const int MaximumClustersToConfirm = 3;

    private readonly object _gate = new();

    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private long _lastRequestTicks;
    private double _position;
    private DateTimeOffset _sampledAt;
    private bool _hasPosition;

    /// <summary>看门狗请求作废当前锁定（见 <see cref="InvalidateChannel"/>）。轮询循环每拍开头检查这个标志。</summary>
    private volatile bool _invalidateRequested;

    /// <summary>作废原因，只用于日志。与 <see cref="_invalidateRequested"/> 一起由看门狗写入。</summary>
    private volatile string? _invalidateReason;

    /// <summary>作废后到允许重新扫描的冷却毫秒数。与 <see cref="_invalidateRequested"/> 一起写入。</summary>
    private volatile int _invalidateCooldownMilliseconds;

    /// <summary>特征码通道，首选的位置来源（见类注释）。/ The signature channel, the preferred position source.</summary>
    private readonly NeteaseSignatureChannel _signature = new();

    /// <summary>当前位置是不是特征码通道给的。false 表示这一拍是扫描通道（或两者都没有）。</summary>
    private bool _usingSignature;

    /// <summary>特征码通道顺带读到的时长（秒），0 表示本拍没读到。受 <see cref="_gate"/> 保护。</summary>
    private double _signatureDurationSeconds;

    /// <summary>特征码通道顺带读到的播放状态。受 <see cref="_gate"/> 保护。</summary>
    private bool _signatureIsPlaying;

    /// <summary>已经记过日志的特征码歌曲 id，避免同一首歌每秒重复一行。受 <see cref="_gate"/> 保护。</summary>
    private string _signatureSongId = string.Empty;

    /// <summary>下一次允许读歌曲 id 的时刻（<see cref="Environment.TickCount64"/>），一秒最多一次。</summary>
    private long _nextSongIdPollTicks;

    /// <summary>读歌曲 id 的最小间隔（毫秒）。/ Minimum interval between song-id reads, in milliseconds.</summary>
    private const int SongIdPollIntervalMilliseconds = 1000;

    /// <summary>
    /// 健全性退避的冷却：锁定"能读但不像当前曲目的位置"（全局计时器、换歌不归零）时，立即重扫只会再锁一次垃圾
    /// ——真机日志里 13:58–14:02 就是这样每 10 几秒扫一次、每次锁几千个垃圾地址，白烧 430MB 的读取。
    /// 等半分钟再扫，期间本地计时顶替（它换歌归零、跟歌 1 倍速，比垃圾位置靠谱得多）。
    /// The cool-down after a sanity rejection: when the lock "reads but does not look like this track's position" (a
    /// global timer, never resetting on a track change), rescanning at once only locks garbage again — the real-hardware
    /// log shows exactly that between 13:58 and 14:02, one scan every ten-odd seconds each burning a 430 MB read for a
    /// few thousand garbage addresses. Waiting half a minute instead lets the local clock stand in (it zeroes on a track
    /// change and follows at 1x, far more trustworthy than a garbage position).
    /// </summary>
    private const int SanityRescanCooldownMilliseconds = 30_000;

    /// <summary>
    /// 取当前播放位置（秒）与采样时刻。来源不是网易云、或还没扫描出结果时返回 false。
    /// 采样时刻要一并返回：上层的 <see cref="TaskbarExperiencePolicy"/> 会以它为基准按 1 倍速外推，
    /// 把内存里每秒才更新一次的位置补成连续的位置。
    /// Returns the playback position in seconds together with the instant it was sampled. Returns false when the source is
    /// not NetEase or nothing has been scanned yet. The instant matters: <see cref="TaskbarExperiencePolicy"/> extrapolates
    /// from it at 1x to turn a position that only updates once a second into a continuous one.
    /// </summary>
    public bool TryGetPosition(string? sourceId, out double seconds, out DateTimeOffset sampledAt)
    {
        if (!MediaSourceNameFormatter.IsNetEase(sourceId))
        {
            seconds = 0;
            sampledAt = default;
            return false;
        }

        Interlocked.Exchange(ref _lastRequestTicks, Environment.TickCount64);
        EnsureLoop();

        lock (_gate)
        {
            if (!_hasPosition)
            {
                seconds = 0;
                sampledAt = default;
                return false;
            }

            seconds = _position;
            sampledAt = _sampledAt;
            return true;
        }
    }

    /// <summary>
    /// 是否已经锁定位置地址。仅供自检读取：正常运行时这条通道是后台自己扫、自己跟，界面永远不需要等它。
    /// Whether position addresses are locked. Read by the self-check only: at runtime the channel scans and follows on its
    /// own and the UI never waits for it.
    /// </summary>
    internal bool IsLocked
    {
        get
        {
            lock (_gate)
                return _hasPosition;
        }
    }

    /// <summary>
    /// 位置是不是特征码通道给的。上层据此跳过"只对扫描通道成立"的健全性检查与看门狗：那两条判据是为扫描通道的
    /// 歧义设计的（位置与本地计时相差几分钟 = 锁到了全局计时器），而特征码读到的是真位置，中途开始播放时本来
    /// 就会与本地计时相差一大截，硬套过去反而会把正确的位置判成垃圾。
    /// Whether the position comes from the signature channel. Callers use it to skip the sanity check and watchdog that
    /// exist only for the scan channel's ambiguity (a position minutes away from the local clock means a locked global
    /// timer); the signature channel reads the real position, and starting playback mid-track legitimately puts it far
    /// from the local clock, so applying those tests would reject a correct value.
    /// </summary>
    internal bool IsSignatureChannel
    {
        get
        {
            lock (_gate)
                return _usingSignature;
        }
    }

    /// <summary>
    /// 特征码通道顺带给出的曲目信息：真实时长与播放状态。返回 false 表示本拍没有这些信息（走的是扫描通道，
    /// 或者特征码通道刚失效），调用方应当保持原来的行为。
    /// Track information the signature channel happens to provide: the real duration and the play status. False means this
    /// poll has neither (the scan channel is in use, or the signatures just failed) and the caller should behave as before.
    /// </summary>
    internal bool TryGetTrackInfo(out double durationSeconds, out bool isPlaying)
    {
        lock (_gate)
        {
            durationSeconds = _signatureDurationSeconds;
            isPlaying = _signatureIsPlaying;
            return _usingSignature;
        }
    }

    /// <summary>
    /// 请求作废当前锁定并重新扫描。供 <see cref="MemoryChannelWatchdog"/> 与 <see cref="MemoryLockSanityPolicy"/>
    /// 调用：地址还能读到值、值也落在秒数区间里，读失败判据抓不到，但锁定值的推进速度与本地计时长期不符，
    /// 或位置根本不像当前曲目的（换歌不归零、与本地计时相差几分钟——全局计时器的形状）。真正的作废在轮询循环里
    /// 执行：地址清单是它的局部状态，这里只留标志，避免跨线程去动循环手里的 <c>List</c>。
    /// Requests that the current lock be dropped and re-scanned. Called by MemoryChannelWatchdog and
    /// MemoryLockSanityPolicy: the addresses still read and the values still land in the seconds range, so the
    /// read-failure test cannot catch it, yet the locked value's advance rate has long disagreed with the local clock,
    /// or the value does not look like this track's position at all (never resets on a track change, minutes away from
    /// the clock — the shape of a global timer). The actual drop happens inside the poll loop: the address list is its
    /// local state, so all this leaves behind is a flag rather than touching another thread's List.
    /// </summary>
    /// <param name="reason">作废原因，只用于日志。/ The reason, used in the log line only.</param>
    /// <param name="sanityBackoff">
    /// true 表示"锁定能读但不可信"：等 <see cref="SanityRescanCooldownMilliseconds"/> 再扫，不吃换歌那条 1.5 秒短冷却
    /// ——立即重扫只会再锁一次垃圾（真机 13:58–14:02 每 10 几秒白扫一次 430MB）。false 用于推进速度类作废，行为同前。
    /// True means "the lock reads but cannot be trusted": wait SanityRescanCooldownMilliseconds before rescanning
    /// instead of the 1.5 s track-change cool-down, because rescanning at once only locks garbage again (the real
    /// machine burned a 430 MB scan every ten-odd seconds between 13:58 and 14:02). False is for advance-rate
    /// rejections and behaves as before.
    /// </param>
    internal void InvalidateChannel(string reason, bool sanityBackoff = false)
    {
        _invalidateReason = reason;
        _invalidateCooldownMilliseconds = sanityBackoff
            ? SanityRescanCooldownMilliseconds
            : RescanAfterLossMilliseconds;
        _invalidateRequested = true;
    }

    private void EnsureLoop()
    {
        lock (_gate)
        {
            if (_loop is not null)
                return;

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _loop = Task.Run(() => LoopAsync(token), token);
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        ProcessMemoryReader? reader = null;
        var addresses = new List<long>();
        var failures = 0;
        var nextScanAllowed = 0L;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (Environment.TickCount64 - Interlocked.Read(ref _lastRequestTicks) > IdleShutdownMilliseconds)
                {
                    // 很久没有网易云在放了：松开句柄，别一直持有别的进程。
                    reader?.Dispose();
                    reader = null;
                    addresses.Clear();
                    _signature.Reset();
                    _usingSignature = false;
                    ClearSignatureSongId();
                    SetPosition(0, default, false);
                    await Task.Delay(2000, token).ConfigureAwait(false);
                    continue;
                }

                // 看门狗请求作废（锁定值的推进速度与本地计时长期不符，或位置不像当前曲目）。地址清单是下面的
                // 局部状态，只能在这里清。健全性退避的作废等 30 秒再扫（立即重扫只会再锁一次垃圾）。
                // A watchdog invalidation request (the locked value's advance rate has long disagreed with the local
                // clock, or the value does not look like this track's position). The address list is local state below,
                // so this is the only place it can be cleared. Sanity rejections wait thirty seconds before rescanning
                // (rescanning at once only locks garbage again).
                if (_invalidateRequested)
                {
                    _invalidateRequested = false;
                    var reason = _invalidateReason;
                    var cooldown = _invalidateCooldownMilliseconds;
                    _invalidateReason = null;

                    if (addresses.Count > 0)
                    {
                        failures = 0;
                        Log.Info("Media", $"网易云内存通道已失效（{reason}），将重新扫描。");
                        addresses.Clear();
                        nextScanAllowed = Environment.TickCount64 + cooldown;
                    }
                }

                // 特征码通道优先：读的是播放器自己的字段，不存在"锁到全局计时器"这种歧义，还能给出真实时长。
                // 它读不到时（网易云没运行、升级后特征码失效、读失败累计）返回 false，下面照旧走内存扫描通道。
                // The signature channel comes first: it reads the player's own fields, so "locked a global timer" cannot
                // happen, and it reports the real duration. When it cannot read (NetEase not running, signatures
                // invalidated by an update, too many read failures) it returns false and the scan below proceeds as before.
                if (_signature.TryRead(out var signature))
                {
                    if (!_usingSignature)
                    {
                        _usingSignature = true;
                        Log.Info("Media", $"网易云位置改用特征码通道（{_signature.ModuleDescription}），不再扫描内存。");
                    }

                    lock (_gate)
                    {
                        _signatureDurationSeconds = signature.DurationSeconds;
                        _signatureIsPlaying = signature.IsPlaying;
                    }

                    SetPosition(signature.Position, signature.SampledAt, true);

                    // 可观测性（v1.4.9）：把播放器自己记的歌曲 id 写进日志。这是"到底是不是同一首歌"最硬的凭据，
                    // 下一版把它接进取词路径后就不用再赌队列文件里的歌手字符串；现在先让它可见。
                    // Observability (v1.4.9): put the song id the player itself keeps into the log. It is the hardest evidence
                    // for "is this really the same track"; the next version feeds it into retrieval, and for now it is simply
                    // visible. Polled at most once a second — an id change never needs to be seen faster than that.
                    var nowTicks = Environment.TickCount64;
                    if (nowTicks >= _nextSongIdPollTicks)
                    {
                        _nextSongIdPollTicks = nowTicks + SongIdPollIntervalMilliseconds;
                        if (_signature.TryGetSongId(out var songId))
                        {
                            ReportSignatureSongId(songId);
                        }
                    }

                    // 特征码能用就别白养着扫描通道的句柄和地址清单：多读一个进程的内存没有好处。
                    // While the signatures work there is no reason to keep the scan channel's handle and addresses alive.
                    if (addresses.Count > 0)
                    {
                        Log.Info("Media", $"网易云内存扫描通道已停用（特征码通道可用），释放 {addresses.Count} 个地址。");
                        addresses.Clear();
                        failures = 0;
                    }

                    if (reader is not null)
                    {
                        reader.Dispose();
                        reader = null;
                    }

                    await Task.Delay(SignaturePollIntervalMilliseconds, token).ConfigureAwait(false);
                    continue;
                }

                if (_usingSignature)
                {
                    _usingSignature = false;
                    // 通道交还给扫描通道时把 id 记忆清掉：下次特征码重新附加、同一首歌也要再记一行，
                    // 免得"通道断过一截"这件事在日志里看不出来。
                    // The id memo is dropped when the channel hands over to the scan channel, so a re-attach logs the id again
                    // and a gap in the channel stays visible in the log.
                    ClearSignatureSongId();
                    Log.Info("Media", "网易云特征码通道读不到位置，退回内存扫描通道。");
                }

                if (addresses.Count == 0)
                {
                    if (Environment.TickCount64 < nextScanAllowed)
                    {
                        await Task.Delay(500, token).ConfigureAwait(false);
                        continue;
                    }

                    reader?.Dispose();
                    reader = null;

                    var scanned = await Task.Run(() => ScanAsync(token), token).ConfigureAwait(false);
                    if (scanned is not { } result)
                    {
                        nextScanAllowed = Environment.TickCount64 + RescanCooldownMilliseconds;
                        await Task.Delay(500, token).ConfigureAwait(false);
                        continue;
                    }

                    reader = result.Reader;
                    addresses.AddRange(result.Addresses);
                    failures = 0;
                    Log.Info("Media", $"网易云内存通道已锁定 {addresses.Count} 个播放位置地址。");
                    continue;
                }

                var values = ReadValues(reader!, addresses);
                var position = SelectPosition(values);
                if (position is null)
                {
                    failures++;
                    if (failures >= MaximumPollFailures)
                    {
                        Log.Info("Media", "网易云内存位置已失效，将重新扫描。");
                        addresses.Clear();
                        nextScanAllowed = Environment.TickCount64 + RescanAfterLossMilliseconds;
                    }
                }
                else
                {
                    failures = 0;
                    SetPosition(position.Value, DateTimeOffset.UtcNow, true);
                }

                await Task.Delay(PollIntervalMilliseconds, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出路径。
        }
        catch (Exception ex)
        {
            Log.Warn("Media", $"网易云内存通道异常退出: {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            reader?.Dispose();
            lock (_gate)
            {
                _loop = null;
                _cancellation?.Dispose();
                _cancellation = null;
            }
        }
    }

    private void SetPosition(double seconds, DateTimeOffset sampledAt, bool hasPosition)
    {
        lock (_gate)
        {
            _position = seconds;
            _sampledAt = sampledAt;
            _hasPosition = hasPosition;
        }
    }

    /// <summary>
    /// 歌曲 id 换了一首才记一行日志；同一首重复上报不写。
    /// Logs one line only when the song id changes to a different track; repeats of the same id write nothing.
    /// </summary>
    private void ReportSignatureSongId(string songId)
    {
        lock (_gate)
        {
            if (string.Equals(songId, _signatureSongId, StringComparison.Ordinal))
            {
                return;
            }

            _signatureSongId = songId;
        }

        Log.Info("Media", $"网易云特征码通道读到歌曲 id: {songId}（播放器自己记的 id）。");
    }

    /// <summary>忘掉已记录的歌曲 id，下一次读到同一首也会重新记一行。/ Forgets the logged song id so the next read logs again.</summary>
    private void ClearSignatureSongId()
    {
        lock (_gate)
        {
            _signatureSongId = string.Empty;
        }
    }

    /// <summary>
    /// 依次尝试几个网易云进程，返回第一个扫描成功的"读取器 + 地址清单"。调用方负责释放读取器。
    /// Tries the NetEase processes in turn and returns the first reader plus address list that scans successfully; the caller
    /// owns the reader.
    /// </summary>
    private (ProcessMemoryReader Reader, List<long> Addresses)? ScanAsync(CancellationToken token)
    {
        foreach (var process in FindCandidateProcesses())
        {
            token.ThrowIfCancellationRequested();

            using (process)
            {
                var reader = ProcessMemoryReader.TryOpen(process.Id);
                if (reader is null)
                    continue;

                try
                {
                    var startedTicks = Environment.TickCount64;
                    var first = CollectCandidates(reader, token);
                    if (first.Count == 0)
                    {
                        reader.Dispose();
                        continue;
                    }

                    if (token.WaitHandle.WaitOne(ScanGapMilliseconds))
                    {
                        reader.Dispose();
                        return null;
                    }

                    // 两遍扫描之间的真实间隔 = 第一遍开始到第二遍开始。绝不能写成"第一遍耗时"，更不能拿它减去等待时间：
                    // 那样算出来的间隔会偏小甚至为负，判据就退化成"值几乎没变"，churn 中的内存几乎全都能通过。
                    // The real gap between the two passes runs from the start of the first to the start of the second. It must not
                    // be the duration of the first pass, and least of all that minus the wait: the gap then comes out too small or
                    // even negative, the test degrades into "the value barely changed", and almost all churning memory passes.
                    var elapsed = (Environment.TickCount64 - startedTicks) / 1000d;
                    var advancing = SelectAdvancing(reader, first, elapsed, token);
                    var clusters = SelectClusters(advancing);

                    // 第三遍只复核最大的那几簇（每簇十几个地址，代价可以忽略）：能紧接着再按 1 倍速前进一次，才当它是播放位置。
                    // 先成簇再复核，是因为"位置"在内存里有好几份副本，而巧合前进的地址零散；复核则把"恰好改了差不多多少"的残余全部挡掉。
                    // The third pass re-checks only the few largest clusters (a dozen addresses each, a negligible cost): a value
                    // has to advance at 1x once more, measured back to back, to be believed. Clustering comes first because the
                    // position exists as several copies while coincidences are scattered, and the re-check then rejects every
                    // remaining "changed by roughly the right amount" coincidence.
                    foreach (var cluster in clusters)
                    {
                        var confirmTicks = Environment.TickCount64;
                        if (token.WaitHandle.WaitOne(ScanGapMilliseconds))
                        {
                            reader.Dispose();
                            return null;
                        }

                        var confirmElapsed = (Environment.TickCount64 - confirmTicks) / 1000d;
                        var confirmed = new List<long>(cluster.Count);
                        foreach (var (address, value) in cluster)
                        {
                            if (!TryReadDouble(reader, address, out var now))
                                continue;

                            var delta = now - value;
                            if (delta > 0 && Math.Abs(delta - confirmElapsed) <= ConfirmAdvanceToleranceSeconds)
                                confirmed.Add(address);
                        }

                        if (confirmed.Count >= MinimumClusterSize)
                        {
                            Log.Info(
                                "Media",
                                $"网易云内存位置已找到：候选 {first.Count} 个，粗筛通过 {advancing.Count} 个，成簇 {cluster.Count} 个，复核通过 {confirmed.Count} 个（间隔 {elapsed:0.0} 秒）。");
                            return (reader, confirmed);
                        }
                    }

                    Log.Info(
                        "Media",
                        $"网易云内存位置扫描未通过：候选 {first.Count} 个，粗筛通过 {advancing.Count} 个，最大簇 {clusters.FirstOrDefault()?.Count ?? 0} 个（间隔 {elapsed:0.0} 秒）。");
                }
                catch (OperationCanceledException)
                {
                    reader.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Warn("Media", $"扫描网易云内存失败: {ex.GetType().Name} {ex.Message}");
                }

                reader.Dispose();
            }
        }

        return null;
    }

    private static List<Process> FindCandidateProcesses()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(PlayerProcessName);
        }
        catch (Exception ex)
        {
            Log.Warn("Media", $"枚举网易云进程失败: {ex.GetType().Name}");
            return [];
        }

        // 播放位置在 CEF 外壳进程里，它是私有内存最大的那个；小的几个是辅助进程，扫了也是白扫。
        return processes
            .OrderByDescending(GetPrivateBytes)
            .Take(MaximumProcessCandidates)
            .ToList();
    }

    private static long GetPrivateBytes(Process process)
    {
        try
        {
            return process.PrivateMemorySize64;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// 这个 double 像不像一个播放位置。必须显式挡掉 NaN：NaN 与任何数比较都是 false，所以"小于下限"和"大于上限"
    /// 两个判断都拦不住它，而后面"前进了多少"的判断同样拦不住——NaN 会一路通过，让进程里每一段未初始化或已释放的
    /// 堆内存都变成候选（实测上百万个），真正的播放位置就淹没在里面了。
    /// Whether a double could be a playback position. NaN has to be rejected explicitly: every comparison with NaN is false,
    /// so neither the lower bound nor the upper bound catches it, and the later "how far did it advance" test does not catch it
    /// either — NaN sails through and turns every stretch of uninitialised or freed heap into a candidate (measured: over a
    /// million of them), drowning the real playback position.
    /// </summary>
    private static bool IsPlausiblePosition(double value)
        => double.IsFinite(value) && value >= MinimumPositionSeconds && value <= MaximumPositionSeconds;

    /// <summary>第一遍：把所有"看起来像秒数"的 double 连同地址一起收下来。/ First pass: every double that looks like a second count, with its address.</summary>
    private static Dictionary<long, double> CollectCandidates(ProcessMemoryReader reader, CancellationToken token)
    {
        var candidates = new Dictionary<long, double>();
        var buffer = new byte[ScanBufferBytes];

        foreach (var region in reader.EnumerateScannableRegions(MaximumRegionBytes))
        {
            token.ThrowIfCancellationRequested();

            var baseAddress = region.BaseAddress.ToInt64();
            var size = region.RegionSize.ToInt64();

            for (long offset = 0; offset + 8 <= size; offset += ScanBufferBytes)
            {
                var want = (int)Math.Min(ScanBufferBytes, size - offset);
                if (reader.Read((IntPtr)(baseAddress + offset), buffer, want) != want)
                    continue;

                var span = MemoryMarshal.Cast<byte, double>(buffer.AsSpan(0, want));
                for (var i = 0; i < span.Length; i++)
                {
                    var value = span[i];
                    if (!IsPlausiblePosition(value))
                        continue;

                    candidates[baseAddress + offset + (i * 8L)] = value;
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// 第二遍：只留下"与两遍之间的真实间隔相符地前进了"的地址。判据是"前进了约 1 倍速"，而不是"前进了任意值"，
    /// 所以随机数据中恰好落在秒数区间里的 double 会被筛掉。
    /// Second pass: keeps only the addresses that advanced by as much as the real gap between the two passes. The test is
    /// "advanced at about 1x", not "advanced by anything", which is what filters out the random doubles that merely happen to
    /// fall inside the seconds range.
    /// </summary>
    private static List<(long Address, double Value)> SelectAdvancing(
        ProcessMemoryReader reader,
        Dictionary<long, double> previous,
        double elapsedSeconds,
        CancellationToken token)
    {
        var advancing = new List<(long, double)>();
        var buffer = new byte[ScanBufferBytes];

        foreach (var region in reader.EnumerateScannableRegions(MaximumRegionBytes))
        {
            token.ThrowIfCancellationRequested();

            var baseAddress = region.BaseAddress.ToInt64();
            var size = region.RegionSize.ToInt64();

            for (long offset = 0; offset + 8 <= size; offset += ScanBufferBytes)
            {
                var want = (int)Math.Min(ScanBufferBytes, size - offset);
                if (reader.Read((IntPtr)(baseAddress + offset), buffer, want) != want)
                    continue;

                var span = MemoryMarshal.Cast<byte, double>(buffer.AsSpan(0, want));
                for (var i = 0; i < span.Length; i++)
                {
                    var value = span[i];
                    if (!IsPlausiblePosition(value))
                        continue;

                    var address = baseAddress + offset + (i * 8L);
                    if (!previous.TryGetValue(address, out var before))
                        continue;

                    var delta = value - before;
                    if (delta <= 0 || Math.Abs(delta - elapsedSeconds) > ScanAdvanceToleranceSeconds)
                        continue;

                    advancing.Add((address, value));
                }
            }
        }

        return advancing;
    }

    /// <summary>
    /// 把数值互相接近的候选聚成簇，按成员数从多到少返回前几簇。同一份播放位置在内存里通常有好几个副本
    /// （音频线程、界面线程各一份，彼此差不到一秒），而巧合前进的地址散落各处，所以成员最多的那簇最可能是位置；
    /// 但"最多"只是猜测，真正说了算的是后面的单点复核，于是这里多给几簇让复核去挑。
    /// Groups candidates whose values are close to each other and returns the biggest few groups, largest first. One position
    /// normally has several copies in memory (one per thread, within a second of each other) while coincidences are scattered,
    /// so the biggest group is the best guess; but "biggest" is only a guess and the point-by-point re-check has the final say,
    /// which is why a few groups are handed over rather than just one.
    /// </summary>
    private static List<List<(long Address, double Value)>> SelectClusters(List<(long Address, double Value)> advancing)
    {
        if (advancing.Count == 0)
            return [];

        var sorted = advancing.OrderBy(pair => pair.Value).ToList();
        var clusters = new List<List<(long, double)>>();
        var current = new List<(long, double)> { sorted[0] };

        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i].Value - sorted[i - 1].Value <= ClusterToleranceSeconds)
            {
                current.Add(sorted[i]);
                continue;
            }

            clusters.Add(current);
            current = [sorted[i]];
        }

        clusters.Add(current);

        return clusters
            .OrderByDescending(cluster => cluster.Count)
            .Take(MaximumClustersToConfirm)
            .ToList();
    }

    private static List<double> ReadValues(ProcessMemoryReader reader, List<long> addresses)
    {
        var values = new List<double>(addresses.Count);
        foreach (var address in addresses)
        {
            if (TryReadDouble(reader, address, out var value) && IsPlausiblePosition(value))
                values.Add(value);
        }

        return values;
    }

    /// <summary>
    /// 从这一轮的副本里定出一个位置：先取中位数，丢掉离群太远的（切歌、重建瞬间的残值），再在其中取**最大**的那个——
    /// 各个副本都滞后于真实播放位置，最靠前的那个才最接近此刻。
    /// Picks one position out of this round's copies: take the median, drop anything far from it (leftovers from a track change
    /// or a rebuild), then take the **largest** survivor, because every copy lags the true position and the leading one is
    /// closest to now.
    /// </summary>
    private static double? SelectPosition(List<double> values)
    {
        if (values.Count == 0)
            return null;

        var sorted = values.OrderBy(value => value).ToList();
        var median = sorted[sorted.Count / 2];
        double? best = null;

        foreach (var value in values)
        {
            if (Math.Abs(value - median) > ClusterToleranceSeconds)
                continue;

            if (best is null || value > best)
                best = value;
        }

        return best;
    }

    private static bool TryReadDouble(ProcessMemoryReader reader, long address, out double value)
    {
        value = 0;
        var buffer = new byte[sizeof(double)];
        if (reader.Read((IntPtr)address, buffer) != buffer.Length)
            return false;

        value = BitConverter.ToDouble(buffer, 0);
        return true;
    }

    public void Dispose()
    {
        _signature.Dispose();

        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _cancellation;
            _cancellation = null;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 循环已经自己收尾了。
        }
    }
}
