// 固定取词阶段与同分选择的纯策略；扩展顺序只改此策略/目录，不改 UI 或网络协调者。
using LyricsBar.Classes.Abstractions;
using LyricsBar.Classes.Models;

namespace LyricsBar.Classes.Services.Lyrics;

/// <summary>优先源与并发备用源的执行计划。/ Preferred-source and parallel-fallback plan.</summary>
internal sealed record LyricsRetrievalPlan(ILyricsProvider? Preferred, IReadOnlyList<ILyricsProvider> Fallbacks);

/// <summary>构造取词阶段；备用阶段优先当前播放器对应结果，再按匹配分选择。</summary>
internal static class LyricsRetrievalPolicy
{
    // 播放器自己没有对应来源时的兜底优先源（QQ 优质歌词库）。备用源并发后按分数采纳。
    // 阶段、权重和门槛属于实现策略，避免用户组合出互相冲突的调度规则；严格度只调整门槛，不调整阶段。
    // Fallback preferred source when the player has no matching source of its own (QQ Music's lyric library). Fallback
    // sources run concurrently and are adopted by score. Stages, weights, and thresholds stay implementation policy so users
    // cannot combine conflicting scheduling rules; strictness only moves the thresholds, never the stages.
    internal const string PreferredSource = LyricsSourceCatalog.QQMusic;

    // 优先源达到此分数直接结束优先阶段；低分结果保留，与备用源按真实元数据评分比较。
    // 这是"均衡"档的取值，其余档位见 LyricsMatchStrictnessPolicy。
    // The preferred source ends the preferred stage immediately at this score; a lower result is kept for comparison against
    // the fallbacks by real metadata score. This is the balanced level's value; the other levels live in
    // LyricsMatchStrictnessPolicy.
    internal const int PreferredMinimumScore = 80;

    /// <summary>
    /// 决定优先阶段先问谁。
    ///
    /// **先问"当前播放器自己的来源"**，没有对应来源时才退回 <see cref="PreferredSource"/>。
    /// 这一条是取词准确度的关键：网易云播放器有自己的来源（<c>Netease</c>，按歌曲 id 精确取词，命中即 100 分），
    /// 而 QQ 音乐只能靠"曲名 + 歌手"搜索。原先固定先问 QQ，QQ 一旦拿到 ≥80 分就直接结束，
    /// 于是"能按 id 精确命中的那一条"根本没机会跑——用户看到的就是"同一个歌手另一张专辑"或"现场版"的歌词。
    /// 播放器是 QQ 音乐时优先源本来就是它自己，所以这一条对 QQ 用户也只会更准，不会更差。
    /// Decides who is asked first in the preferred stage.
    ///
    /// **The current player's own source comes first**, falling back to <see cref="PreferredSource"/> only when the player has
    /// none. This is the key to match accuracy: the NetEase player has a source of its own (<c>Netease</c>, retrieving by song
    /// id, which scores a flat 100), whereas QQ Music can only search by title and artist. Asking QQ first meant that as soon
    /// as QQ scored 80 or more the stage ended and the exact-by-id path never ran at all — which is exactly how a user ends up
    /// looking at another album's or a live take's lyrics. When QQ Music is the player its own source is the preferred one
    /// anyway, so this can only improve things for QQ users too.
    /// </summary>
    public static LyricsRetrievalPlan Plan(
        IReadOnlyList<ILyricsProvider> providers,
        IReadOnlyList<ILyricsProvider> enabled,
        string? playbackSourceId)
    {
        var active = providers.Where(provider => enabled.Contains(provider)).ToArray();
        var preferred = active.FirstOrDefault(provider =>
                LyricsSourcePolicy.IsCurrentPlaybackSource(playbackSourceId, provider.SourceName))
            ?? active.FirstOrDefault(provider => provider.SourceName == PreferredSource);
        return new(preferred, active.Where(provider => !ReferenceEquals(provider, preferred)).ToArray());
    }

    /// <summary>
    /// 在候选里挑最终结果：当前播放器自己的来源优先，其余按匹配分从高到低，且**低于门槛的一律不要**。
    /// 最后一个条件是"匹配严格度"真正生效的地方——没有它，一个 60 分出头的勉强匹配也会照常上屏。
    /// Picks the final result: the current player's own source wins outright, the rest are ranked by match score, and anything
    /// **below the threshold is dropped entirely**. That last condition is where match strictness actually bites; without it a
    /// barely-passing 60-odd match goes to the bar just the same.
    /// </summary>
    /// <param name="results">备用源的结果。/ Fallback results.</param>
    /// <param name="preferred">优先源的结果（可能低于门槛，仍然参与比较）。/ The preferred source's result, possibly below the threshold but still compared.</param>
    /// <param name="currentPlayback">当前播放器自己的来源给出的结果。/ The result from the current player's own source.</param>
    /// <param name="minimumScore">采纳所需的最低分。/ The minimum score required for acceptance.</param>
    public static LyricsResult? Select(
        IEnumerable<LyricsResult?> results,
        LyricsResult? preferred = null,
        LyricsResult? currentPlayback = null,
        int minimumScore = 0)
    {
        var accepted = results.Prepend(preferred)
            .Where(result => result is not null && result.MatchScore >= minimumScore)
            .OrderByDescending(result => result!.MatchScore)
            .ToList();

        if (currentPlayback is { } playback && playback.MatchScore >= minimumScore)
            return playback;

        return accepted.FirstOrDefault();
    }
}
