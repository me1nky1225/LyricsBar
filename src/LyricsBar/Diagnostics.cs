using System.Windows;
using LyricsBar.Classes.Models;
using LyricsBar.Classes.Services;
using LyricsBar.Classes.Services.Lyrics;
using LyricsBar.Classes.Settings;
using LyricsBar.Classes.Utils;

namespace LyricsBar;

/// <summary>
/// 命令行自检：把"环境是否可用"和"歌词链路是否通"一次性查清楚，结果写进日志。
/// 用法：<c>LyricsBar.exe --diagnose</c>，或 <c>LyricsBar.exe --diagnose "曲名" "歌手"</c> 指定要试取的曲目。
/// Command-line self-check that answers "is this environment usable" and "does the lyric pipeline work" in one pass, writing
/// the result to the log. Usage: <c>LyricsBar.exe --diagnose</c>, or <c>LyricsBar.exe --diagnose "title" "artist"</c> to try a
/// specific track.
/// </summary>
internal static class Diagnostics
{
    private const string DefaultTitle = "晴天";
    private const string DefaultArtist = "周杰伦";

    /// <summary>执行自检；返回 true 表示调用方应直接退出而不启动界面。/ Runs the self-check; true means the caller should exit without starting the UI.</summary>
    internal static bool TryRun(string[] args)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, "--diagnose", StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return false;

        var title = args.Length > index + 1 ? args[index + 1] : DefaultTitle;
        var artist = args.Length > index + 2 ? args[index + 2] : DefaultArtist;

