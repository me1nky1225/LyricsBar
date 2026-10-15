using LyricsBar.Classes.Models;
using LyricsBar.Classes.Services.Lyrics;
using LyricsBar.Classes.Settings;
using LyricsBar.Classes.Utils;

namespace LyricsBar.Classes.Services;

/// <summary>
/// 把"当前播放"与"歌词文档"接在一起：监听媒体变化、按曲目取词、把结果并进快照并对外广播。
/// Joins "now playing" with "lyric document": watches media changes, retrieves lyrics per track, folds the result into the
/// snapshot, and broadcasts it.
///
/// 与原项目的差异：原项目的取词触发散落在 MediaSessionService 内部，与双通道仲裁、封面解码、诊断计数交织在一起；
/// 精简版把这一步单独拿出来，职责只有三条——换歌就取词、同一首不重复取词、设置改了按策略清缓存。
/// Difference from the original: the original triggered retrieval inside MediaSessionService, interleaved with two-channel
/// arbitration, artwork decoding, and diagnostic counters. The trimmed build pulls that step out with exactly three duties:
/// fetch on track change, never refetch the same track, and clear the cache per policy when settings change.
/// </summary>
public sealed class LyricsCoordinator : IDisposable
{
    /// <summary>歌词缓存的条数上限。按曲目缓存，含"确认无歌词"，避免来回切歌时反复打第三方接口。</summary>
    private const int CacheCapacity = 64;

    /// <summary>
    /// 取词失败后的重试间隔。网络抖一下不该把这首歌判成"这首歌没有歌词"——用户听着歌，
    /// 十几秒后网络恢复，歌词应该自己出现，而不是要手动切歌或重启。
    /// Retry interval after a failed lookup. A momentary network hiccup must not condemn the track to "has no lyrics":
    /// the user is listening, the network recovers, and the lyrics should show up on their own instead of needing a manual
    /// re-select or restart.
    /// </summary>
    private static readonly TimeSpan FetchRetryDelay = TimeSpan.FromSeconds(15);

    /// <summary>单首曲子的最大重试次数；超过就认命，停在曲名歌手兜底画面上。/ Maximum retries per track; past this the app gives up and stays on the title-and-artist fallback.</summary>
    private const int MaxFetchRetries = 3;

    private readonly SmtcMediaReader _media;
    private readonly LyricsService _lyricsService;
    private readonly NeteaseQueueService _neteaseQueue = new();
    private readonly LruCache<string, LyricsResult?> _cache = new(CacheCapacity);
    private readonly SemaphoreSlim _fetchGate = new(1, 1);

    private CancellationTokenSource? _fetchCancellation;

    /// <summary>失败重试的取消源。新取词、切歌、退出时都要取消，否则重试会打已经不播了的歌。</summary>
    private CancellationTokenSource? _retryCancellation;
    private MediaSnapshot _latest = MediaSnapshot.Disconnected;
    private string _latestTrackKey = string.Empty;
    private bool _disposed;

    /// <summary>快照更新时触发（可能来自媒体变化，也可能来自取词完成）。订阅者需自行切回 UI 线程。</summary>
    public event Action<MediaSnapshot>? SnapshotUpdated;

    /// <summary>用默认提供器序列创建协调器。/ Creates the coordinator with the default provider sequence.</summary>
    public LyricsCoordinator()
        : this(
            new SmtcMediaReader { NeteasePosition = new NeteasePositionService() },
            LyricsProviderFactory.CreateDefaultService())
    {
    }

    /// <summary>注入依赖，便于测试与替换。/ Injects dependencies so the coordinator can be tested or swapped.</summary>
    public LyricsCoordinator(SmtcMediaReader media, LyricsService lyricsService)
    {
        _media = media;
        _lyricsService = lyricsService;
        _media.SnapshotChanged += OnMediaSnapshotChanged;
        SettingsManager.Changed += OnSettingsChanged;
    }

