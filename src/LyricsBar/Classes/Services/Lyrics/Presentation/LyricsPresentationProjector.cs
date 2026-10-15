using LyricsBar.Classes.Models;
using LyricsBar.Classes.Services;
using LyricsBar.Classes.Settings;

namespace LyricsBar.Classes.Services.Lyrics;

/// <summary>
/// 可直接发送给 Web 歌词呈现引擎的目标帧。歌词检索和文本解析已经在上游完成；该帧只表达目标画面。
/// A target frame ready for the web lyrics presentation engine. Retrieval and parsing are complete upstream; this frame only
/// describes the target visual state.
/// </summary>
public sealed record LyricsPresentationFrame(
    bool IsVisible,
    string TrackId,
    int CurrentLineIndex,
    string Current,
    string Next,
    string CurrentTranslation,
    string NextTranslation,
    bool TranslationMode,
    double LineProgress,
    double? WordScanProgress,
    bool IsPlaying)
{
    public static LyricsPresentationFrame Hidden { get; } = new(
        false,
        string.Empty,
        -1,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        false,
        0,
        null,
        false);
}

/// <summary>
/// 把不可变媒体快照投影成 Web 歌词目标帧。调用者只需要提交快照、设置和当前时间；行选择、第二行语义、
/// 位置外推与真实逐字进度全部封装在本模块中。
/// Projects an immutable media snapshot into a web-lyrics target frame. Callers submit only the snapshot, settings, and current
/// time; line selection, secondary-row semantics, timeline extrapolation, and real syllable progress stay inside this module.
/// </summary>
public static class LyricsPresentationProjector
{
    public static LyricsPresentationFrame Project(
        MediaSnapshot snapshot,
        AppSettings settings,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);

        var lines = snapshot.Lyrics?.Document.Lines;
        if (!settings.LyricsEnabled || !snapshot.IsConnected)
        {
            return LyricsPresentationFrame.Hidden;
        }

        if (lines is not { Count: > 0 })
        {
            // 没有歌词行。这时必须区分两种"没有"：取词还在进行中，与确认这一首就是没有歌词。
            // 后者（纯音乐、伴奏、无人声）如果什么都不显示，任务栏上就完全看不出在放什么，
            // 所以改为留下封面、（开了的话）曲目信息块，或把曲名与歌手顶到歌词行上；取词进行中则维持原样，什么都不显示。
            // No lyric lines. Two different "nothing" cases have to be told apart here: a lookup still in flight, and a track
            // confirmed to have no lyrics. Showing nothing for the latter (instrumentals, backing tracks, no vocals) leaves the
            // taskbar with no clue about what is playing, so the artwork — and the track-info block or a promoted title/artist
            // pair — is kept instead; an in-flight lookup keeps the previous behaviour and shows nothing.
            // 曲目信息块（LyricsTrackInfoBlockEnabled）开着时，曲名与歌手已经由那一块常驻显示，
            // 再走这条路只会把同样的字在同一根歌词栏里再印一遍，所以不再把曲名顶到歌词行上。
            // When the track-info block (LyricsTrackInfoBlockEnabled) is on, title and artist are already shown permanently by
            // that block, so taking this path would only print the same text a second time inside one bar; the title is not
            // promoted onto a lyric row here.
            if (settings.LyricsShowTrackInfoWhenNoLyrics
                && snapshot.LyricsLookupCompleted
                && !settings.LyricsTrackInfoBlockEnabled)
            {
                return BuildTrackInfoFrame(snapshot, settings);
            }

            // 没有歌词可唱 ≠ 没有东西可显示。封面与曲目信息块跟歌词帧无关（由 ApplyCover / ApplyTrackInfo 单独下发），
            // 所以这里必须交出一帧"空歌词"让整条栏继续留在任务栏上；返回 Hidden 会让 SetBarVisible(false) 把窗口变全透明，
            // 于是封面与被用户打开的那块曲目信息一起被吞掉——纯音乐在任务栏上就什么都不剩。
            // No lyrics to sing is not the same as nothing to show. The artwork and the track-info block are independent of the
            // lyric frame (they are pushed by ApplyCover / ApplyTrackInfo), so this has to hand back an "empty lyrics" frame and
            // keep the bar docked. Returning Hidden makes SetBarVisible(false) fade the whole window out, swallowing the artwork
            // and the very track-info block the user switched on — an instrumental then shows nothing at all.
            return BuildEmptyFrame(snapshot);
        }

        var position = TaskbarExperiencePolicy.GetPosition(snapshot, now);
        var index = LyricLineIndexPolicy.FindIndex(lines, position);
        if (index < 0)
        {
            // 位置早于首行（前奏）时，把首行当作"即将唱到"显示，而不是让整条歌词栏空着。
            // 不少歌的前奏有十几秒，空着会被当成"歌词没取到"；显示首行则立刻给出"取到了"的反馈。
            // 上游在这里回落到标题与歌手，精简版没有标题组件，用首行替代是最接近的等价行为。
            // Before the first line (an intro), the first line is shown as the upcoming one instead of leaving the whole bar
            // empty. Intros of ten-plus seconds are common and an empty bar reads as "no lyrics found", whereas showing the
            // first line gives immediate confirmation. Upstream fell back to title and artist here; the trimmed build has no
            // title component, so the first line is the closest equivalent.
            index = 0;
        }

