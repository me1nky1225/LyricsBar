using LyricsBar.Classes.Abstractions;
using LyricsBar.Classes.Models;
using Lyricify.Lyrics.Providers.Web.Netease;

namespace LyricsBar.Classes.Services.Lyrics;

/// <summary>
/// 网易云歌词源（来源专用）：播放器本身就是网易云时，用歌曲 id 精确取词。
/// The NetEase source provider for the NetEase player itself: it retrieves exactly by song id.
///
/// QQ 未命中后参与并发备用阶段；没有歌曲 id 时不发请求。
/// Participates in parallel fallbacks after a QQ miss, and makes no request without a song id.
/// </summary>
public sealed class NetEaseLyricsProvider : ILyricsProvider
{
    private readonly Api _api = new();

    public string SourceName => LyricsSourceCatalog.NetEase;

    public async Task<LyricsResult?> GetLyricsAsync(
        LyricsRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NetEaseSongId))
        {
            return null;
        }

        var result = await NetEaseLyricFetcher.FetchAndBuildAsync(
            _api,
            request.NetEaseSongId,
            SourceName,
            request,
            cancellationToken);
        // 按 id 取词就是这首歌本身，给满分是应当的——但前提是 id 真的属于当前曲目：
        // `NeteaseQueueService` 只在曲名完全相等、而且歌手对得上（或队列里只此一首同名曲）时才交出 id，
        // 歌手对不上、同名曲多首时一律不给，这里也就不会有"另一首歌的歌词拿满分"。
        // Fetching by id is this very song, so full marks are warranted — as long as the id really belongs to the current
        // track: `NeteaseQueueService` hands one back only on an exact title plus a confirmed artist (or a lone namesake),
        // and yields nothing on an artist mismatch or several namesakes, so no other song's lyrics can score full marks here.
        return result is null ? null : result with { MatchScore = 100 };
    }
}
