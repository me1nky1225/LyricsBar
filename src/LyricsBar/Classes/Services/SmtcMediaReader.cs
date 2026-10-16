using System.Diagnostics;
using System.Runtime.InteropServices;
using LyricsBar.Classes.Models;
using LyricsBar.Classes.Utils;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace LyricsBar.Classes.Services;

/// <summary>
/// 读取 Windows 系统媒体会话（SMTC），产出当前播放曲目。这是歌词的输入源：没有曲名与歌手，取词无从谈起。
/// Reads the Windows system media session (SMTC) and produces the currently playing track. This is the lyric pipeline's input:
/// without a title and artist there is nothing to look up.
///
/// 与原项目的差异：原项目的 MediaSessionService 有一千余行，同时管理 SMTC 通道、网易云/酷狗的内存通道、封面解码、
/// 双通道仲裁与播放控制命令。精简版只保留"读一条曲目信息"这一件事，不提供任何控制能力。
/// 唯一例外是 <c>CanSkipNext</c> / <c>CanSkipPrevious</c> 两个只读标志：它们来自播放器自己的声明，
/// 用于给歌词栏上的切歌按钮置灰；真正执行切歌的是 <see cref="MediaTransportService"/>。
/// Difference from the original: the original's MediaSessionService runs over a thousand lines and manages the SMTC channel,
/// in-memory channels for NetEase and Kugou, artwork decoding, two-channel arbitration, and transport commands. The trimmed
/// build keeps only "read one track's information" and issues no transport commands. The one exception is the read-only
/// <c>CanSkipNext</c> / <c>CanSkipPrevious</c> flags: they come from the player's own declaration and only decide whether the
/// bar's skip buttons are greyed out; <see cref="MediaTransportService"/> is what actually skips.
/// </summary>
public sealed class SmtcMediaReader : IDisposable
{
    /// <summary>轮询间隔。任务栏歌词只需要跟上换行，250 毫秒足够，比事件驱动的复杂度低得多。</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>位置推进超过这个秒数才发布一次新快照，避免把 250 毫秒的抖动当成变化。</summary>
    private const double PositionChangeEpsilon = 0.2;

    /// <summary>连续多少次轮询位置都没推进，就认为播放器报的位置不可信，改走本地计时（约 2 秒）。</summary>
    private const int StalledPollThreshold = 8;

    /// <summary>封面最多尝试读取的次数，应对"缩略图比元数据晚一拍"的播放器。</summary>
    private const int MaxCoverAttempts = 4;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private string _lastSessionId = string.Empty;
    private string _lastTitle = string.Empty;
    private string _lastArtist = string.Empty;
    private CancellationTokenSource? _loopCancellation;

    /// <summary>
    /// 本地计时：既在"播放器完全不上报时间轴"时顶替播放位置，也在仲裁时充当第二个参照
    /// （见 <see cref="PositionArbitrationPolicy"/>）—— 播放器报了个塌回开头的假值时，靠它顶住。
    /// 实测（2026-10-15/16）网易云音乐的 SMTC 时间轴**并不是恒零**，而是会周期性塌回 0–1.5 秒：
    /// 同一时刻内存通道读到 288.2 秒，上报值却在 0.4 / 3.4 / 49.2 / 33.5 之间乱跳。
    /// 计时器换歌归零、播放中累加、暂停时冻结；它的基准点由 <see cref="_localClockBaselineSeconds"/> 挂在最近采信过的真实位置上。
    /// A local clock that both stands in when the player reports no timeline at all and serves as the second reference during
    /// arbitration (see PositionArbitrationPolicy): when the player reports a collapse-to-the-start value, this is what holds the
    /// line. Measured 2026-10-15/16: NetEase's SMTC timeline is **not** permanently zero — it periodically collapses back to
    /// 0–1.5 s, while at the same instant the memory channel read 288.2 s and the reported value bounced between
    /// 0.4 / 3.4 / 49.2 / 33.5. The clock zeroes on a track change, accumulates while playing, freezes while paused, and its
    /// baseline is pinned to the last trusted real position through _localClockBaselineSeconds.
    /// </summary>
    private readonly Stopwatch _fallbackClock = new();
    private string _fallbackTrackKey = string.Empty;

    /// <summary>
    /// 本地计时的基准秒数：最近一次采信的真实位置减去当时的计时读数。没有它，"程序启动时歌已经在播"和"用户拖过进度条"
    /// 这两种场景下的本地计时会与真实位置相差一大截，一旦被仲裁用来顶替就会把歌词拉回第一行。
    /// The local clock's baseline in seconds: the last trusted real position minus the clock reading at that moment. Without it,
    /// the clock drifts far from the real position whenever the app started mid-song or the user dragged the seek bar, and the
    /// moment arbitration falls back to it the lyrics would be yanked back to the first line.
    /// </summary>
    private double _localClockBaselineSeconds;

    /// <summary>位置仲裁：上一拍的值、塌回开头的候选等状态都在它里面；本类只负责把三条通道读出来交给它。</summary>
    private readonly PositionArbitrationPolicy _positionArbitration = new();

    /// <summary>
    /// 内存通道看门狗：锁定之后位置必须与本地计时同步推进，持续不符就作废重扫（见 <see cref="MemoryChannelWatchdog"/>）。
    /// 真机日志里"通道已失效"一次都没打过，而锁定的地址数在 3–113 之间乱跳——这说明现有的失效判据
    /// （连续 4 拍读不到值）漏掉了"锁到了错误的地址"这一整类失败。
    /// The memory channel's watchdog: once locked, the position has to advance in step with the local clock, and a
    /// persistent disagreement means a rescan (see MemoryChannelWatchdog). "Channel invalidated" never fired in the
    /// real-hardware logs while the locked address count bounced between 3 and 113, which means the existing failure
    /// test (four polls with no value) misses the whole class of "locked onto the wrong addresses".
    /// </summary>
    private readonly MemoryChannelWatchdog _memoryWatchdog = new();

    /// <summary>
    /// 本地计时顶替上限：连续顶替满 30 秒就冻结位置，等真实通道恢复（见 <see cref="LocalClockStandInGuard"/>）。
    /// The cap on the local clock's stand-in: after thirty continuous seconds the position freezes until a real channel
    /// returns (see LocalClockStandInGuard).
    /// </summary>
    private readonly LocalClockStandInGuard _standInGuard = new();