    /// <summary>当前最新快照（已并入歌词）。UI 每帧据此投影，不重新取词。/ The latest snapshot with lyrics folded in; the UI projects from it every frame without refetching.</summary>
    public MediaSnapshot Latest => _latest;

    /// <summary>启动媒体监听。失败时返回 false，界面应提示"未找到媒体会话"而不是崩溃。</summary>
    public async Task<bool> StartAsync()
    {
        var initialized = await _media.InitializeAsync();
        if (!initialized)
        {
            Log.Warn("Lyrics", "媒体会话管理器不可用，歌词栏将保持空闲。");
            return false;
        }

        _media.Start();
        return true;
    }

    /// <summary>停止监听并取消在途取词。/ Stops listening and cancels any in-flight retrieval.</summary>
    public void Stop()
    {
        _media.Stop();
        CancelFetch();
    }

    /// <summary>
    /// 手动把歌词时间复位到当前歌曲的开头。播放器不上报时间轴（网易云音乐）时，用户在音乐软件里拖动进度条
    /// 之后用这个重新对齐；会上报时间轴的播放器不需要它。
    /// Resets the lyric time to the start of the current track. Used to realign after the user drags the seek bar in a player
    /// that reports no timeline (NetEase); players that report one need no such thing.
    /// </summary>
    public void ResetLyricsPosition() => _media.ResetLocalClock();

    /// <summary>把歌词时间相对快进或回退若干秒。/ Advances or rewinds the lyric time by the given seconds.</summary>
    public void NudgeLyricsPosition(double seconds) => _media.NudgeLocalClock(seconds);

    private void OnMediaSnapshotChanged(MediaSnapshot snapshot)
    {
        if (_disposed)
            return;

        var key = TrackKeyOf(snapshot);

        // 同一首曲子只换播放位置时，直接沿用已取到的歌词，不重新取词。
        // "取词是否结束"也一并沿用：这个标志由协调器维护，读取器每轮都给 false，
        // 不显式带过来的话，取词刚结束就会被下一次轮询抹掉，纯音乐的曲名与歌手会一闪即逝。
        // When only the position changes, the retrieved lyrics are reused instead of refetched. The "lookup finished" flag is
        // carried over too: the coordinator owns it and the reader reports false every poll, so without this the flag would be
        // wiped by the next poll right after the lookup finished, making the instrumental's title and artist flash and vanish.
        if (string.Equals(key, _latestTrackKey, StringComparison.Ordinal) &&
            snapshot.IsConnected == _latest.IsConnected)
        {
            _latest = snapshot with
            {
                Lyrics = _latest.Lyrics,
                LyricsLookupCompleted = _latest.LyricsLookupCompleted
            };
            SnapshotUpdated?.Invoke(_latest);
            return;
        }

        _latestTrackKey = key;

        // 换歌就记一行：真出问题时，"当时在放哪首歌"是唯一能把界面上的歌词和曲目对上的凭据。
        // One line per track change: when something goes wrong, "what was playing" is the only way to line the lyrics on
        // screen up against the track afterwards.
        if (!string.IsNullOrWhiteSpace(snapshot.Title))
            Log.Info("Lyrics", $"换歌: {snapshot.Title} — {snapshot.Artist}（{snapshot.SourceName}）");

        _latest = snapshot with { Lyrics = null, LyricsLookupCompleted = false };
        SnapshotUpdated?.Invoke(_latest);

        if (!snapshot.IsConnected || string.IsNullOrWhiteSpace(snapshot.Title))
            return;

        _ = FetchLyricsAsync(snapshot, key);
    }

