namespace LyricsBar.Classes.Services.Lyrics.Presentation;

/// <summary>
/// 指针落在栏里哪一块上的判定。
///
/// 栏从左到右是「内边距 | 封面 | 间距 | 曲目信息 | 间距 | 歌词 | 内边距」，判定只需要一个分界点：
/// 封面与曲目信息那一块从哪里结束。分界点左侧（含左侧内边距）算"信息块"，右侧（含右侧内边距）算"歌词"。
/// 这么切是有意的：两侧内边距只有几 DIP，指针停在那里时用户实际上是在看那一边的内容，
/// 与其判成"哪一块都不是"，不如就近归给相邻的那一块，悬停效果才不会有死区。
///
/// 判定写成纯函数是为了能直接断言：它不该依赖窗口、指针设备或设置管理器，探针里给几个宽度就能验完。
/// Deciding which part of the bar the pointer is over.
///
/// Left to right the bar is "inline padding | artwork | gap | track info | gap | lyrics | inline padding", so the decision needs
/// exactly one boundary: where the artwork-and-track-info part ends. Everything left of it (inline padding included) counts as
/// the info part, everything right of it (inline padding included) as the lyrics part. That split is deliberate: the inline
/// paddings are only a few DIP wide, and a pointer resting on one of them means the user is looking at that side's content, so
/// attributing it to the neighbouring part beats declaring it "neither" and leaving a dead zone with no hover effect.
///
/// The decision is a pure function so it can be asserted directly: it must not depend on a window, a pointing device or the
/// settings manager, and a probe can verify it by handing over a few widths.
/// </summary>
public static class LyricsHoverRegionPolicy
{
    /// <summary>
    /// 封面与曲目信息那一块的总宽度（DIP），与宿主的栏宽计算用同一套footprint：
    /// 封面占「边长 + 间距」，曲目信息占「宽度 + 间距」，两者各自被关掉时贡献 0。
    /// Total width of the artwork-and-track-info part in DIP, using the same footprints the host's bar-width calculation uses:
    /// the artwork takes "edge + gap" and the track-info block "width + gap", each contributing zero when it is switched off.
    /// </summary>
    public static double ResolveInfoPartWidthDip(
        bool coverEnabled,
        double coverSizeDip,
        double coverGapDip,
        double trackInfoWidthDip,
        double trackInfoGapDip)
    {
        var cover = coverEnabled && double.IsFinite(coverSizeDip) && coverSizeDip > 0
            ? coverSizeDip + Math.Max(0, coverGapDip)
            : 0;

        var trackInfo = double.IsFinite(trackInfoWidthDip) && trackInfoWidthDip > 0
            ? trackInfoWidthDip + Math.Max(0, trackInfoGapDip)
            : 0;

        return cover + trackInfo;
    }

    /// <summary>
    /// 判定指针在哪一块上。
    /// </summary>
    /// <param name="pointerOffsetDip">
    /// 指针相对"内容起点"的位置（DIP），也就是扣掉左右内边距之后的横向坐标：内容起点为 0。
    /// The pointer's position in DIP relative to the content's left edge, i.e. the horizontal coordinate once the inline padding
    /// is subtracted, with the content's start at zero.
    /// </param>
    /// <param name="infoPartWidthDip">封面与曲目信息那一块的总宽度（DIP）；0 表示这一块不存在。/ Total width of the info part in DIP; 0 means the part does not exist.</param>
    /// <returns>指针所在的块；两块都不存在时也返回 <see cref="LyricsOverlayRegion.Lyrics"/>，因为那时整条栏画的都是歌词。/ The part under the pointer; when neither part exists it still returns <see cref="LyricsOverlayRegion.Lyrics"/>, because then the whole bar is lyrics.</returns>
    public static LyricsOverlayRegion Resolve(double pointerOffsetDip, double infoPartWidthDip)
    {
        if (!double.IsFinite(pointerOffsetDip))
            return LyricsOverlayRegion.None;

        // 信息块不存在时没有别的可能：整条栏都是歌词那一块。
        // With no info part there is nothing else it could be: the whole bar is the lyrics part.
        if (!double.IsFinite(infoPartWidthDip) || infoPartWidthDip <= 0)
            return LyricsOverlayRegion.Lyrics;

        return pointerOffsetDip <= infoPartWidthDip ? LyricsOverlayRegion.Info : LyricsOverlayRegion.Lyrics;
    }
}