    /// <summary>
    /// 内存锁定的健全性检查（见 <see cref="MemoryLockSanityPolicy"/>）：v1.4.6 真机日志证明网易云更新后扫描会锁到
    /// 一个 12:39 起算的全局计时器（数千份副本、1 倍速、换歌不归零），于是播放位置变成 4800 秒的荒谬值、
    /// 歌词越过所有行尾停在最后一行——用户报的"匹配不准、不会动了"。它在仲裁之前把这种锁定挡掉。
    /// The sanity check on the memory lock (see MemoryLockSanityPolicy): the v1.4.6 real-hardware log proved that after
    /// a NetEase update the scan locks onto a global timer started at 12:39 (thousands of copies, 1x, never resets on
    /// a track change), so the playback position became an absurd 4800 seconds and the lyrics froze past every line's
    /// end — the user's "matching is off and it stopped moving". It blocks such locks before the arbiter sees them.
    /// </summary>
    private readonly MemoryLockSanityPolicy _memorySanity = new();

    /// <summary>上一拍 <see cref="ResolvePosition"/> 的时刻（UTC ticks），两个看门狗靠它算"过了多少秒"。</summary>
    private long _lastResolveTicks = DateTimeOffset.UtcNow.Ticks;

    /// <summary>本地计时是否已被冻结。冻结期间每拍返回同一个钉住的位置（基准给当前时刻，外推加零）。</summary>
    private bool _localClockFrozen;

    /// <summary>冻结时钉住的位置（秒）：最近一次从真实通道采信的值，没有过就退回当时的计时读数。</summary>
    private double _frozenPosition;

    /// <summary>最近一次从真实通道（上报值 / 内存）采信的位置。冻结时回到这里，让偏差不随秒表继续增长。</summary>
    private double _lastRealPosition = double.NaN;

    /// <summary>上一拍最终采用的位置来源，只用于"来源切换时打一条日志"。</summary>
    private PositionSource _lastPositionSource = PositionSource.None;

    /// <summary>
    /// 本地计时的附加偏移（秒）。只对"播放器不上报时间轴"的场景有意义：用户在音乐软件里拖动进度条时，
    /// 系统接口看不到这个动作（网易云音乐的时间轴恒为零），本地计时无从知晓，只能由用户手动把歌词时间推到当前进度。
    /// 正数表示歌词时间比本地计时快，负数表示慢。
    /// Extra offset in seconds applied to the local clock. It only matters when the player reports no timeline: when the user
    /// drags the seek bar in the music app the system interfaces cannot see it (NetEase reports an all-zero timeline), so the
    /// local clock has no way to know and the user has to nudge the lyric time by hand. Positive means the lyric time runs
    /// ahead of the local clock, negative means behind.
    /// </summary>
    private double _localClockOffsetSeconds;
    private double _lastReportedPosition = double.NaN;
    private int _stalledPolls;

    /// <summary>
    /// 网易云的进程内存位置通道（见 <see cref="NeteasePositionService"/>）。装上它之后，"播放器不上报时间轴"
    /// 就不再只剩本地计时一条路：位置直接从网易云自己的内存里读，用户拖进度条才会跟着走。
    /// NetEase's in-process memory position channel (see <see cref="NeteasePositionService"/>). Once installed, a player that
    /// reports no timeline is no longer limited to the local clock: the position is read out of NetEase's own memory, which is
    /// what makes seeking follow.
    /// </summary>
    internal NeteasePositionService? NeteasePosition { get; set; }

    /// <summary>当前是否正在采用内存位置。仅用于把"切换"这件事记进日志，不参与任何计算。</summary>
    private bool _usingMemoryPosition;

    /// <summary>
    /// 上一拍的内存值是不是特征码通道给的（见 <see cref="NeteasePositionService.IsSignatureChannel"/>）。通道切换时
    /// 用它判断该清掉两条看门狗的状态：特征码时期的位置不适用"必须贴近本地计时"，带着旧账回到扫描通道会误判。
    /// Whether the previous poll's memory value came from the signature channel. Used to detect the channel switch and clear
    /// both watchdogs: a signature-channel position is not bound to sit near the local clock, so carrying that history back
    /// into the scan channel would misjudge it.
    /// </summary>
    private bool _lastFromSignature;

    /// <summary>按曲目缓存的封面 data URI，以及它的重试计数。见 <see cref="ResolveCoverArtAsync"/>。</summary>
    private string _coverTrackKey = string.Empty;
    private string _coverDataUri = string.Empty;
    private int _coverAttempts;

    /// <summary>
    /// 上一首最终采用的封面。用来识别"播放器还没把缩略图换过来"：换歌后头几次读到的往往仍是上一首的图，
    /// 与这份字节完全相同就说明还没换，不能采用。见 <see cref="ResolveCoverArtAsync"/>。
    /// The artwork finally adopted for the previous track. It detects "the player has not swapped the thumbnail yet": the first
    /// reads after a track change usually still return the previous track's image, and identical bytes mean it has not been
    /// swapped. See <see cref="ResolveCoverArtAsync"/>.
    /// </summary>
    private string _previousCoverDataUri = string.Empty;

    /// <summary>曲目或播放状态发生变化时触发，携带最新快照；由订阅者切回 UI 线程。</summary>
    public event Action<MediaSnapshot>? SnapshotChanged;

    /// <summary>读取器停止或发生不可恢复的错误时触发。</summary>
    public event Action? Stopped;

