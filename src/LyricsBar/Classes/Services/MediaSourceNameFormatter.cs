using System.IO;

namespace LyricsBar.Classes.Services;

/// <summary>
/// 把 SMTC 来源标识（AppUserModelId）格式化成用户能看懂的名称。原项目走多语言文案键；精简版界面固定简体中文，
/// 于是直接写中文名。
/// Formats a SMTC source identifier (AppUserModelId) into a name a user can read. The original went through localization keys;
/// the trimmed build is Simplified Chinese only, so the names are written out directly.
/// </summary>
public static class MediaSourceNameFormatter
{
    private const string UnknownSourceName = "未知播放器";

    /// <summary>网易云音乐的显示名。内存位置通道按这个名字分流。/ NetEase Cloud Music's display name, which routes the memory-position channel.</summary>
    private const string NetEaseSourceName = "网易云音乐";

    /// <summary>来源标识的匹配令牌，按顺序匹配，命中即返回。/ Matching tokens, tried in order; the first hit wins.</summary>
    private static readonly (string Name, string[] Tokens)[] SourceNames =
    [
        (NetEaseSourceName, ["cloudmusic", "netease", "163music"]),
        ("QQ 音乐", ["qqmusic"]),
        ("酷狗音乐", ["kugou", "kgmusic"]),
        ("汽水音乐", ["sodamusic", "soda"]),
        ("Spotify", ["spotify"]),
        ("Google Chrome", ["chrome"]),
        ("Microsoft Edge", ["msedge", "microsoftedge"]),
        ("Firefox", ["firefox"]),
        ("VLC", ["vlc"]),
        ("PotPlayer", ["potplayer", "daum"]),
        ("Windows Media Player", ["zunemusic", "media.player", "wmplayer"]),
        ("mpv", ["mpv"]),
        ("foobar2000", ["foobar"])
    ];

    /// <summary>
    /// 把来源标识规范化为稳定的显示名；无法识别时回退到标识本身的可读部分，再回退到"未知播放器"。
    /// Normalizes a source identifier into a stable display name, falling back to the readable part of the identifier and then
    /// to "未知播放器".
    /// </summary>
    /// <param name="sourceId">来源应用标识。/ Source application identifier.</param>
    public static string GetDisplayName(string? sourceId)
    {
        var value = sourceId?.Trim() ?? string.Empty;
        foreach (var (name, tokens) in SourceNames)
        {
            if (tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase)))
                return name;
        }

        if (string.IsNullOrWhiteSpace(value))
            return UnknownSourceName;

        // AUMID 形如 "C:\...\cloudmusic.exe!App"，取 '!' 之后或去掉扩展名后才是可读部分。
        var bangIndex = value.LastIndexOf('!');
        if (bangIndex >= 0 && bangIndex < value.Length - 1)
            value = value[(bangIndex + 1)..];

        value = Path.GetFileNameWithoutExtension(value);
        var packageIndex = value.IndexOf('_');
        if (packageIndex > 0)
            value = value[..packageIndex];

        return string.IsNullOrWhiteSpace(value) ? UnknownSourceName : value;
    }

    /// <summary>
    /// 判断来源是不是网易云音乐。内存位置通道只对网易云开放：别的播放器都老实上报 SMTC 时间轴，不需要也不该去读它们的内存。
    /// Reports whether the source is NetEase Cloud Music. The memory-position channel is opened for NetEase only: every other
    /// player reports a real SMTC timeline, so there is no reason to read their memory.
    /// </summary>
    /// <param name="sourceId">来源应用标识。/ Source application identifier.</param>
    public static bool IsNetEase(string? sourceId) =>
        string.Equals(GetDisplayName(sourceId), NetEaseSourceName, StringComparison.Ordinal);
}
