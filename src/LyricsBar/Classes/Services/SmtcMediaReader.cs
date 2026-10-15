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
    /// 本地计时器：播放器完全不上报时间轴时用它顶替播放位置。
    /// 实测（2026-10-14）网易云音乐的 SMTC 时间轴全是零——Start/End/Position/MinSeek/MaxSeek 均为 0，
    /// LastUpdatedTime 是 1601-01-01（WinRT 的零值）。上游项目为此专门做了网易云的内存通道；
    /// 精简版不逆向那个私有通道，改用本地计时兜底：换歌归零、播放中累加、暂停时冻结。
    /// 代价是"程序启动时歌已经在播"或"用户拖动进度条"这两种情况会对不上，
    /// 但相比"整条时间轴为零导致歌词永远不显示"，这是能接受且明确记录在案的取舍。
    /// A local stopwatch standing in for playback position when the player reports no timeline at all.
    /// Measured 2026-10-14: NetEase Cloud Music's SMTC timeline is entirely zero — Start/End/Position/MinSeek/MaxSeek are all 0
    /// and LastUpdatedTime is 1601-01-01 (the WinRT zero value). The upstream project built a private in-memory channel for
    /// NetEase to work around this; the trimmed build does not reverse-engineer that channel and uses a local clock instead:
    /// zeroed on track change, accumulated while playing, frozen while paused. The cost is that "the app started mid-song" and
    /// "the user dragged the seek bar" will be out of sync, which is an accepted and documented trade-off against the
    /// alternative of a permanently blank lyric bar.
    /// </summary>
    private readonly Stopwatch _fallbackClock = new();
    private string _fallbackTrackKey = string.Empty;

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
    /// 决定这一次要发布的位置、时长与时间轴基准时刻。播放器报了可用位置就采信；报的全是零（网易云音乐）或位置
    /// 卡住不动，就换成本地计时，并返回 <see cref="DateTimeOffset.UtcNow"/> 作为外推基准——本地计时已经算到此刻，
    /// 再让上层按这个基准外推就会重复累加。
    /// Decides the position, duration, and timeline reference instant to publish. A usable reported position is trusted; an
    /// all-zero timeline (NetEase Cloud Music) or a frozen one falls back to the local clock, and
    /// <see cref="DateTimeOffset.UtcNow"/> is returned as the extrapolation reference because the clock is already current
    /// and letting the caller extrapolate from an older instant would double-count.
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
        if (trackChanged)
        {
            _fallbackTrackKey = trackKey;
            _fallbackClock.Reset();
            _lastReportedPosition = double.NaN;
            _stalledPolls = 0;

            // 换歌回到这首歌的开头，用户的手动偏移不再适用，一并清零。
            // A track change returns to the start of the new song, so the user's manual offset is stale and cleared too.
            _localClockOffsetSeconds = 0;
        }

        // 位置连续多次轮询都不推进，说明这个播放器只是"占了个位置字段"，实际不可用。
        var reportedAdvanced = double.IsNaN(_lastReportedPosition) ||
                               Math.Abs(reportedPosition - _lastReportedPosition) >= 0.01;

        // 位置一次性跳变超过 3 秒，只可能是用户拖了进度条（或播放器自己跳了）。这里只记录、不做特殊处理：
        // 位置是整段采纳的，跳变下一秒就会反映到歌词行上。日志用于区分"歌词不跟手"到底是没检测到还是没呈现。
        // A one-off jump of more than three seconds can only mean the user dragged the seek bar (or the player jumped). Nothing
        // special happens here beyond logging: the position is adopted wholesale, so the next poll already picks the right
        // lyric line. The log exists to tell "lyrics did not follow" apart from a detection failure.
        if (!double.IsNaN(_lastReportedPosition) &&
            Math.Abs(reportedPosition - _lastReportedPosition) > 3)
            Log.Info("Media", $"检测到播放位置跳变: {_lastReportedPosition:0.#} 秒 → {reportedPosition:0.#} 秒，歌词行已跟随新位置。");

        _lastReportedPosition = reportedPosition;
        _stalledPolls = isPlaying && !reportedAdvanced && reportedPosition > 0 ? _stalledPolls + 1 : 0;

        var trustReported = reportedPosition > 0 && _stalledPolls < StalledPollThreshold;
        PlayerReportsTimeline = trustReported;
        if (trustReported)
        {
            // 播放器报的位置可用：本地计时不参与，保持停摆状态即可。
            _fallbackClock.Stop();
            var reference = IsUsableTimestamp(reportedUpdatedAt) ? reportedUpdatedAt : DateTimeOffset.UtcNow;
            return (reportedPosition, reportedDuration, reference);
        }

        if (isPlaying)
        {
            if (!_fallbackClock.IsRunning)
                _fallbackClock.Start();
        }
        else
        {
            _fallbackClock.Stop();
        }

        // 网易云的时间轴恒为零，但它的播放位置在进程内存里读得到（见 NeteasePositionService）：采信它，拖进度条才会跟着走。
        // 本地计时故意继续跑着——内存通道一旦读不到（换版本、进程重启），下一拍就能直接接回去，而不是从头开始数。
        // NetEase's timeline is permanently zero, yet its playback position is readable from process memory (see
        // NeteasePositionService). Trusting it is what makes seeking follow. The local clock is deliberately left running: if
        // the memory channel goes away (a new version, a restarted process) the next poll can take over again instead of
        // counting from zero.
        if (NeteasePosition is not null &&
            NeteasePosition.TryGetPosition(sourceId, out var memoryPosition, out var memorySampledAt))
        {
            if (!_usingMemoryPosition)
            {
                _usingMemoryPosition = true;
                Log.Info("Media", $"已改用网易云进程内存中的播放位置：{memoryPosition:0.#} 秒。");
            }

            // 返回采样时刻当外推基准：位置每秒才更新一次，上层由此补齐两次更新之间的零头。
            // The sampling instant is returned as the extrapolation reference: the value updates once a second and this is how
            // the fraction between two updates is filled in.
            return (memoryPosition, reportedDuration, memorySampledAt);
        }

        if (_usingMemoryPosition)
        {
            _usingMemoryPosition = false;
            Log.Info("Media", "网易云内存位置暂时读不到，已退回本地计时。");
        }

        return (
            Math.Max(0, _fallbackClock.Elapsed.TotalSeconds + _localClockOffsetSeconds),
            0,
            DateTimeOffset.UtcNow);
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
        if (!PlayerReportsTimeline)
        {
            AddOffset(seconds);
            Log.Info("Media", $"本地计时已{(seconds > 0 ? "快进" : "快退")} {Math.Abs(seconds):0.#} 秒。");
        }
        else
        {
            Log.Info("Media", $"当前播放器自行上报时间轴，忽略手动偏移（{seconds:0.#} 秒）。");
        }

        RepublishAsync();
    }

    /// <summary>当前是否正在使用本地计时兜底（播放器不上报可用时间轴）。/ Whether the local clock is currently standing in (the player reports no usable timeline).</summary>
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