        // 自检不能弹窗：结果一律进日志，便于在无界面环境下收集。
        _ = RunAsync(title, artist);
        return true;
    }

    private static async Task RunAsync(string title, string artist)
    {
        var log = AppLogService.Current;
        try
        {
            log.Info("Diagnose", "===== LyricsBar 自检开始 =====");
            log.Info("Diagnose", $"运行环境: {Environment.OSVersion.VersionString}, .NET {Environment.Version}");
            log.Info("Diagnose", $"设置文件: {SettingsManager.FilePath}");
            log.Info("Diagnose", $"WebView2 运行时: {SafeRuntimeVersion()}");

            var taskbars = new TaskbarDockService().EnumerateTaskbars();
            log.Info("Diagnose", $"发现任务栏 {taskbars.Count} 块");
            foreach (var taskbar in taskbars)
                log.Info("Diagnose", $"  - {taskbar.DisplayName}, DPI 缩放 {taskbar.DpiScale:0.##}");

            using var media = new SmtcMediaReader { NeteasePosition = new NeteasePositionService() };
            var initialized = await media.InitializeAsync();
            log.Info("Diagnose", $"媒体会话管理器: {(initialized ? "可用" : "不可用")}");
            if (initialized)
            {
                var snapshot = await media.ReadAsync();
                log.Info("Diagnose", snapshot.IsConnected
                    ? $"当前曲目: {snapshot.Title} — {snapshot.Artist}（{snapshot.SourceName}，{snapshot.Position:0.0}/{snapshot.Duration:0.0} 秒，播放中={snapshot.IsPlaying}）"
                    : "当前没有正在播放的媒体会话");
                log.Info("Diagnose", snapshot.CoverArt.Length > 0
                    ? $"封面: 已读取，data URI 长度 {snapshot.CoverArt.Length / 1024} KB"
                    : "封面: 无（播放器未提供，或读取失败）");

                await WaitForNeteaseMemoryAsync(log, media, snapshot);
                DumpNeteaseQueueAsync(log, snapshot);
                await DumpRawTimelineAsync(log);
                await VerifyFallbackClockAsync(log, media);
                if (snapshot.IsConnected)
                    await DiagnosePresentationAsync(log, media, snapshot);
            }

            log.Info("Diagnose", $"开始试取歌词: {title} — {artist}");
            var service = LyricsProviderFactory.CreateDefaultService();
            var request = new LyricsRequest(title, artist, string.Empty, null, NetEaseSongId: null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await service.GetLyricsAsync(request, cancellation.Token);

            if (result is null)
            {
                log.Error("Diagnose", "取词失败：所有来源都没有返回结果。");
            }
            else
            {
                var document = result.Document;
                log.Info("Diagnose", $"取词成功: 来源={result.Source}, 匹配分={result.MatchScore}, 状态={result.Status}");
                log.Info("Diagnose", $"格式={document.SourceFormat}, 同步级别={document.SyncType}, 行数={document.Lines.Count}");
                foreach (var line in document.Lines.Take(5))
                    log.Info("Diagnose", $"  [{line.Start:0.00}-{line.End:0.00}] {line.Text}（逐字片段 {line.Words.Count} 个）");

                // 这次修的就是第二行的取值顺序：老顺序把"下一句"排在翻译与音译前面，而下一句几乎总是非空，
                // 翻译和音译就永远轮不到——用户看到的现象正是"第二行只会显示原歌词"。所以这里把每一路的候选值
                // 和最终选中项并排打出来，任意一首歌都能一眼看出第二行是从哪儿来的。
                // The fix is the second row's selection order: the old order ranked the next line above translation and
                // romanization, and because the next line is almost always non-empty those two never got a turn — which is
                // exactly the reported "the second row only ever shows the original lyric". Printing every candidate next to
                // the winner makes the second row's origin obvious for any song.
                var settings = SettingsManager.Current;
                for (var i = 0; i < Math.Min(3, document.Lines.Count); i++)
                {
                    var sample = document.Lines[i];
                    var following = i + 1 < document.Lines.Count ? document.Lines[i + 1].Text : null;
                    var picked = LyricsSecondaryLinePolicy.ResolveSelection(
                        settings.LyricsSecondaryLine, following, sample.Translation, sample.Romanization);
                    log.Info("Diagnose", $"  第二行样例 {i}: 翻译=「{sample.Translation ?? string.Empty}」, " +
                                         $"音译=「{sample.Romanization ?? string.Empty}」, 下一句=「{following ?? string.Empty}」" +
                                         $" → 选中 {picked?.Mode.ToString() ?? "无"}「{picked?.Text ?? string.Empty}」");
                }
            }

            log.Info("Diagnose", "===== 自检结束 =====");
        }
        catch (Exception ex)
        {
            log.Error("Diagnose", "自检过程中发生异常。", ex);
        }
        finally
        {
            // 自检是纯控制台场景，日志写完就退出，不启动任何界面。
            Application.Current?.Dispatcher.Invoke(() => Application.Current.Shutdown());
        }
    }

    private static void DumpNeteaseQueueAsync(AppLogService log, MediaSnapshot snapshot)
    {
        try
        {
            var service = new NeteaseQueueService();
            var songId = service.TryResolveSongId(snapshot.Title, snapshot.Artist);
            if (songId is null)
            {
                log.Info("Diagnose", "网易云本地队列: 未命中当前曲目（可能未安装网易云、未播放、或曲名不匹配）");
                return;
            }

            log.Info("Diagnose", $"网易云本地队列: 命中歌曲 ID={songId}，将走精确取词路径");
        }
        catch (Exception ex)
        {
            log.Warn("Diagnose", $"网易云本地队列诊断失败: {ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>
    /// 直接打印 SMTC 原始时间轴字段。"位置永远是 0"这类问题在聚合后的快照上看不出来，
    /// 必须看 StartTime/EndTime/Position/LastUpdatedTime 的原始值才能判断是播放器不上报还是我们读错。
    /// Dumps the raw SMTC timeline fields. A "position is always 0" problem is invisible in the aggregated snapshot; only the
    /// raw StartTime/EndTime/Position/LastUpdatedTime tell whether the player omits them or we misread them.
    /// </summary>
    private static async Task DumpRawTimelineAsync(AppLogService log)
    {
        try
        {
            var manager = await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var sessions = manager.GetSessions();
            log.Info("Diagnose", $"SMTC 会话数: {sessions.Count}");
            foreach (var session in sessions)
            {
                var playback = session.GetPlaybackInfo();
                var timeline = session.GetTimelineProperties();
                log.Info("Diagnose",
                    $"  会话 {session.SourceAppUserModelId}: 状态={playback.PlaybackStatus}, " +
                    $"Start={timeline.StartTime.TotalSeconds:0.00}, End={timeline.EndTime.TotalSeconds:0.00}, " +
                    $"Position={timeline.Position.TotalSeconds:0.00}, " +
                    $"LastUpdated={timeline.LastUpdatedTime:yyyy-MM-dd HH:mm:ss.fff}, " +
                    $"MinSeek={timeline.MinSeekTime.TotalSeconds:0.00}, MaxSeek={timeline.MaxSeekTime.TotalSeconds:0.00}");
            }
        }
        catch (Exception ex)
        {
            log.Warn("Diagnose", $"读取原始 SMTC 时间轴失败: {ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>
    /// 对"正在播放的这首歌"跑一遍完整的取词 + 投影，把最终帧打出来。
    /// 这是定位"界面一直显示正在检索歌词"的关键：它能区分是取词没结果、行文本为空，还是行下标算成了 -1。
    /// Runs the whole fetch + project chain for the actually-playing track and prints the resulting frame. This is the key to
    /// diagnosing a bar stuck on "searching for lyrics": it separates "no result" from "empty line text" from "line index -1".
    /// </summary>
    private static async Task DiagnosePresentationAsync(AppLogService log, SmtcMediaReader media, MediaSnapshot snapshot)
    {
        log.Info("Diagnose", $"--- 呈现链路自检: {snapshot.Title} — {snapshot.Artist} ---");

        var settings = SettingsManager.Current;
        log.Info("Diagnose", $"设置: 歌词开关={settings.LyricsEnabled}, 双行={settings.TwoLineLyricsEnabled}, " +
                             $"繁简={settings.LyricsChineseConversion}, 逐字={settings.LyricsSyllableHighlightEnabled}");

        var service = LyricsProviderFactory.CreateDefaultService();
        var request = new LyricsRequest(
            snapshot.Title,
            snapshot.Artist,
            string.Empty,
            snapshot.Duration > 0 ? snapshot.Duration : null,
            NetEaseSongId: null,
            PlaybackSourceId: snapshot.SourceId);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await service.GetLyricsAsync(request, cancellation.Token);
        if (result is null)
        {
            log.Warn("Diagnose", "取词无结果：这首歌没有任何来源返回歌词。");
            return;
        }

        var lines = result.Document.Lines;
        log.Info("Diagnose", $"取词: 来源={result.Source}, 状态={result.Status}, 格式={result.Document.SourceFormat}, 行数={lines.Count}");

        var nonEmpty = lines.Count(l => !string.IsNullOrWhiteSpace(l.Text));
        log.Info("Diagnose", $"非空文本行数: {nonEmpty} / {lines.Count}（为 0 表示解析出的行文本全是空的）");
        foreach (var line in lines.Take(3))
            log.Info("Diagnose", $"  原始行 [{line.Start:0.00}-{line.End:0.00}] 「{line.Text}」");

        var withLyrics = snapshot with { Lyrics = result };
        var frame = LyricsPresentationProjector.Project(withLyrics, settings, DateTimeOffset.UtcNow);
        // 第二行到底渲染哪一路，必须连翻译一起打出来：只打"下一句"时，翻译模式下它按定义就是空的，
        // 日志看上去像"第二行没内容"，而实际取到的可能是翻译。 / The second row's actual source has to be logged together
        // with the translations: in translation mode "next" is empty by definition, so logging it alone reads as "the second
        // row is empty" even when a translation was picked.
        log.Info("Diagnose", $"投影结果: IsVisible={frame.IsVisible}, 行下标={frame.CurrentLineIndex}, " +
                             $"当前=「{frame.Current}」, 下一句=「{frame.Next}」, " +
                             $"当前翻译=「{frame.CurrentTranslation}」, 次句翻译=「{frame.NextTranslation}」, " +
                             $"翻译模式={frame.TranslationMode}");

        if (!frame.IsVisible)
        {
            var position = TaskbarExperiencePolicy.GetPosition(withLyrics, DateTimeOffset.UtcNow);
            var firstStart = lines.Count > 0 ? lines[0].Start : double.NaN;
            log.Warn("Diagnose", $"投影为隐藏。推算位置={position:0.00} 秒, 首行起点={firstStart:0.00} 秒, " +
                                 $"曲长={withLyrics.Duration:0.00} 秒。" +
                                 (position < firstStart ? " → 位置早于首行，行下标会算成 -1。" : string.Empty));
        }
    }

    /// <summary>
    /// 等网易云的内存位置通道锁定位址。这条通道要先扫一遍内存（约 5-7 秒）才知道位置存在哪儿，自检在这里等它一下，
    /// 后面那次"位置推进检查"读到的才是内存位置，而不是本地计时。来源不是网易云时直接跳过，不白等。
    /// Waits for the NetEase memory-position channel to lock onto addresses. It has to scan memory once (about five to seven
    /// seconds) before it knows where the position lives, and the self-check waits for that so the advance check below reports
    /// the memory position rather than the local clock. It is skipped outright when the source is not NetEase.
    /// </summary>
    private static async Task WaitForNeteaseMemoryAsync(AppLogService log, SmtcMediaReader media, MediaSnapshot snapshot)
    {
        var service = media.NeteasePosition;
        if (service is null)
        {
            log.Info("Diagnose", "网易云内存位置通道: 未安装");
            return;
        }

        if (!LyricsBar.Classes.Services.MediaSourceNameFormatter.IsNetEase(snapshot.SourceId))
        {
            log.Info("Diagnose", $"网易云内存位置通道: 跳过（当前来源是{snapshot.SourceName}）");
            return;
        }

        log.Info("Diagnose", "网易云内存位置通道: 等待锁定位址（最多 20 秒）");
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (service.IsLocked)
            {
                log.Info("Diagnose", $"网易云内存位置通道: 已锁定（用时 {attempt * 0.5:0.0} 秒）");
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        log.Info("Diagnose", "网易云内存位置通道: 20 秒内未锁定，这次会退回本地计时");
    }

    /// <summary>
    /// 连续读几次位置，验证"播放器不上报时间轴时本地计时能否推进"。
    /// 单次读取看不出问题：本地计时在换歌那一次必然返回 0，只有隔一秒再读才能确认它在走。
    /// Reads the position several times to verify that the local clock advances when the player reports no timeline. A single
    /// read proves nothing: the clock necessarily returns 0 on the poll that detects a new track, so a one-second gap is
    /// needed to see it move.
    /// </summary>
    private static async Task VerifyFallbackClockAsync(AppLogService log, SmtcMediaReader media)
    {
        log.Info("Diagnose", "位置推进检查（间隔 1 秒读 3 次）:");
        for (var i = 0; i < 3; i++)
        {
            var snapshot = await media.ReadAsync();
            log.Info("Diagnose",
                $"  第 {i + 1} 次: 位置={snapshot.Position:0.00} 秒, 时长={snapshot.Duration:0.00} 秒, " +
                $"播放中={snapshot.IsPlaying}, 外推基准={snapshot.TimelineUpdatedAt:HH:mm:ss.fff}");

            if (i < 2)
                await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private static string SafeRuntimeVersion()
    {
        try
        {
            return Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (Exception ex)
        {
            return $"不可用（{ex.GetType().Name}）";
        }
    }
}
