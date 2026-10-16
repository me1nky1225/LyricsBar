namespace LyricsBar.Classes.Services.Lyrics.Presentation;

/// <summary>
/// 曲目信息块宽度的取值规则。自动模式沿用原来的上限（<see cref="AutoMaximumDip"/>），手动模式允许更宽，
/// 因为手动定宽的目的正是"把栏宽钉死，不让长短曲名把整条栏推来推去"，上限太低就钉不住了。
/// Value rules for the track-info block's width. The automatic mode keeps the original ceiling
/// (<see cref="AutoMaximumDip"/>), while the manual mode allows more: the whole point of a manual width is to pin the bar down
/// so that long and short titles stop resizing it, and a low ceiling would defeat that.
/// </summary>
public static class LyricsTrackInfoWidthPolicy
{
    /// <summary>自动宽度的上限（DIP）。/ Ceiling of the automatic width in DIP.</summary>
    public const double AutoMaximumDip = 160;

    /// <summary>手动宽度的下限（DIP）：再窄就只剩两三个字，没有可用性。/ Floor of the manual width in DIP; anything narrower shows two or three characters and is unusable.</summary>
    public const double MinimumDip = 40;

    /// <summary>手动宽度的上限（DIP）。/ Ceiling of the manual width in DIP.</summary>
    public const double MaximumDip = 400;

    /// <summary>
    /// 归一化手动宽度：非有限值或 &lt;= 0 都当作"自适应"（返回 0），其余夹到合法区间。
    /// Normalizes a manual width: a non-finite or &lt;= 0 value means "automatic" (returns 0), anything else is clamped.
    /// </summary>
    public static double NormalizeManual(double value)
    {
        if (!double.IsFinite(value) || value <= 0)
            return 0;

        return Math.Clamp(value, MinimumDip, MaximumDip);
    }
}
