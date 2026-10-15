using LyricsBar.Classes.Models;

namespace LyricsBar.Classes.Services;

/// <summary>
/// 播放位置推算。原项目把这个方法挂在 <c>TaskbarExperiencePolicy</c>（一个八百多行的任务栏布局策略）上；精简版只用到
/// 这一个方法，于是单独留在这里，让移植过来的 <c>LyricsPresentationProjector</c> 保持零改动。
/// Playback-position extrapolation. The original hung this method on <c>TaskbarExperiencePolicy</c>, an eight-hundred-line
/// taskbar layout policy; the trimmed build needs only this one method, so it lives here alone and the ported
/// <c>LyricsPresentationProjector</c> stays untouched.
/// </summary>
public static class TaskbarExperiencePolicy
{
    /// <summary>
    /// 取当前播放位置：以上次时间轴为基准，按播放速率外推到"现在"。
    /// 时长已知时把结果夹在曲目区间内；时长未知（播放器不上报）时只保证非负，不做上夹——
    /// 网易云音乐这类播放器的时长恒为 0，若沿用"时长为 0 就返回 0"的老规则，位置会被永久钉在 0，
    /// 行下标永远是 -1，歌词再取到也显示不出来。位置本身由 <c>SmtcMediaReader</c> 的本地计时兜底给出。
    /// Resolves the current playback position by extrapolating from the last timeline update at the playback rate. When the
    /// duration is known the result is clamped to the track range; when it is unknown the result is only kept non-negative
    /// and not upper-clamped. Players such as NetEase Cloud Music report a permanent duration of 0, and the older
    /// "return 0 when the duration is 0" rule pinned the position there forever, so the line index stayed at -1 and retrieved
    /// lyrics could never be shown. The position itself comes from the reader's local-clock fallback.
    /// </summary>
    /// <param name="snapshot">媒体快照。/ The media snapshot.</param>
    /// <param name="now">当前本地时刻。/ The current local time.</param>
    public static double GetPosition(MediaSnapshot snapshot, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var position = snapshot.Position;
        if (snapshot.IsPlaying && snapshot.TimelineUpdatedAt > DateTimeOffset.UnixEpoch)
        {
            var elapsed = Math.Max(0, (now - snapshot.TimelineUpdatedAt).TotalSeconds);
            position += elapsed * (snapshot.PlaybackRate <= 0 ? 1 : snapshot.PlaybackRate);
        }

        return snapshot.Duration > 0
            ? Math.Clamp(position, 0, snapshot.Duration)
            : Math.Max(0, position);
    }
}
