using LyricsBar.Classes.Settings;

namespace LyricsBar.Classes.Services.Lyrics;

/// <summary>
/// 把"匹配严格度"这一个用户档位翻译成取词链真正用的两个分数门槛。
///
/// 为什么要有这一层：门槛原先散在三处硬编码（<see cref="LyricsMetadataScore.MinimumScore"/> 的通用门槛、
/// <see cref="LyricsRetrievalPolicy"/> 的优先源提前结束分、以及各来源自己传的值），用户想"宁可不显示也别显示错的"
/// 时无处可调。这一层把它们收敛成一个档位，并且**均衡档就是升级前的原值**，所以不改设置时行为逐位不变。
/// This maps the single user-facing strictness level onto the two score thresholds the retrieval chain actually uses.
///
/// Why the layer exists: the thresholds used to be hardcoded in three places — the general minimum in
/// <see cref="LyricsMetadataScore.MinimumScore"/>, the preferred source's early-exit score in
/// <see cref="LyricsRetrievalPolicy"/>, and whatever each provider passed — so a user who would rather see nothing than see
/// the wrong song had no knob. This collapses them into one level, and **the balanced level is exactly the old values**, so
/// leaving the setting alone changes nothing.
/// </summary>
public static class LyricsMatchStrictnessPolicy
{
    /// <summary>
    /// 采纳一首候选曲目所需的最低分。低于它的结果一律不采纳（宁可不显示歌词）。
    /// The minimum score a candidate track needs to be accepted; anything below it is rejected outright, so no lyrics are
    /// shown rather than the wrong ones.
    /// </summary>
    /// <param name="strictness">用户档位。/ The user's level.</param>
    public static int MinimumScore(LyricsMatchStrictness strictness) => strictness switch
    {
        LyricsMatchStrictness.Lenient => 50,
        LyricsMatchStrictness.Strict => 75,
        _ => LyricsMetadataScore.MinimumScore
    };

    /// <summary>
    /// 优先源达到这个分数就可以直接结束优先阶段、不再等其他来源。
    /// 调高它等于"多等一会儿，看看别的来源能不能给出更好的匹配"。
    /// Once the preferred source reaches this score the preferred stage ends immediately without waiting for the others.
    /// Raising it means "wait a little longer and see whether another source matches better".
    /// </summary>
    /// <param name="strictness">用户档位。/ The user's level.</param>
    public static int PreferredEarlyExitScore(LyricsMatchStrictness strictness) => strictness switch
    {
        LyricsMatchStrictness.Lenient => 70,
        LyricsMatchStrictness.Strict => 90,
        _ => LyricsRetrievalPolicy.PreferredMinimumScore
    };

    /// <summary>当前设置下的通用门槛。/ The general threshold under the current settings.</summary>
    public static int CurrentMinimumScore => MinimumScore(SettingsManager.Current.LyricsMatchStrictness);

    /// <summary>当前设置下的优先源提前结束分。/ The preferred source's early-exit score under the current settings.</summary>
    public static int CurrentPreferredEarlyExitScore => PreferredEarlyExitScore(SettingsManager.Current.LyricsMatchStrictness);
}
