using LyricsBar.Classes.Models;

namespace LyricsBar.Classes.Services.Lyrics;

/// <summary>
/// 逐字擦亮的纯策略：把播放位置换算成当前行的已唱进度。
/// Pure syllable-highlight policy: converts playback position into sung progress for the active line.
///
/// 有真实逐字时间轴时按字推进（唯一准确的做法）；只有行级歌词时按整行时长**线性摊到整行**（v1.4.10 起），
/// 这样每个字都有"扫过去"的过程，而不是整行一次性点亮——用户看到的"有的歌词擦亮、有的不擦亮"主要就是后者。
/// A real word timeline advances character by character (the only accurate way). With line-level lyrics only, the progress
/// is spread linearly across the line's own window (since v1.4.10) so every line still sweeps instead of lighting up all at
/// once — the inconsistency users reported between songs. 长尾行（间奏、拖长尾音）按字数估一个上限，超过就直接全亮，
/// 免得扫描头慢慢爬。
/// A long tail (an instrumental gap or a held note) is capped by an estimate from the text length, so the scan head holds at
/// fully lit instead of crawling.
/// </summary>
public static class LyricHighlightPolicy
{
    /// <summary>
    /// 行级歌词合成擦亮时，每个字大约占多少秒（按下限与上限一起用）。
    /// How many seconds one character is assumed to take when the wipe is synthesised for line-level lyrics.
    /// </summary>
    public const double LineScanSecondsPerCharacter = 0.22;

    /// <summary>
    /// 合成擦亮的最短时长（秒）：一个字以内的短句也要看得见扫描过程。
    /// Shortest synthesised wipe, in seconds: even a one-word line should visibly sweep.
    /// </summary>
    public const double MinimumLineScanSeconds = 0.9;

    /// <summary>
    /// 歌词时间轴与擦亮层的呈现决策。时间轴决定长歌词如何滚动，擦亮层只是叠在同一轨迹上的视觉效果；
    /// 因此关闭擦亮不得关闭时间轴或改用另一种滚动方式。
    /// Presentation decision for the lyric timeline and highlight layer. The timeline decides how a long lyric scrolls, while the
    /// highlight is only a visual layer over that same trajectory, so hiding it must not disable the timeline or select another scroll mode.
    /// </summary>
    /// <param name="AdvanceTimeline">是否按歌词时间轴推进。/ Whether the lyric timeline advances.</param>
    /// <param name="ShowHighlight">是否显示擦亮层。/ Whether the highlight layer is shown.</param>
    public readonly record struct PresentationState(bool AdvanceTimeline, bool ShowHighlight);

    /// <summary>
    /// 分别决定时间轴是否推进、擦亮层是否显示。用户开关只参与后者，保证开关前后的滚动轨迹完全相同。
    /// Separately decides whether the timeline advances and whether the highlight layer is shown. The user switch participates only
    /// in the latter, keeping the scrolling trajectory identical on both sides of the switch.
    /// </summary>
    public static PresentationState ResolvePresentationState(
        bool highlightEnabled,
        bool hasCurrentLine,
        bool connected,
        bool playing,
        bool lyricsVisible,
        bool controlVisible,
        bool isAdvancePruned,
        bool highContrast,
        bool useContinuousMotion)
    {
        var advanceTimeline = hasCurrentLine &&
                              connected &&
                              playing &&
                              lyricsVisible &&
                              controlVisible &&
                              !isAdvancePruned &&
                              useContinuousMotion;
        return new PresentationState(
            AdvanceTimeline: advanceTimeline,
            ShowHighlight: advanceTimeline && highlightEnabled && !highContrast);
    }

    /// <summary>
    /// 计算当前行的擦亮进度。
    /// Computes the highlight progress of the active line.
    /// </summary>
    /// <param name="line">当前行 / The active line.</param>
    /// <param name="positionSeconds">播放位置（秒）/ Playback position in seconds.</param>
    /// <returns>0 到 1 的进度；该行连起止时间都没有时返回 null / Progress between 0 and 1, or null when the line has no window at all.</returns>
    public static double? ResolveProgress(LyricLine line, double positionSeconds)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!double.IsFinite(positionSeconds))
        {
            return null;
        }

        var words = CollectUsableWords(line.Words);
        if (words.Count > 0)
        {
            return ResolveWordProgress(words, positionSeconds);
        }

        return ResolveSyntheticProgress(line, positionSeconds);
    }

    /// <summary>
    /// 行级歌词的合成擦亮：没有逐字时间轴时，按整行窗口把擦亮线性摊到整行上。
    /// Synthesised wipe for line-level lyrics: without a word timeline, the progress is spread linearly across the line window.
    ///
    /// 上限按字数估算（见 <see cref="LineScanSecondsPerCharacter"/>）：间奏或拖长尾音的行不让扫描头慢慢爬，估完就保持全亮。
    /// The span is capped by an estimate from the text length (see <see cref="LineScanSecondsPerCharacter"/>): a line with an
    /// instrumental gap or a held note does not make the scan head crawl, it holds fully lit once the estimate is used up.
    /// </summary>
    private static double? ResolveSyntheticProgress(LyricLine line, double positionSeconds)
    {
        if (!double.IsFinite(line.Start))
        {
            return null;
        }

        var estimate = Math.Max(MinimumLineScanSeconds, line.Text.Length * LineScanSecondsPerCharacter);
        var span = double.IsFinite(line.End) && line.End > line.Start ? line.End - line.Start : estimate;
        var effective = Math.Min(span, estimate);
        return Math.Clamp((positionSeconds - line.Start) / effective, 0, 1);
    }

    private static List<LyricWord> CollectUsableWords(IReadOnlyList<LyricWord> words)
    {
        var usable = new List<LyricWord>(words.Count);
        foreach (var word in words)
        {
            if (!string.IsNullOrEmpty(word.Text) && word.End > word.Start)
            {
                usable.Add(word);
            }
        }

        return usable;
    }

    private static double? ResolveWordProgress(List<LyricWord> words, double positionSeconds)
    {
        var totalCharacters = 0d;
        foreach (var word in words)
        {
            totalCharacters += word.Text.Length;
        }

        if (totalCharacters <= 0)
        {
            return null;
        }

        if (positionSeconds <= words[0].Start)
        {
            return 0;
        }

        if (positionSeconds >= words[^1].End)
        {
            return 1;
        }

        var sungCharacters = 0d;
        foreach (var word in words)
        {
            if (positionSeconds >= word.End)
            {
                sungCharacters += word.Text.Length;
                continue;
            }

            if (positionSeconds > word.Start)
            {
                // 音节内部按时间线性插值：字符数权重让中英文混排的擦亮速度看起来一致。
                // Inside one syllable the progress is interpolated linearly; the character weight keeps the wipe speed
                // consistent between CJK and Latin text.
                sungCharacters += word.Text.Length * (positionSeconds - word.Start) / (word.End - word.Start);
            }

            break;
        }

        return Math.Clamp(sungCharacters / totalCharacters, 0, 1);
    }

}