        var currentLine = lines[index];

        // 曲目信息不再参与这两行：它由 <see cref="AppSettings.LyricsTrackInfoBlockEnabled"/> 控制，
        // 在 Web 端作为封面右侧的独立区块渲染（见 <see cref="LyricsWebViewRenderer.SetTrackInfo"/>）。
        // 因此这里只负责歌词本身——主行永远是当前歌词行，第二行按用户配的顺序取下一句/翻译/音译。
        // Track info no longer competes for either row: the web side renders it as a dedicated block right of the artwork
        // under AppSettings.LyricsTrackInfoBlockEnabled (see LyricsWebViewRenderer.SetTrackInfo). This projector therefore
        // only deals with the lyrics — the main row is always the current line and the second row follows the user's
        // configured order (next line / translation / romanization).
        var mainText = currentLine.Text;
        var nextLine = index + 1 < lines.Count ? lines[index + 1] : null;
        var followingLine = index + 2 < lines.Count ? lines[index + 2] : null;
        var currentSecondary = settings.TwoLineLyricsEnabled
            ? LyricsSecondaryLinePolicy.ResolveSelection(
                settings.LyricsSecondaryLine,
                nextLine?.Text,
                currentLine.Translation,
                currentLine.Romanization)
            : null;

        var nextSecondary = settings.TwoLineLyricsEnabled && nextLine is not null
            ? LyricsSecondaryLinePolicy.ResolveSelection(
                settings.LyricsSecondaryLine,
                followingLine?.Text,
                nextLine.Translation,
                nextLine.Romanization)
            : null;

        var translationMode = currentSecondary is { Mode: not LyricsSecondaryLineMode.NextLine };
        var secondaryText = currentSecondary is { Mode: LyricsSecondaryLineMode.NextLine } selection
            ? selection.Text
            : string.Empty;
        var currentTranslation = translationMode ? currentSecondary?.Text ?? string.Empty : string.Empty;
        var nextTranslation = nextSecondary is { Mode: not LyricsSecondaryLineMode.NextLine }
            ? nextSecondary.Value.Text
            : string.Empty;

        // 行下标是前端"这一帧要不要翻卷"的身份依据（同曲同下标只换文本，下标变了才滚动）。
        // 主行永远是当前歌词行，所以下标就是歌词行下标本身，不需要任何钉死。
        // The line index is the front end's identity for "should this frame roll" (same track plus same index means a text
        // swap; a changed index triggers the roll). The main row is always the current lyric line, so the index is simply the
        // lyric line index and needs no pinning.
        var emittedLineIndex = index;

        var lineProgress = ResolveLineProgress(currentLine, position);
        // 逐字擦亮作用于主行，而主行始终是当前歌词行，所以只要有逐字时间轴就开着。
        // Syllable highlighting belongs to the main row, which is always the current lyric line, so it stays on whenever a
        // syllable timeline exists.
        var wordScanProgress = settings.LyricsSyllableHighlightEnabled
            ? LyricHighlightPolicy.ResolveProgress(currentLine, position)
            : null;

        // 繁简转换落在"文本流向呈现层的最后一步"：当前行、下一句、译文与下一句译文都走同一个设置，
        // 因此不会出现"主行转了、第二行没转"的半截画面。逐字擦亮是 CSS 按渲染宽度百分比裁剪的，
        // 不依赖字符索引，所以转换改变字数也不会错位。
        // Conversion happens where text leaves for the presentation layer: the active line, the next line, and both translations all
        // follow the one setting, so there is never a half-converted frame. Syllable highlighting clips by rendered-width percentage in
        // CSS and does not index characters, so a changed character count cannot misalign it.
        var conversion = settings.LyricsChineseConversion;