    private async Task FetchLyricsAsync(MediaSnapshot snapshot, string trackKey)
    {
        CancelFetch();
        var cancellation = new CancellationTokenSource();
        _fetchCancellation = cancellation;

        // 命中缓存（含"确认无歌词"）时直接结束，不再打第三方接口。
        if (_cache.TryGetValue(trackKey, out var cached))
        {
            ApplyLyrics(trackKey, cached);
            return;
        }

        await _fetchGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try
        {
            if (cancellation.IsCancellationRequested)
                return;

            var request = new LyricsRequest(
                snapshot.Title,
                snapshot.Artist,
                string.Empty,
                snapshot.Duration > 0 ? snapshot.Duration : null,
                NetEaseSongId: ResolveNeteaseSongId(snapshot),
                PlaybackSourceId: snapshot.SourceId);

            var result = await _lyricsService.GetLyricsAsync(request, cancellation.Token).ConfigureAwait(false);
            if (cancellation.IsCancellationRequested)
                return;

            // 取词预算最长 12 秒；期间用户可能已经切歌，只有仍是当前曲目才落缓存与界面。
            if (!string.Equals(trackKey, _latestTrackKey, StringComparison.Ordinal))
                return;

            if (result is null)
            {
                // 失败**不进缓存**：网络抖一下就把这首歌记成"确认无歌词"的话，整个会话里再放
                // 都不会重试，用户只能手动切歌或重启。界面这边照常走曲名歌手兜底（LookupCompleted
                // 一并置上），同时挂一个延迟重试，网络恢复后歌词自己出现。
                // A failure is **not cached**: recording it as "confirmed no lyrics" on a network hiccup means the rest of
                // the session never retries this track and the user has to re-select it or restart. The UI still falls back
                // to title and artist (the lookup is marked finished), while a delayed retry is scheduled so the lyrics show
                // up by themselves once the network recovers.
                ApplyLyrics(trackKey, null);
                ScheduleFetchRetry(snapshot, trackKey, attempt: 1);
                return;
            }

            _cache.Set(trackKey, result);
            ApplyLyrics(trackKey, result);
        }
        catch (OperationCanceledException)
        {
            // 切歌或退出导致的取消，属于正常路径。
        }
        catch (Exception ex)
        {
            Log.Warn("Lyrics", $"取词失败: {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            try { _fetchGate.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    private void ApplyLyrics(string trackKey, LyricsResult? lyrics)
    {
        if (_disposed || !string.Equals(trackKey, _latestTrackKey, StringComparison.Ordinal))
            return;

        // 同时置位"取词已结束"：歌词为 null 有两种含义——"还在取"与"确认没有"，
        // 呈现层要靠这个标志区分（前者什么都不显示，后者显示曲名与歌手，纯音乐就是后者）。
        // Also marks the lookup as finished: null lyrics mean either "still looking" or "confirmed none", and the presentation
        // layer needs this flag to tell them apart (nothing for the former, title and artist for the latter — instrumentals).
        _latest = _latest with { Lyrics = lyrics, LyricsLookupCompleted = true };
        SnapshotUpdated?.Invoke(_latest);
    }

    /// <summary>
    /// 给一次失败的取词挂延迟重试。仍是当前曲目、没超过次数才真的重试；
    /// 重试期间用户切了歌，键一不符就整条链安静退出。
    /// Schedules a delayed retry after a failed lookup. The retry only runs when it is still the current track and the
    /// attempt budget is not spent; if the user changes track in the meantime the key check drops the whole chain silently.
    /// </summary>
    private void ScheduleFetchRetry(MediaSnapshot snapshot, string trackKey, int attempt)
    {
        if (_disposed || attempt > MaxFetchRetries)
            return;

        _retryCancellation?.Cancel();
        _retryCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _retryCancellation = cancellation;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(FetchRetryDelay, cancellation.Token).ConfigureAwait(false);
                if (cancellation.IsCancellationRequested || _disposed)
                    return;
                if (!string.Equals(trackKey, _latestTrackKey, StringComparison.Ordinal))
                    return;

                Log.Info("Lyrics", $"第 {attempt}/{MaxFetchRetries} 次重试取词: {snapshot.Title} — {snapshot.Artist}");

                var request = new LyricsRequest(
                    snapshot.Title,
                    snapshot.Artist,
                    string.Empty,
                    snapshot.Duration > 0 ? snapshot.Duration : null,
                    NetEaseSongId: ResolveNeteaseSongId(snapshot),
                    PlaybackSourceId: snapshot.SourceId);

                var result = await _lyricsService.GetLyricsAsync(request, cancellation.Token).ConfigureAwait(false);
                if (cancellation.IsCancellationRequested || _disposed)
                    return;
                if (!string.Equals(trackKey, _latestTrackKey, StringComparison.Ordinal))
                    return;

                if (result is not null)
                    _cache.Set(trackKey, result);
                ApplyLyrics(trackKey, result);

                // 还是失败就再排一次，直到次数用完。
                // Still failing: schedule another attempt until the budget is spent.
                if (result is null)
                    ScheduleFetchRetry(snapshot, trackKey, attempt + 1);
            }
            catch (OperationCanceledException)
            {
                // 切歌或退出导致的取消，属于正常路径。
            }
            catch (Exception ex)
            {
                Log.Warn("Lyrics", $"重试取词出错: {ex.GetType().Name} {ex.Message}");
            }
        }, cancellation.Token);
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (_disposed)
            return;

        // 只有"影响取词结果"的设置变化才清缓存；改字号、字距这类纯呈现设置不该触发重新联网取词。
        if (LyricsCacheInvalidationPolicy.ShouldClearCache(e.PropertyName, e.ResetScope))
        {
            _cache.Clear();
        }

        // 无论是否清缓存，都要把最新快照重发一次，让呈现层立刻用新设置重绘。
        SnapshotUpdated?.Invoke(_latest);
    }

