using LyricsBar.Classes.Models;

namespace LyricsBar.Classes.Services.Lyrics;

/// <summary>
/// 曲目时长：播放器报了就用播放器的，播放器不报（网易云音乐的 Position 与 Duration 恒为 0）就用歌词末行推算。
/// Track duration: the player's reported value when there is one, otherwise inferred from the last lyric line —
/// the only source available on players such as NetEase Cloud Music that report a permanent zero timeline.
///
/// 进度条需要一把"整首歌多长"的尺子：它必须 > 0 才有刻度可言。只认播放器上报的时长时，网易云音乐的进度条
/// 永远不出现（<see cref="Views.TaskbarLyricsWindow"/> 的 <c>UpdateProgress</c> 直接以 <c>Duration &gt; 0</c> 为显示条件），
/// 用户看到的就是"设置里开了进度条但没有任何效果"。
/// A progress bar needs a "how long is the whole song" ruler that is greater than zero to have a scale at all. Trusting only
/// the reported duration makes the bar never appear on NetEase Cloud Music (the window's <c>UpdateProgress</c> gates visibility
/// on <c>Duration &gt; 0</c>), which the user sees as "the progress-bar switch does nothing".
///
/// 歌词末行结束时间在任何播放器上都拿得到，它比真实曲长略短（末行唱完到曲末通常还有一段尾奏），作为进度刻度足够准；
/// 播放器报了时长时一律以播放器的为准，所以其他播放器的行为不受影响。
/// The last lyric line's end is available on every player and is slightly shorter than the real track (an outro usually follows
/// the last line), which is accurate enough as a progress scale; whenever the player reports a duration that value wins, so no
/// other player's behaviour changes.
/// </summary>
public static class LyricsDurationPolicy
{
    /// <summary>
    /// 取用于进度刻度的曲目时长（秒）；既没有上报时长也没有歌词时返回 0，调用方据此隐藏进度条。
    /// Resolves the track duration in seconds used as the progress scale; returns 0 when neither the player nor the lyrics
    /// provide one, which is how the caller decides to hide the bar.
    /// </summary>
    /// <param name="snapshot">当前媒体快照。/ The current media snapshot.</param>
    public static double Resolve(MediaSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (double.IsFinite(snapshot.Duration) && snapshot.Duration > 0)
        {
            return snapshot.Duration;
        }

        var lines = snapshot.Lyrics?.Document.Lines;
        if (lines is not { Count: > 0 })
        {
            return 0;
        }

        // 末行结束时间已经由解析器补齐（下一行起点 / 曲目时长 / 固定尾部），所以这里不再补任何尾部时长。
        // The last line's end has already been completed by the parser (next line's start, track duration, or a fixed tail),
        // so no extra tail is added here.
        var end = lines[^1].End;
        return double.IsFinite(end) && end > 0 ? end : 0;
    }
}