        return new LyricsPresentationFrame(
            true,
            BuildTrackId(snapshot),
            emittedLineIndex,
            LyricsChineseConverter.Convert(mainText, conversion),
            LyricsChineseConverter.Convert(secondaryText, conversion),
            LyricsChineseConverter.Convert(currentTranslation, conversion),
            LyricsChineseConverter.Convert(nextTranslation, conversion),
            translationMode,
            lineProgress,
            wordScanProgress,
            snapshot.IsPlaying);
    }

    /// <summary>
    /// 曲目信息块要显示的两行文本：曲名与歌手，各自去掉首尾空白。
    /// 两者相同（不少播放器把曲名与歌手发布成同一个字符串）时丢掉歌手，否则区块里会出现两行一模一样的字。
    /// 两边都为空就都返回空串，调用方据此把整块收起来，而不是占着宽度显示空白。
    /// The two lines the track-info block shows: title and artist, each trimmed. When the two are identical — several players
    /// publish the same string for both — the artist is dropped so the block does not show the same text twice. Both come back
    /// empty when neither is known, letting the caller collapse the whole block instead of holding width for blank text.
    /// </summary>
    /// <param name="snapshot">媒体快照。/ The media snapshot.</param>
    public static (string Title, string Artist) ResolveTrackInfoLines(MediaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var title = snapshot.Title?.Trim() ?? string.Empty;
        var artist = snapshot.Artist?.Trim() ?? string.Empty;

        if (title.Length > 0 && artist.Length > 0 &&
            string.Equals(title, artist, StringComparison.OrdinalIgnoreCase))
        {
            artist = string.Empty;
        }

        return (title, artist);
    }

    /// <summary>
    /// 构造"这一首没有歌词"时的目标帧：曲名当主行、歌手当第二行，配合封面，让纯音乐在任务栏上仍能看出在放什么。
    ///
    /// 复用歌词场景而不是新增一个场景：前端那套场景状态机已经处理了换行、淡入淡出与逐字擦亮，
    /// 再塞一个场景进去只会多出转场分支与回归风险；曲名与歌手本来就是一"行"文本，走同一条渲染路径最省事也最稳。
    /// 逐字擦亮与进度都置零，避免主行被当成"正在唱"而做擦亮动画。
    /// Builds the target frame for "this track has no lyrics": the title as the main row and the artist as the second, alongside
    /// the artwork, so an instrumental still shows what is playing.
    ///
    /// It reuses the lyric scene rather than adding a new one: the front end's scene state machine already handles line
    /// transitions, crossfades, and syllable highlighting, and a new scene would only add transition branches and regression
    /// risk. Title and artist are just "a line of text", so the existing path is both the least work and the safest. Progress and
    /// syllable scan are zeroed so the main row is not treated as "currently being sung" and animated.
    /// </summary>
    /// <summary>
    /// 构造"这一首确认没有歌词、但栏要留着"的帧：可见，歌词两行都是空串。
    ///
    /// 交给它一帧空歌词而不是让它返回 <see cref="LyricsPresentationFrame.Hidden"/>，是因为"隐藏"在这条链路上意味着
    /// 整条歌词栏淡出（<c>TaskbarLyricsWindow.SetBarVisible(false)</c>）。封面与曲目信息块虽然与歌词帧无关、
    /// 各自单独下发，却都画在这条栏里，栏一淡出它们就一起消失了。空行不会占宽度：自动宽度量到 0，
    /// 剩下的只有封面与曲目信息块本身的宽度。
    /// Builds the frame for "this track is confirmed to have no lyrics, but the bar stays": visible, with both lyric rows empty.
    ///
    /// It hands back an empty lyric frame rather than <see cref="LyricsPresentationFrame.Hidden"/> because on this path "hidden"
    /// means the whole bar fades out (<c>TaskbarLyricsWindow.SetBarVisible(false)</c>). The artwork and the track-info block are
    /// independent of the lyric frame and are pushed separately, but both are drawn inside that bar, so fading it out takes them
    /// with it. Empty rows take no width: the automatic width measures 0 and what remains is the artwork and the track-info
    /// block's own width.
    /// </summary>
    /// <param name="snapshot">媒体快照 / The media snapshot.</param>
    private static LyricsPresentationFrame BuildEmptyFrame(MediaSnapshot snapshot) => new(
        true,
        BuildTrackId(snapshot),
        -1,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        false,
        0,
        null,
        snapshot.IsPlaying);

    private static LyricsPresentationFrame BuildTrackInfoFrame(MediaSnapshot snapshot, AppSettings settings)    {
        var title = snapshot.Title?.Trim() ?? string.Empty;
        if (title.Length == 0)
        {
            // 连曲名都没有就没什么可显示的了：留空比显示一个没有意义的占位更诚实。
            // With no title there is nothing worth showing; an empty bar is more honest than a meaningless placeholder.
            return LyricsPresentationFrame.Hidden;
        }

        // 歌手只在用户开启第二行时才带出来：这是他在"排版"里表达过的偏好，曲目信息不该绕过它。
        // The artist only rides along when the user enabled the second row: that is a preference they expressed in the layout
        // settings, and track info has no business overriding it.
        var artist = settings.TwoLineLyricsEnabled ? snapshot.Artist?.Trim() ?? string.Empty : string.Empty;

        return new LyricsPresentationFrame(
            true,
            BuildTrackId(snapshot),
            0,
            title,
            artist,
            string.Empty,
            string.Empty,
            false,
            0,
            null,
            snapshot.IsPlaying);
    }

    private static string BuildTrackId(MediaSnapshot snapshot) => string.Join(
        "\u001f",
        snapshot.SourceId.Trim().ToUpperInvariant(),
        snapshot.Title.Trim().ToUpperInvariant(),
        snapshot.Artist.Trim().ToUpperInvariant());

    private static double ResolveLineProgress(LyricLine line, double position)
    {
        if (!double.IsFinite(line.Start) || !double.IsFinite(line.End) || line.End <= line.Start)
        {
            return 0;
        }

        return Math.Clamp((position - line.Start) / (line.End - line.Start), 0, 1);
    }
}
