namespace LyricsBar.Classes.Models;

/// <summary>
/// 当前播放状态的最小快照。原项目的同名类型携带封面、控制能力、循环模式等十余个字段；精简版只保留
/// 歌词呈现真正会读到的部分：身份（用于判断换歌）、播放位置（用于选行）与歌词文档本身。
/// The minimal now-playing snapshot. The original type of the same name carried artwork, control capabilities, repeat mode and a
/// dozen more fields; the trimmed build keeps only what lyric presentation actually reads: identity (to detect a track change),
/// playback position (to pick a line), and the lyric document itself.
/// </summary>
/// <param name="IsConnected">是否连接到媒体会话。/ Whether a media session is connected.</param>
/// <param name="IsPlaying">是否正在播放。/ Whether playback is running.</param>
/// <param name="Title">曲名。/ Track title.</param>
/// <param name="Artist">歌手。/ Artist.</param>
/// <param name="SourceId">来源标识（AppUserModelId）。/ Source identifier (AppUserModelId).</param>
/// <param name="SourceName">来源显示名。/ Source display name.</param>
/// <param name="Position">上次时间轴更新时的播放位置（秒）。/ Playback position in seconds at the last timeline update.</param>
/// <param name="Duration">曲目总时长（秒），未知为 0。/ Track duration in seconds; 0 when unknown.</param>
/// <param name="PlaybackRate">播放速率，用于在两次更新之间外推位置。/ Playback rate, used to extrapolate the position between updates.</param>
/// <param name="TimelineUpdatedAt">时间轴对应的本地时刻。/ Local time the timeline belongs to.</param>
/// <param name="Lyrics">已取到的歌词；未取到为 null。/ The retrieved lyrics; null when none were retrieved.</param>
/// <param name="CoverArt">
/// 封面的 data URI（已缩小并转成 JPEG）；没有封面时为空串。
/// 放在快照里而不是让界面自己去读，是因为封面属于"当前播放曲目"的一部分：换歌时它和曲名一起失效，
/// 由读取器按曲目缓存一份，界面只负责转发给 Web 端。
/// Data URI of the artwork (already downscaled and encoded as JPEG); empty when there is none. It rides in the snapshot rather
/// than being read by the UI because artwork belongs to the current track: it goes stale with the title on a track change, so
/// the reader caches one per track and the UI only forwards it to the web side.
/// </param>
/// <param name="LyricsLookupCompleted">
/// 这一首的取词是否已经结束。必须与"歌词为 null"分开：取词进行中与"确认没有歌词"都是 null，
/// 而这两种情况在界面上要给出完全不同的东西——前者什么都不显示，后者显示曲名与歌手（纯音乐就是这么处理的）。
/// Whether the lyric lookup for this track has finished. It has to be distinct from "lyrics are null": both an in-flight lookup
/// and a confirmed "no lyrics" are null, yet they must render completely differently — nothing for the former, title and artist
/// for the latter (which is how instrumental tracks are handled).
/// </param>
/// <param name="CanSkipNext">播放器是否允许"下一首"。按钮据此置灰，避免点不动却看不出原因。/ Whether the player allows skipping forward.</param>
/// <param name="CanSkipPrevious">播放器是否允许"上一首"。/ Whether the player allows skipping backward.</param>
/// <param name="CanPlayPause">播放器是否允许"播放/暂停"切换。/ Whether the player allows toggling play/pause.</param>
public sealed record MediaSnapshot(
    bool IsConnected,
    bool IsPlaying,
    string Title,
    string Artist,
    string SourceId,
    string SourceName,
    double Position,
    double Duration,
    double PlaybackRate,
    DateTimeOffset TimelineUpdatedAt,
    LyricsResult? Lyrics,
    string CoverArt = "",
    bool LyricsLookupCompleted = false,
    bool CanSkipNext = false,
    bool CanSkipPrevious = false,
    bool CanPlayPause = false)
{
    /// <summary>断开状态的快照：没有可用媒体会话。/ Disconnected snapshot: no media session available.</summary>
    public static MediaSnapshot Disconnected { get; } = new(
        false,
        false,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        0,
        0,
        1,
        DateTimeOffset.MinValue,
        null);
}
