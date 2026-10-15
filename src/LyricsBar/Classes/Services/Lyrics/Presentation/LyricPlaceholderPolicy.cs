namespace LyricsBar.Classes.Services.Lyrics;

/// <summary>
/// 歌词占位文本的识别：来源用一行字表示"这首歌没有歌词"，那种行不是歌词，不能占着静置层的歌词位置。
/// Recognition of placeholder lyric text: a source sometimes answers with a single line that means "this song has no lyrics",
/// and such a line is not a lyric and must not take up the rest layer's lyric slot.
///
/// 实测（2026-09-17）：网易云两个端点在无词曲目上都返回 `[00:00.00]暂无歌词`，它会带着时间戳通过解析，于是静置层会把它
/// 当成唯一一句歌词显示出来。识别按标记词做，并要求整行就是一个标记（去掉空白与首尾标点后相等），或标记词之外只剩极少字符：
/// 真正的歌词偶尔也会出现"歌词"二字，但不会短到只剩一句"暂无歌词"。
/// Measured on 2026-09-17: both NetEase endpoints answer `[00:00.00]暂无歌词` for a track without lyrics, which passes parsing
/// with a timestamp and would then be shown as the only lyric line. Detection works on marker phrases and requires either that
/// the whole line is one marker (equal after whitespace and edge punctuation are removed) or that no more than
/// <see cref="MaximumTrailingCharacters"/> characters are left over beside the marker: real lyrics may mention the word "歌词",
/// but never as a line as short as "暂无歌词".
/// </summary>
public static class LyricPlaceholderPolicy
{
    /// <summary>整行含有标记词时，标记之外还允许剩下的字符数；超过它的行不再按占位文本处理。
    /// Maximum number of characters allowed to remain beside a marker for a line containing one to still count as a
    /// placeholder; lines with more left over are left alone.</summary>
    public const int MaximumTrailingCharacters = 2;

    private static readonly string[] Markers =
    [
        "暂无歌词",
        "暫無歌詞",
        "暂无歌词。",
        "该歌曲暂无歌词",
        "該歌曲暫無歌詞",
        "此歌曲暂无歌词",
        "歌词加载中",
        "歌詞載入中",
        "歌词制作中",
        "純音樂，請欣賞",
        "纯音乐，请欣赏",
        "纯音乐请欣赏",
        "此歌曲为没有填词的纯音乐",
        "此歌曲為沒有填詞的純音樂",
        "该歌曲为纯音乐",
        "該歌曲為純音樂",
        "此歌曲为纯音乐",
        "此歌曲為純音樂",
        "该歌曲为没有填词的纯音乐",
        "該歌曲為沒有填詞的純音樂",
        "这首歌是纯音乐",
        "這首歌是純音樂",
        "这是一首纯音乐",
        "這是一首純音樂",
        "没有歌词",
        "沒有歌詞",
        "无歌词",
        "無歌詞",
        "暂无歌词信息",
        "暫無歌詞信息",
        "暂无歌词内容",
        "暫無歌詞內容",
        "instrumental",
        "no lyrics",
        "lyrics not available",
        "no lyrics available",
        "lyrics unavailable"
    ];

    /// <summary>
    /// 判断一行文本是否为"没有歌词"的占位。
    /// Decides whether one line of text is a "no lyrics" placeholder.
    /// </summary>
    /// <param name="text">行文本 / Line text.</param>
    /// <returns>是占位文本时为 true / True when the text is a placeholder.</returns>
    public static bool IsPlaceholder(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var normalized = Normalize(text);
        if (normalized.Length == 0)
        {
            return true;
        }

        foreach (var marker in Markers)
        {
            if (normalized.Equals(Normalize(marker), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 标记词之外还剩多少字符，比"整行多长"更能说明这一行是不是占位：
        // 「纯音乐，请欣赏这一首歌」也是一句实话，它的长度为 11、能塞进原来那条 24 字符的短行规则里，于是整句被当成占位丢掉，
        // 而按"标记外残留"判断，它在「纯音乐请欣赏」之后还剩 5 个字，明显不是占位。
        // How much is left BESIDE the marker says more about a line than how long the line is: 「纯音乐，请欣赏这一首歌」 is a real
        // sentence whose length of 11 fits the old 24-character short-line rule, so the whole line was dropped as a placeholder,
        // whereas by leftover characters it keeps 5 characters after 「纯音乐请欣赏」 and is plainly not one.
        var required = normalized.Length - MaximumTrailingCharacters;
        foreach (var marker in Markers)
        {
            var shape = Normalize(marker);
            if (shape.Length >= required
                && normalized.Contains(shape, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 去掉空白与首尾标点，便于按内容比较。
    /// Removes whitespace and edge punctuation so the comparison is about content.
    /// </summary>
    private static string Normalize(string text) => text
        .Trim()
        .Trim('（', '）', '(', ')', '[', ']', '【', '】', '《', '》', '，', ',', '。', '.', '、', '~', '～', '-', '—', ':', '：', ';', '；')
        .Replace(" ", string.Empty, StringComparison.Ordinal)
        .Replace("\u3000", string.Empty, StringComparison.Ordinal)
        .Replace("\t", string.Empty, StringComparison.Ordinal)
        .Trim();
}
