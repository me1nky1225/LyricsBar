namespace LyricsBar.Resources;

/// <summary>
/// 文案表。原项目走 .resx 多语言资源；精简版界面语言固定为简体中文，于是退化成一个字典。
/// 保留 <c>Get(key)</c> 这个签名，是为了让移植过来的 <c>LyricsSourceCatalog</c> 不必改动。
/// The text table. The original used .resx resources for four interface languages; the trimmed build is Simplified Chinese
/// only, so it degenerates into a dictionary. The <c>Get(key)</c> signature is kept so the ported <c>LyricsSourceCatalog</c>
/// needs no edits.
/// </summary>
public static class Translations
{
    private static readonly Dictionary<string, string> Table = new(StringComparer.Ordinal)
    {
        ["Lyrics.Source.Unknown"] = "未知来源",
        ["Lyrics.Source.Netease"] = "网易云音乐",
        ["Lyrics.Source.NeteaseSearch"] = "网易云搜索",
        ["Lyrics.Source.LRCLIB"] = "LRCLIB",
        ["Lyrics.Source.QQMusic"] = "QQ 音乐",
        ["Lyrics.Source.Kugou"] = "酷狗音乐",
        ["Lyrics.Source.SodaMusic"] = "汽水音乐"
    };

    /// <summary>
    /// 取一条文案；键不存在时原样返回键，与原项目行为一致——界面显示原始键好过显示一片空白。
    /// Looks a string up; an unknown key returns the key itself, matching the original behaviour, because showing a raw key
    /// beats showing nothing.
    /// </summary>
    /// <param name="key">文案键。/ Text key.</param>
    public static string Get(string key) =>
        string.IsNullOrEmpty(key) || !Table.TryGetValue(key, out var text) ? key ?? string.Empty : text;
}