    /// <summary>
    /// 请求媒体会话管理器。没有正在播放的媒体时 <c>RequestAsync</c> 本身不会失败，只是后续拿不到会话。
    /// Requests the media session manager. <c>RequestAsync</c> does not fail merely because nothing is playing; the sessions
    /// simply come back empty afterwards.
    /// </summary>
    public async Task<bool> InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            return _manager is not null;
        }
        catch (Exception ex)
        {
            Log.Error("Media", "无法获取系统媒体会话管理器。", ex);
            return false;
        }
    }

    /// <summary>开始后台轮询。重复调用只生效一次。/ Starts background polling; repeated calls take effect once.</summary>
    public void Start()
    {
        if (_loopCancellation is not null)
            return;

        _loopCancellation = new CancellationTokenSource();
        _ = PollLoopAsync(_loopCancellation.Token);
    }

    /// <summary>停止轮询并清空已发布的曲目。/ Stops polling and clears the published track.</summary>
    public void Stop()
    {
        var cancellation = Interlocked.Exchange(ref _loopCancellation, null);
        if (cancellation is null)
            return;

        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task PollLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var snapshot = await ReadAsync().ConfigureAwait(false);
                if (token.IsCancellationRequested)
                    return;

                Publish(snapshot);
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止路径。
        }
        catch (Exception ex)
        {
            Log.Error("Media", "媒体轮询异常终止。", ex);
            Stopped?.Invoke();
        }
    }

    private void Publish(MediaSnapshot snapshot)
    {
        // 只在"身份、播放状态或播放位置"真的变了的时候通知，避免每 250 毫秒都惊动一次 UI。
        // 位置必须计入：本地计时兜底与时间轴外推都以上一次发布的快照为基准，若位置推进不发通知，
        // 界面拿到的永远是启动那一瞬的位置，歌词会停在第一行不动。
        // Only notify when identity, playback state, or position actually changed, so the UI is not woken every 250 ms.
        // Position has to count: both the fallback clock and timeline extrapolation are based on the last published
        // snapshot, so without it the UI would keep seeing the position from startup and the lyrics would freeze.
        var positionMoved = double.IsNaN(_lastPosition) ||
                            Math.Abs(snapshot.Position - _lastPosition) >= PositionChangeEpsilon;
        var changed =
            !string.Equals(_lastTitle, snapshot.Title, StringComparison.Ordinal) ||
            !string.Equals(_lastArtist, snapshot.Artist, StringComparison.Ordinal) ||
            !string.Equals(_lastSessionId, snapshot.SourceId, StringComparison.Ordinal) ||
            _lastIsConnected != snapshot.IsConnected ||
            _lastIsPlaying != snapshot.IsPlaying ||
            positionMoved ||
            // 封面通常比元数据晚到：暂停时位置不推进，若不计入封面变化，这张封面就永远送不到界面。
            // Artwork usually arrives after the metadata: while paused the position does not advance, so without counting
            // artwork changes this cover would never reach the UI.
            !string.Equals(_lastCoverArt, snapshot.CoverArt, StringComparison.Ordinal) ||
            _lastCanSkipNext != snapshot.CanSkipNext ||
            _lastCanSkipPrevious != snapshot.CanSkipPrevious ||
            _lastCanPlayPause != snapshot.CanPlayPause;

        _lastTitle = snapshot.Title;
        _lastArtist = snapshot.Artist;
        _lastSessionId = snapshot.SourceId;
        _lastIsConnected = snapshot.IsConnected;
        _lastIsPlaying = snapshot.IsPlaying;
        _lastPosition = snapshot.Position;
        _lastCoverArt = snapshot.CoverArt;
        _lastCanSkipNext = snapshot.CanSkipNext;
        _lastCanSkipPrevious = snapshot.CanSkipPrevious;
        _lastCanPlayPause = snapshot.CanPlayPause;

        if (changed)
            SnapshotChanged?.Invoke(snapshot);
    }

    private bool _lastIsConnected;
    private bool _lastIsPlaying;
    private double _lastPosition = double.NaN;
    private string _lastCoverArt = string.Empty;
    private bool _lastCanSkipNext;
    private bool _lastCanSkipPrevious;
    private bool _lastCanPlayPause;

    /// <summary>
    /// 读一次当前曲目。没有任何会话或读取失败时返回断开快照而不是抛异常——媒体状态随时会消失，
    /// 把"没有媒体"当成正常状态处理，界面才不会被异常打断。
    /// Reads the current track once. Returns a disconnected snapshot instead of throwing when there is no session or the read
    /// fails: media state disappears at any moment, so "no media" is treated as a normal state rather than an interruption.
    /// </summary>
    public async Task<MediaSnapshot> ReadAsync()
    {
        if (_manager is null)
            return MediaSnapshot.Disconnected;

        try
        {
            var session = SelectSession(_manager);
            if (session is null)
                return MediaSnapshot.Disconnected;

            // WinRT 的 IAsyncOperation 没有 ConfigureAwait，直接 await 即可：轮询循环本身不在 UI 线程上启动。
            var properties = await session.TryGetMediaPropertiesAsync();
            if (properties is null)
                return MediaSnapshot.Disconnected;

            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            var isPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var sourceId = session.SourceAppUserModelId ?? string.Empty;
            var title = properties.Title ?? string.Empty;
            var artist = properties.Artist ?? string.Empty;

            var start = timeline.StartTime.TotalSeconds;
            var reportedDuration = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds);
            var reportedPosition = Math.Clamp(
                timeline.Position.TotalSeconds - start,
                0,
                reportedDuration > 0 ? reportedDuration : double.MaxValue);

            var (position, duration, timelineUpdatedAt) = ResolvePosition(
                sourceId,
                title,
                artist,
                isPlaying,
                reportedPosition,
                reportedDuration,
                timeline.LastUpdatedTime);

            var coverArt = await ResolveCoverArtAsync(
                BuildTrackKey(sourceId, title, artist),
                properties.Thumbnail);

            // 播放器自己声明能不能切歌。有些来源（网页播放器、部分本地播放器）没有"下一首"的概念，
            // 按钮据此置灰，而不是让用户点了没反应却不知道为什么。
            // The player itself declares whether skipping is possible. Some sources (web players, certain local players) have no
            // notion of "next track"; the buttons grey out accordingly instead of leaving a click with no visible reason.
            var controls = playback.Controls;

            // 播放/暂停是最通用的传输控制，几乎每个播放器都支持；但网页播放器这类来源可能没有，
            // 所以同样按播放器自己的声明决定按钮是否可用。
            // Play/pause is the most common transport control and nearly every player supports it; sources such as web players
            // may still not, so the button's availability follows the player's own declaration too.
            var canPlayPause = controls?.IsPlayPauseToggleEnabled == true;

            return new MediaSnapshot(
                true,
                isPlaying,
                title,
                artist,
                sourceId,
                MediaSourceNameFormatter.GetDisplayName(sourceId),
                position,
                duration,
                playback.PlaybackRate is > 0 ? playback.PlaybackRate.Value : 1,
                timelineUpdatedAt,
                null,
                coverArt,
                LyricsLookupCompleted: false,
                CanSkipNext: controls?.IsNextEnabled == true,
                CanSkipPrevious: controls?.IsPreviousEnabled == true,
                CanPlayPause: canPlayPause);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // 播放器退出、会话被回收都会走到这里，属于正常现象，只记一条警告。
            Log.Warn("Media", $"读取媒体会话失败: {ex.GetType().Name}");
            return MediaSnapshot.Disconnected;
        }
    }

    /// <summary>曲目身份：来源 + 曲名 + 歌手。与协调器、呈现层构造 TrackId 的口径保持一致。</summary>
    private static string BuildTrackKey(string sourceId, string title, string artist) => string.Join(
        "\u001f",
        sourceId.Trim().ToUpperInvariant(),
        title.Trim().ToUpperInvariant(),
        artist.Trim().ToUpperInvariant());

    /// <summary>
    /// 取当前曲目的封面 data URI，按曲目缓存一份。
    /// 封面读一次要几十毫秒（I/O + 缩放 + JPEG 编码），而轮询是每 250 毫秒一次，因此只在换歌时读。
    /// 有些播放器会晚一拍才发布缩略图，所以空结果允许再试几次，而不是一次不成便放弃整首歌的封面。
    ///
    /// 换歌后还必须排除"读到的是上一首的图"：播放器普遍是先把曲名换掉、缩略图过一拍才换，
    /// 于是新曲目头几次读到的其实还是旧图。若直接采用，这张错封面会被按新曲目缓存住，整首歌都对不上——
    /// 这正是"上下切歌时封面对不上"的原因。判据取"与上一首最终采用的封面字节完全相同"：
    /// 不同曲目恰好共用同一张图的概率极低，而同一张专辑的连续曲目本来就会命中这个判据，
    /// 所以重试到上限后仍相同就接受，避免整张专辑都没有封面。
    /// Resolves the current track's artwork data URI, cached one per track. Reading it costs tens of milliseconds
    /// (I/O, downscale, JPEG encode) against a 250 ms poll, so it is only read on a track change. Some players publish the
    /// thumbnail a beat late, so an empty result is retried a few times rather than giving up on that track's artwork.
    ///
    /// After a track change the previous track's image has to be ruled out as well: players commonly swap the title first and
    /// the thumbnail a beat later, so the first reads for a new track still return the old image. Adopting it would cache the
    /// wrong artwork under the new track and keep it wrong for the whole song — which is exactly why covers looked mismatched
    /// when skipping between tracks. The test is "byte-identical to the artwork finally adopted for the previous track":
    /// two different tracks sharing one image is vanishingly unlikely, while consecutive tracks from the same album do hit that
    /// test, so an identical result is accepted once the retry budget runs out rather than leaving a whole album coverless.
    /// </summary>
    private async Task<string> ResolveCoverArtAsync(string trackKey, IRandomAccessStreamReference? thumbnail)
    {
        if (!string.Equals(trackKey, _coverTrackKey, StringComparison.Ordinal))
        {
            _coverTrackKey = trackKey;
            _coverDataUri = string.Empty;
            _coverAttempts = 0;
        }

        if (_coverDataUri.Length > 0 || _coverAttempts >= MaxCoverAttempts || thumbnail is null)
            return _coverDataUri;

        _coverAttempts++;
        var dataUri = await CoverArtLoader.LoadDataUriAsync(thumbnail);
        if (dataUri.Length == 0)
            return _coverDataUri;

        var sameAsPreviousTrack = string.Equals(dataUri, _previousCoverDataUri, StringComparison.Ordinal);
        if (sameAsPreviousTrack && _coverAttempts < MaxCoverAttempts)
        {
            // 大概率还是上一首的图，等下一次轮询再读；这一次不计入"取到了"。
            // Most likely still the previous track's image; wait for the next poll. This attempt does not count as a success.
            Log.Info("Cover", $"第 {_coverAttempts} 次读到的封面与上一首相同，判定为播放器尚未换图，稍后重试。");
            return _coverDataUri;
        }

        _coverDataUri = dataUri;
        _previousCoverDataUri = dataUri;
        Log.Info("Cover", sameAsPreviousTrack
            ? $"封面与上一首相同（同专辑曲目），已按第 {_coverAttempts} 次重试的上限接受: {dataUri.Length / 1024} KB（data URI）"
            : $"已读取封面: {dataUri.Length / 1024} KB（data URI）");
        return _coverDataUri;
    }

    /// <summary>
    /// 决定这一次要发布的位置、时长与时间轴基准时刻：把三条通道（播放器上报、网易云进程内存、本地计时）交给
    /// <see cref="PositionArbitrationPolicy"/> 仲裁，并按它的选择返回对应的外推基准。
    /// 本地计时的基准时刻返回 <see cref="DateTimeOffset.UtcNow"/>（它已经算到此刻，再让上层外推就会重复累加）；
    /// 内存通道返回采样时刻；播放器上报则用它自己的 <c>LastUpdatedTime</c>。
    /// Decides the position, duration, and timeline reference instant to publish: the three channels (the player's reported
    /// timeline, NetEase's process memory, the local clock) go to <see cref="PositionArbitrationPolicy"/> and the reference
    /// follows its choice. The local clock returns <see cref="DateTimeOffset.UtcNow"/> as the reference (it is already current
    /// and letting the caller extrapolate would double-count), the memory channel returns its sampling instant, and a reported
    /// timeline returns its own <c>LastUpdatedTime</c>.
    /// </summary>
    private (double Position, double Duration, DateTimeOffset TimelineUpdatedAt) ResolvePosition(
        string sourceId,
        string title,
        string artist,
        bool isPlaying,
        double reportedPosition,
        double reportedDuration,
        DateTimeOffset reportedUpdatedAt)
    {
        var trackKey = BuildTrackKey(sourceId, title, artist);
        var trackChanged = !string.Equals(trackKey, _fallbackTrackKey, StringComparison.Ordinal);

        // app 启动后的第一首：歌可能已经播了一大半，位置本来就不是从零开始，"换歌后位置归零"的检查不对它布防。
        // The first track after app start: the song may already be half over, so the position is legitimately not zero
        // and the "returns to zero after a track change" check is not armed for it.
        var firstTrack = _fallbackTrackKey.Length == 0;

        if (trackChanged)
        {
            _fallbackTrackKey = trackKey;
            _fallbackClock.Reset();
            _lastReportedPosition = double.NaN;
            _stalledPolls = 0;

            // 换歌回到这首歌的开头，用户的手动偏移不再适用，一并清零。
            // A track change returns to the start of the new song, so the user's manual offset is stale and cleared too.
            _localClockOffsetSeconds = 0;

            // 看门狗与冻结状态都跟着换歌作废：新歌的位置从零开始是正常的，不能被当成上一首的漂移或顶替。
            // The watchdogs and the frozen state are dropped with the track change: starting from zero is normal for a new
            // song and must not read as the previous song's drift or stand-in.
            _memoryWatchdog.Reset();
            _standInGuard.Reset();
            _lastRealPosition = double.NaN;
            _localClockFrozen = false;
        }

        // 健全性检查、两个看门狗共用这一拍的时间增量。手动同步（ResetLocalClock / NudgeLocalClock 后的
        // RepublishAsync）会在线程池上并发触发一次重读，与轮询循环只差几毫秒，用 Interlocked 取上一 tick 并
        // clamp 到 [0, 1] 秒，保证增量在任何并发下都有意义。
        // The sanity check and both watchdogs share this poll's time delta. A manual sync (RepublishAsync after
        // ResetLocalClock / NudgeLocalClock) triggers a concurrent re-read on the thread pool milliseconds apart from
        // the poll loop, so the previous tick is taken with Interlocked and the delta clamped to [0, 1] s to stay
        // meaningful either way.
        var nowTicks = DateTimeOffset.UtcNow.Ticks;
        var elapsedSeconds = Math.Clamp(
            TimeSpan.FromTicks(nowTicks - Interlocked.Exchange(ref _lastResolveTicks, nowTicks)).TotalSeconds,
            0,
            1);

        // 位置连续多次轮询都不推进，说明这个播放器只是"占了个位置字段"，实际不可用。
        var reportedAdvanced = double.IsNaN(_lastReportedPosition) ||
                               Math.Abs(reportedPosition - _lastReportedPosition) >= 0.01;

        // 位置一次性跳变超过 3 秒，只可能是用户拖了进度条（或播放器自己跳了）。这一行打印的是**播放器上报的原始值**，
        // 采纳与否由下面的仲裁决定 —— 排查时先看这一行知道"播放器报了什么"，再看下面几行知道"最终用了谁"。
        // A one-off jump of more than three seconds can only mean the user dragged the seek bar (or the player jumped). This line
        // prints the **raw reported value**; whether it is adopted is the arbiter's call below. Read this line to learn what the
        // player claimed and the following ones to learn which channel actually won.
        if (!double.IsNaN(_lastReportedPosition) &&
            Math.Abs(reportedPosition - _lastReportedPosition) > 3)
            Log.Info("Media", $"播放器上报的位置跳变: {_lastReportedPosition:0.#} 秒 → {reportedPosition:0.#} 秒（原始值，采纳结果见后续日志）。");

        _lastReportedPosition = reportedPosition;
        _stalledPolls = isPlaying && !reportedAdvanced && reportedPosition > 0 ? _stalledPolls + 1 : 0;

        // 本地计时始终跟着播放状态跑：它既是最后的兜底，也是仲裁时判断"播放器报的值是否离谱"的第二个参照。
        // 采信上报值或内存值时也让它继续跑，这样内存通道一旦读不到（换版本、进程重启），下一拍就能直接接回去。
        // The local clock always follows the playback state: it is both the last fallback and the second reference the arbiter
        // uses to judge whether the reported value is absurd. It keeps running even while a reported or memory value is adopted,
        // so that if the memory channel goes away (a new version, a restarted process) the next poll can take over.
        if (isPlaying)
        {
            if (!_fallbackClock.IsRunning)
                _fallbackClock.Start();
        }
        else
        {
            _fallbackClock.Stop();
        }

        var hasMemory = false;
        var memoryPosition = 0d;
        var memorySampledAt = DateTimeOffset.UtcNow;
        if (NeteasePosition is not null)
            hasMemory = NeteasePosition.TryGetPosition(sourceId, out memoryPosition, out memorySampledAt);

        var localClockPosition = Math.Max(
            0,
            _fallbackClock.Elapsed.TotalSeconds + _localClockBaselineSeconds + _localClockOffsetSeconds);

        var fromSignature = hasMemory && NeteasePosition?.IsSignatureChannel == true;

        // 通道切换那一拍清掉两条看门狗的状态。特征码通道给的是播放器自己的字段，不存在"锁到全局计时器"的可能，
        // 而"位置必须贴近本地计时"这条判据对它也不成立（中途开始播放时本来就差一大截），所以它工作期间两条看门狗
        // 一律停喂；回到扫描通道时必须从零开始，不能带着特征码时期攒下的历史。
        // The poll where the channel changes clears both watchdogs. The signature channel reads the player's own field, so a
        // locked global timer is impossible, and "the position must sit near the local clock" does not hold for it either
        // (starting playback mid-song legitimately puts it far away); both watchdogs are therefore left unfed while it works
        // and have to start clean when the scan channel returns rather than inherit the signature period's history.
        if (fromSignature != _lastFromSignature)
        {
            _lastFromSignature = fromSignature;
            _memorySanity.Reset();
            _memoryWatchdog.Reset();
        }

        // 健全性检查先于仲裁：锁到的值"能读、在区间、1 倍速"只证明它是个计时器，不证明它是当前曲目的位置
        // （真机日志：网易云更新后扫描锁到 12:39 起算的全局计时器，位置变成 4800 秒、换歌不归零）。检查不过就
        // 把这一拍的内存值当作"读不到"，并请求带长冷却的重新扫描——本地计时顶多漂秒级，计时器当位置发布出去
        // 会让歌词越过所有行尾停在最后一行。
        // The sanity check runs before the arbiter: a value that reads fine, sits in range, and advances at 1x only
        // proves it is a timer, not that it is this track's position (real-hardware log: after a NetEase update the
        // scan locked a global timer started 12:39, the position became 4800 seconds and never reset on a track
        // change). A failed check treats this poll's memory value as unreadable and requests a rescan with a long
        // cool-down — the local clock drifts by seconds at worst, while publishing a timer as the position sails the
        // lyrics past every line's end and freezes them on the last one.
        if (!fromSignature)
        {
            var sanity = _memorySanity.Observe(
                armTrackReset: trackChanged && !firstTrack,
                hasMemory: hasMemory,
                memoryPosition: memoryPosition,
                localClockPosition: localClockPosition,
                isPlaying: isPlaying,
                elapsedSeconds: elapsedSeconds);

            if (sanity is not MemoryLockSanityVerdict.Ok)
            {
                var sanityReason = sanity switch
                {
                    MemoryLockSanityVerdict.RejectLockAgreement =>
                        $"新锁定的位置与本地计时相差 {Math.Abs(memoryPosition - localClockPosition):0.#} 秒"
                        + $"（内存 {memoryPosition:0.#} / 计时 {localClockPosition:0.#}）",
                    MemoryLockSanityVerdict.RejectTrackReset =>
                        $"换歌后位置未归零（{memoryPosition:0.#} 秒）",
                    _ => "原因未知"
                };

                Log.Info("Media", $"网易云内存锁定未通过健全性检查：{sanityReason}，本拍不采用，已请求重新扫描。");
                NeteasePosition?.InvalidateChannel(sanityReason, sanityBackoff: true);
                hasMemory = false;
                memoryPosition = 0;
            }
        }

        var decision = _positionArbitration.Decide(
            trackKey,
            new PositionObservation(
                PlayerReports: reportedPosition > 0 && _stalledPolls < StalledPollThreshold,
                PlayerPosition: reportedPosition,
                HasMemory: hasMemory,
                MemoryPosition: memoryPosition,
                LocalClockPosition: localClockPosition,
                IsPlaying: isPlaying));

        LogPositionDecision(decision, reportedPosition, memoryPosition);

        // 真实通道（上报值 / 内存）采信到什么，就记住什么：冻结时要回到这个位置。冻结中若真实通道恢复，
        // 立刻解除冻结并把两个看门狗清零——旧的观察历史属于冻结前的时间段，不带过来。
        // Whatever a real channel (reported value / memory) adopts is remembered: that is where the freeze returns to.
        // If a real channel recovers while frozen, the freeze lifts at once and both watchdogs clear — the old history
        // belongs to the time before the freeze and does not carry over.
        if (decision.Source is PositionSource.Player or PositionSource.Memory)
        {
            _lastRealPosition = decision.Position;

            if (_localClockFrozen)
            {
                _localClockFrozen = false;
                _standInGuard.Reset();
                _memoryWatchdog.Reset();
                Log.Info("Media", $"真实位置通道已恢复，解除本地计时冻结（{decision.Position:0.#} 秒）。");
            }
        }

        // 本地计时顶替上限：连续顶替满 30 秒就把位置钉在最近一次采信的真实位置上，等真实通道恢复。
        // The local clock's stand-in cap: after thirty continuous seconds the position is pinned to the last position
        // adopted from a real channel until a real channel returns.
        if (_standInGuard.Observe(decision.Source == PositionSource.LocalClock, isPlaying, elapsedSeconds)
            is LocalClockStandInVerdict.Freeze && !_localClockFrozen)
        {
            _localClockFrozen = true;
            _frozenPosition = double.IsFinite(_lastRealPosition) ? _lastRealPosition : localClockPosition;
            Log.Info(
                "Media",
                $"本地计时已连续顶替 {LocalClockStandInGuard.MaximumStandInSeconds:0.#} 秒仍无真实通道，冻结播放位置（{_frozenPosition:0.#} 秒），等待通道恢复。");
        }

        // 把本地计时的基准挂到刚刚采信的真实位置上：这样它下一次顶替（例如播放器报了个塌回开头的假值）时，
        // 给出的是"真实位置附近"的值，而不是"从换歌起算"的值 —— 后者在"程序启动时歌已经在播"的场景里
        // 会把歌词直接拉回第一行，反而制造出它要防的那种跳变。
        // Pins the local clock's baseline to the real position just adopted, so the next time it stands in (say the player
        // reported a collapse) it reads near the real position instead of "seconds since the track changed" — the latter would
        // yank the lyrics back to the first line when the app started mid-song, manufacturing the very jump it exists to stop.
        if (decision.Source is PositionSource.Player or PositionSource.Memory &&
            Math.Abs(decision.Position - localClockPosition) > PositionArbitrationPolicy.DiscontinuitySeconds)
        {
            _localClockBaselineSeconds = decision.Position - _fallbackClock.Elapsed.TotalSeconds;
            _localClockOffsetSeconds = 0;
        }

        // 内存通道看门狗。**必须在钉基线之后调用**：v1.4.6 把这段放在 pin 之前，结果新锁定那一拍的窗口基线
        // 采到的是钉基线以前的旧时钟值（真机实例：内存 163.1 / 计时还是 12.7），而 pin 当拍就把时钟改成 ≈163；
        // 窗口结算时时钟"推进"了 150 秒而内存只推进 10 秒 → 健康锁定被误判失效 → 触发重扫 → 第二次扫描锁中
        // 垃圾簇，此后再也回不到正常锁定。放在这里，窗口基线拿到的就是钉基线之后的值，rising poll 也不会
        // 采到"下一秒就被改写"的基线。
        // The memory channel's watchdog. It **must run after the pin**: v1.4.6 placed it before the pin, so the window
        // baseline of a freshly locked poll captured the pre-pin clock value (real instance: memory 163.1, clock still
        // 12.7) while the pin that very poll moved the clock to ≈163; at window close the clock had "advanced" 150
        // seconds against the memory's 10 → a healthy lock was judged stale → a rescan was triggered → the second scan
        // locked a garbage cluster and the channel never recovered. Here the baseline is taken after the pin, and the
        // opening poll can no longer capture a baseline that is rewritten a moment later.
        if (!fromSignature &&
            !_localClockFrozen &&
            _memoryWatchdog.Observe(trackKey, hasMemory, memoryPosition, localClockPosition, isPlaying, elapsedSeconds)
            is MemoryChannelVerdict.Invalidate)
        {
            NeteasePosition?.InvalidateChannel("锁定后与本地计时的推进速度持续不符", sanityBackoff: true);
        }

        PlayerReportsTimeline = decision.Source == PositionSource.Player;

        // 特征码通道顺带给出真实时长与播放状态。网易云 SMTC 上报的时长通常是 0（进度条因此一直是空的），
        // 而播放器自己的时长字段是准的——`tools/SignatureProbe` 实测 261.538 秒对接口的 261537 毫秒，差 1 毫秒。
        // The signature channel also reports the real duration and play status. NetEase's SMTC usually reports a duration of
        // zero (which left the progress bar empty), while the player's own field is accurate — SignatureProbe measured
        // 261.538 s against the API's 261,537 ms, a one-millisecond difference.
        var signatureDuration = 0d;
        var signatureIsPlaying = false;
        if (fromSignature)
            NeteasePosition?.TryGetTrackInfo(out signatureDuration, out signatureIsPlaying);

        var hasSignatureDuration = signatureDuration > 0;
        var effectiveDuration = hasSignatureDuration ? signatureDuration : reportedDuration;

        if (decision.Source == PositionSource.Memory)
        {
            if (!_usingMemoryPosition)
            {
                _usingMemoryPosition = true;
                Log.Info(
                    "Media",
                    fromSignature
                        ? $"已改用网易云播放器的位置字段（特征码通道）：{decision.Position:0.#} 秒，时长 {effectiveDuration:0.#} 秒，播放状态 {(signatureIsPlaying ? "播放中" : "未播放")}。"
                        : $"已改用网易云进程内存中的播放位置：{decision.Position:0.#} 秒。");
            }

            // 返回采样时刻当外推基准：位置每秒才更新一次，上层由此补齐两次更新之间的零头。特征码通道 100 毫秒一拍，
            // 这个零头只有不到 0.1 秒，等于让"擦亮"直接跟着播放器走。
            // The sampling instant is returned as the extrapolation reference: the value updates once a second and this is how
            // the fraction between two updates is filled in. The signature channel polls every 100 ms, so that fraction is
            // under 0.1 s — effectively letting the wipe follow the player directly.
            return (decision.Position, effectiveDuration, memorySampledAt);
        }

        if (_usingMemoryPosition)
        {
            _usingMemoryPosition = false;
            Log.Info("Media", "网易云内存位置暂时读不到，已退回本地计时。");
        }

        if (decision.Source == PositionSource.Player)
        {
            var reference = IsUsableTimestamp(reportedUpdatedAt) ? reportedUpdatedAt : DateTimeOffset.UtcNow;
            return (decision.Position, effectiveDuration, reference);
        }

        // 本地计时顶替：位置已经算到此刻，再让上层按它外推就会重复累加，所以基准给 DateTimeOffset.UtcNow。
        // 时长只在确实有出处时带出去：特征码通道读到的真实时长（它比 SMTC 可靠）优先，其次是播放器确实上报过
        // 时间轴的那几拍（例如"塌回开头"被顶住时），两者都没有就仍然是 0。
        // The local clock stands in: the value is already current, so the reference is UtcNow or the caller would extrapolate on
        // top of it. The duration only rides along when it has a source: the signature channel's real value comes first (it is
        // more trustworthy than SMTC), then the polls where the player really reported a timeline (a collapse held off), and
        // with neither it stays zero.
        var standInDuration = hasSignatureDuration
            ? signatureDuration
            : reportedDuration > 0 && reportedPosition > 0 ? reportedDuration : 0;

        //
        // 冻结中：返回钉住的位置而不是计时读数，基准同样给当前时刻——上层按此外推只会加零，位置就此停住。
        // While frozen: the pinned position is returned instead of the clock reading, again with the current instant as
        // reference, so the caller's extrapolation adds nothing and the position holds still.
        if (_localClockFrozen)
        {
            return (_frozenPosition, standInDuration, DateTimeOffset.UtcNow);
        }

        return (decision.Position, standInDuration, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 把仲裁结果记进日志，但只在"位置来源切换"或"确实发生了跳变"时记：轮询是 250 毫秒一拍，无条件记录会把日志淹掉。
    /// 排查"歌词不跟手"靠的就是这几行：它明确写出这一拍最终用了哪条通道、为什么。
    /// Logs the arbitration result, but only when the position source changes or a jump really happened: the poll runs every
    /// 250 ms and logging unconditionally would drown the log. These lines are what diagnosing "the lyrics do not follow" is
    /// built on: they state which channel won this poll and why.
    /// </summary>
    private void LogPositionDecision(in PositionDecision decision, double reportedPosition, double memoryPosition)
    {
        var sourceChanged = decision.Source != _lastPositionSource;
        _lastPositionSource = decision.Source;

        switch (decision.Reason)
        {
            case PositionDecisionReason.DiffersFromMemory when sourceChanged:
                Log.Info("Media", $"播放器上报的位置与内存通道相差 {Math.Abs(reportedPosition - memoryPosition):0.#} 秒（上报 {reportedPosition:0.#} / 内存 {memoryPosition:0.#}），采用内存值。");
                break;
            case PositionDecisionReason.BackwardCollapsePending when sourceChanged:
                Log.Info("Media", $"播放器上报的位置塌回开头（{reportedPosition:0.#} 秒），先不采纳；连续 {PositionArbitrationPolicy.BackwardConfirmationPolls} 拍都报这个值才当作真实跳转。");
                break;
            case PositionDecisionReason.BackwardCollapseConfirmed:
                Log.Info("Media", $"播放器连续 {PositionArbitrationPolicy.BackwardConfirmationPolls} 拍都报 {reportedPosition:0.#} 秒，按真实跳转采纳。");
                break;
            case PositionDecisionReason.BackwardSeek:
                Log.Info("Media", $"播放器上报的位置向后跳到 {reportedPosition:0.#} 秒（不是塌回开头的形状），按真实跳转采纳。");
                break;
            case PositionDecisionReason.ForwardSeek:
                Log.Info("Media", $"播放器上报的位置向前跳到 {reportedPosition:0.#} 秒，按真实跳转采纳。");
                break;
            case PositionDecisionReason.LocalClockFallback when sourceChanged:
                Log.Info("Media", $"播放器不上报时间轴，改用本地计时：{decision.Position:0.#} 秒。");
                break;
        }
    }

    /// <summary>
    /// 把本地计时复位到当前歌曲的开头。用户在音乐软件里拖动进度条之后，用这个把歌词拉回"从本首歌起点算起"的状态，
    /// 再配合 <see cref="NudgeLocalClock"/> 快进到当前进度。
    /// Resets the local clock to the start of the current track. After the user drags the seek bar in the music app this puts
    /// the lyrics back into "counted from this track's beginning", and <see cref="NudgeLocalClock"/> then advances to where
    /// the playhead is now.
    /// </summary>
    public void ResetLocalClock()
    {
        Interlocked.Exchange(ref _localClockOffsetSeconds, 0d);
        _localClockBaselineSeconds = 0;

        // 仲裁里"上一拍的值"也要一起清掉：它记的是复位之前的位置，不清的话下一拍的上报值（回到开头附近）
        // 会被当成"塌回开头"而白白挡住三拍。
        // The arbiter's "last adopted value" is cleared too: it refers to the position from before the reset, and leaving it
        // would make the next reported value (near the start of the song) look like a collapse and block it for three polls.
        _positionArbitration.Reset();

        // 看门狗、健全性检查与冻结一并作废：用户刚手动把位置对到歌头，旧的观察历史（顶替时长、推进速度、
        // 锁定新鲜度）全部不算数。健全性检查重置后，下一次内存值读数会被当作"新锁定"重新核对与计时的关系。
        // The watchdogs, the sanity check and the freeze are dropped as well: the user just realigned to the start of
        // the song by hand, so none of the old history (stand-in duration, advance rates, lock freshness) counts any
        // more. With the sanity check reset, the next memory reading is treated as a fresh lock and re-checked
        // against the clock.
        _memoryWatchdog.Reset();
        _standInGuard.Reset();
        _memorySanity.Reset();
        _lastRealPosition = double.NaN;
        _localClockFrozen = false;

        _fallbackClock.Restart();
        Log.Info("Media", "本地计时已复位到当前歌曲开头。");
        RepublishAsync();
    }

    /// <summary>
    /// 把本地计时相对推进或回退若干秒，用于在播放器不上报时间轴时手动对齐歌词。
    /// Advances or rewinds the local clock by the given number of seconds, used to align lyrics by hand while the player
    /// reports no timeline.
    /// </summary>
    /// <param name="seconds">正数快进、负数快退，单位为秒。/ Positive to advance, negative to rewind; seconds.</param>
    public void NudgeLocalClock(double seconds)
    {
        if (seconds == 0)
            return;

        // 偏移只对"当前采用本地计时"的场景生效；播放器自己上报时间轴时位置以它为准，偏移会让两套时钟互相打架。
        // The offset only applies while the local clock is the source of truth; when the player reports a timeline the position
        // comes from it, and an offset would make the two clocks fight each other.
        if (PlayerReportsTimeline)
        {
            Log.Info("Media", $"当前播放器自行上报时间轴，忽略手动偏移（{seconds:0.#} 秒）。");
        }
        else
        {
            AddOffset(seconds);

            // 手动微调意味着用户正在对齐位置：冻结解除、顶替计时清零，别让"顶替太久"把用户刚对好的位置又钉回旧值。
            // A manual nudge means the user is realigning the position: the freeze lifts and the stand-in counter clears, so
            // "stood in too long" cannot pin the freshly aligned position back to an old value.
            _localClockFrozen = false;
            _standInGuard.Reset();
            Log.Info("Media", $"本地计时已{(seconds > 0 ? "快进" : "快退")} {Math.Abs(seconds):0.#} 秒。");
        }

        RepublishAsync();
    }

    /// <summary>
    /// 这一拍是不是以"播放器自己上报的时间轴"为准（仲裁结果，见 <see cref="PositionSource.Player"/>）。
    /// 只用于 <see cref="NudgeLocalClock"/> 判断手动偏移该不该生效：位置以播放器为准时，偏移会跟它打架。
    /// Whether this poll is using the player's own reported timeline (an arbitration result, see <see cref="PositionSource.Player"/>).
    /// Only used by <see cref="NudgeLocalClock"/> to decide whether a manual offset applies: while the player's own timeline is
    /// the source of truth, an offset would fight it.
    /// </summary>
    private bool PlayerReportsTimeline { get; set; }

    private void AddOffset(double seconds)
    {
        double initial;
        double updated;
        do
        {
            initial = Volatile.Read(ref _localClockOffsetSeconds);
            updated = Math.Max(-3600, initial + seconds);
        }
        while (Interlocked.CompareExchange(ref _localClockOffsetSeconds, updated, initial) != initial);
    }

    /// <summary>立刻重读并对外广播一次，让"手动同步"不需要等下一个轮询周期就能看到效果。/ Re-reads and republishes at once, so a manual sync is visible without waiting for the next poll.</summary>
    private void RepublishAsync()
    {
        if (_loopCancellation is null)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                Publish(await ReadAsync());
            }
            catch (Exception ex)
            {
                Log.Warn("Media", $"手动同步后重读媒体状态失败: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 判断时间轴的上报时刻是否可用。WinRT 在播放器不上报时给出 1601-01-01（<see cref="DateTimeOffset.MinValue"/>），
    /// 拿它当外推基准会算出一个荒谬的位移，因此只接受 1970 年之后的时刻。
    /// Decides whether a timeline's reported instant is usable. WinRT hands out 1601-01-01
    /// (<see cref="DateTimeOffset.MinValue"/>) when the player reports nothing, and using that as an extrapolation reference
    /// yields an absurd offset, so only instants after 1970 are accepted.
    /// </summary>
    private static bool IsUsableTimestamp(DateTimeOffset value) => value > DateTimeOffset.UnixEpoch;

    /// <summary>
    /// 选一个会话：优先系统认定的当前会话（正在播放时），否则退到第一个正在播放的会话，再否则用当前会话。
    /// 这样"后台暂停 + 前台新播放"的场景不会读错曲目。
    /// Picks a session: the system's current one while it is playing, else the first playing session, else the current one. This
    /// keeps a "paused in the background, newly playing in the foreground" situation from reading the wrong track.
    /// </summary>
    private static GlobalSystemMediaTransportControlsSession? SelectSession(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var current = manager.GetCurrentSession();
        if (current is not null && IsPlaying(current))
            return current;

        foreach (var session in manager.GetSessions())
        {
            if (IsPlaying(session))
                return session;
        }

        return current;
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetPlaybackInfo().PlaybackStatus ==
                   GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        Stop();
        _loopCancellation?.Dispose();
        _loopCancellation = null;
        _manager = null;

        // 内存通道由本读取器持有并负责收尾，调用方只需要创建它。
        // The memory channel is owned and shut down here, so the caller only has to create it.
        NeteasePosition?.Dispose();
        NeteasePosition = null;
    }
}