    private void CancelFetch()
    {
        var cancellation = Interlocked.Exchange(ref _fetchCancellation, null);
        if (cancellation is not null)
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        // 重试链一并取消：切歌之后还去取上一首的歌词没有意义。
        // The retry chain is cancelled too: fetching the previous track's lyrics after a track change is pointless.
        var retry = Interlocked.Exchange(ref _retryCancellation, null);
        if (retry is not null)
        {
            try { retry.Cancel(); retry.Dispose(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// 解析这次取词要用的网易云歌曲 id。
    ///
    /// 只有当前播放器**就是网易云**时才去读它的本地队列：别的播放器读它没有意义，而且平白多读一次 1.4MB 的文件。
    /// 拿到 id 之后，<c>Netease</c> 来源就能按 id 精确取词（命中即 100 分），而它现在又是"当前播放器自己的来源"、
    /// 在优先阶段排在第一位，所以这一条能真正决定最终用谁——这正是"匹配更准"的来源。
    /// Resolves the NetEase song id for this retrieval.
    ///
    /// NetEase's local queue is only read while NetEase itself is the player: for any other player the lookup is meaningless
    /// and would only cost a 1.4 MB file read. With the id in hand the <c>Netease</c> source can fetch exactly by id (a flat
    /// 100), and because it is now also the current player's own source it leads the preferred stage, so this path really does
    /// decide the outcome — which is where the better matching comes from.
    /// </summary>
    private string? ResolveNeteaseSongId(MediaSnapshot snapshot) =>
        LyricsSourcePolicy.IsCurrentPlaybackSource(snapshot.SourceId, LyricsSourceCatalog.NetEase)
            ? _neteaseQueue.TryResolveSongId(snapshot.Title, snapshot.Artist)
            : null;

    /// <summary>曲目身份：来源 + 曲名 + 歌手。与呈现层构造 TrackId 的口径保持一致，换歌检测才不会有偏差。</summary>
    private static string TrackKeyOf(MediaSnapshot snapshot) => string.Join(
        "\u001f",
        snapshot.SourceId.Trim().ToUpperInvariant(),
        snapshot.Title.Trim().ToUpperInvariant(),
        snapshot.Artist.Trim().ToUpperInvariant());

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _media.SnapshotChanged -= OnMediaSnapshotChanged;
        SettingsManager.Changed -= OnSettingsChanged;
        CancelFetch();
        _media.Dispose();
        _fetchGate.Dispose();
    }
}
