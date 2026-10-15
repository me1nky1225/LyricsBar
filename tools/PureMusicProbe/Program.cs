using LyricsBar.Classes.Models;
using LyricsBar.Classes.Services.Lyrics;
using LyricsBar.Classes.Settings;

// 纯音乐 / 无歌词时的呈现探针：把「这一首没有歌词」的各种组合直接投影成帧，看任务栏最终会得到什么。
// Probe for the "pure music / no lyrics" presentation: projects each combination straight into a frame so we can see
// what the taskbar is actually handed.
var now = new DateTimeOffset(2026, 10, 15, 22, 0, 0, TimeSpan.FromHours(8));
var failures = new List<string>();
var index = 0;

MediaSnapshot Snapshot(bool lookupCompleted, LyricsResult? lyrics) => new(
    IsConnected: true,
    IsPlaying: true,
    Title: "Bloom of Youth",
    Artist: "清水淳一",
    SourceId: "cloudmusic.exe",
    SourceName: "网易云音乐",
    Position: 30,
    Duration: 200,
    PlaybackRate: 1,
    TimelineUpdatedAt: now,
    Lyrics: lyrics,
    CoverArt: "data:image/jpeg;base64,AAAA",
    LyricsLookupCompleted: lookupCompleted,
    CanSkipNext: true,
    CanSkipPrevious: true,
    CanPlayPause: true);

var confirmedNoLyrics = (LyricsResult?)LyricsResult.NoLyrics("LRCLIB", 80);
var emptyDocument = (LyricsResult?)new LyricsResult("Netease", LyricDocument.Empty);

void Show(string name, AppSettings settings, MediaSnapshot snapshot, bool expectVisible, string expectPrimary)
{
    index++;
    var frame = LyricsPresentationProjector.Project(snapshot, settings, now);
    var primary = frame.Current;
    var ok = frame.IsVisible == expectVisible && primary == expectPrimary;
    var trackInfo = settings.LyricsTrackInfoBlockEnabled && snapshot.Title.Trim().Length > 0 ? "有曲目信息块" : "无曲目信息块";
    Console.WriteLine(
        $"{index,2}. {name,-46} IsVisible={frame.IsVisible,-5} 主行=\"{primary}\" 第二行=\"{frame.Next}\" 这块={trackInfo} 期望={(expectVisible ? (expectPrimary.Length == 0 ? "显示空行（留栏）" : "显示「" + expectPrimary + "」") : "隐藏")} {(ok ? "OK" : "  <<< 不符合预期")}");
    if (!ok)
    {
        failures.Add(name);
    }
}

Console.WriteLine("=== 纯音乐 / 无歌词的呈现矩阵 ===");
Console.WriteLine();

var defaults = new AppSettings();

// 1) 当前设置：曲目信息块开着 + 「无歌词时显示曲名与歌手」开着。
//    曲名不再被顶到歌词行上（它由信息块显示），但栏必须留着，否则封面与信息块会被一起淡出。
//    With the track-info block on, the title/artist path is not promoted onto a lyric row (the block shows them), but the bar
//    has to stay so the artwork and the block are not faded out with it.
Show(
    "曲目信息块=开, 无歌词兜底=开（当前设置）",
    defaults,
    Snapshot(true, confirmedNoLyrics),
    expectVisible: true,
    expectPrimary: "");

// 2) 关掉曲目信息块，兜底路径生效。
Show(
    "曲目信息块=关, 无歌词兜底=开",
    new AppSettings { LyricsTrackInfoBlockEnabled = false },
    Snapshot(true, confirmedNoLyrics),
    expectVisible: true,
    expectPrimary: "Bloom of Youth");

// 3) 用户关掉兜底开关：不再把曲名顶到歌词行上，但栏照旧留着（只剩封面）。
//    With the fallback switch off, the title is no longer promoted onto a lyric row, but the bar still stays (artwork only).
Show(
    "曲目信息块=关, 无歌词兜底=关（只留封面）",
    new AppSettings { LyricsTrackInfoBlockEnabled = false, LyricsShowTrackInfoWhenNoLyrics = false },
    Snapshot(true, confirmedNoLyrics),
    expectVisible: true,
    expectPrimary: "");

// 4) 取词还在进行中（歌词为 null、lookup 未完成）：也留栏。
//    整条栏在"这一首到底有没有歌词"还没结论时消失一下再出现，正是用户看到的"有些显示有些不显示"。
//    A lookup still in flight also keeps the bar. Fading the whole bar out and back in while the answer is still unknown is
//    exactly the "some show, some don't" flicker the user reported.
Show(
    "取词进行中（曲目信息块=关，留栏）",
    new AppSettings { LyricsTrackInfoBlockEnabled = false },
    Snapshot(false, lyrics: null),
    expectVisible: true,
    expectPrimary: "");

// 5) 取词完成但来源给的是空文档（不是 NoLyrics 标记，但一行都没有）。
Show(
    "取词完成, 空文档（曲目信息块=关）",
    new AppSettings { LyricsTrackInfoBlockEnabled = false },
    Snapshot(true, emptyDocument),
    expectVisible: true,
    expectPrimary: "Bloom of Youth");

// 6) 连曲名都没有。
Show(
    "无曲名（曲目信息块=关）",
    new AppSettings { LyricsTrackInfoBlockEnabled = false },
    Snapshot(true, confirmedNoLyrics) with { Title = "   " },
    expectVisible: false,
    expectPrimary: "");

// 7) 关掉第二行时只出曲名，不带歌手。
Console.WriteLine();
Console.WriteLine("=== 第二行开关的影响 ===");
var noBlock = new AppSettings { LyricsTrackInfoBlockEnabled = false };
var pairFrame = LyricsPresentationProjector.Project(Snapshot(true, confirmedNoLyrics), noBlock, now);
Console.WriteLine($"   第二行=开 → 曲名=\"{pairFrame.Current}\" 第二行=\"{pairFrame.Next}\"");
var singleFrame = LyricsPresentationProjector.Project(
    Snapshot(true, confirmedNoLyrics),
    new AppSettings { LyricsTrackInfoBlockEnabled = false, TwoLineLyricsEnabled = false },
    now);
Console.WriteLine($"   第二行=关 → 曲名=\"{singleFrame.Current}\" 第二行=\"{singleFrame.Next}\"");

// 8) 网易云占位行是否真的会在解析阶段被丢掉。
Console.WriteLine();
Console.WriteLine("=== 占位文本的解析过滤 ===");
foreach (var placeholder in new[] { "纯音乐，请欣赏", "純音樂，請欣賞", "暂无歌词", "此歌曲为没有填词的纯音乐", "该歌曲暂无歌词", "Instrumental", "该歌曲为纯音乐", "該歌曲為純音樂", "此歌曲為純音樂", "这首歌是纯音乐", "暂无歌词信息", "没有歌词" })
{
    var raw = $"[00:00.00]{placeholder}";
    var document = LyricsTextParser.Parse(raw);
    var verdict = document.Lines.Count == 0 ? "被丢弃（好）" : $"残留 {document.Lines.Count} 行: \"{document.Lines[0].Text}\"  <<<";
    Console.WriteLine($"   \"{placeholder}\" → {verdict}");
    if (document.Lines.Count != 0)
    {
        failures.Add($"占位未过滤: {placeholder}");
    }
}

// 9) 真正的一句歌词不能被误杀，带装饰的占位行仍要被拦下。
var lyricDocument = LyricsTextParser.Parse("[00:01.00]今天也想见到你\n[00:06.00]纯音乐，请欣赏这一首歌\n");
Console.WriteLine($"   含占位词的长歌词行 → {(lyricDocument.Lines.Count == 2 ? "两行都保留（好）" : $"只剩 {lyricDocument.Lines.Count} 行  <<<")}");
if (lyricDocument.Lines.Count != 2)
{
    failures.Add("含占位词的真歌词被误杀");
}

var decoratedDocument = LyricsTextParser.Parse("[00:00.00]（纯音乐，请欣赏）\n[00:01.00]【暂无歌词】\n");
Console.WriteLine($"   带括号/书名号的占位行 → {(decoratedDocument.Lines.Count == 0 ? "两行都被丢弃（好）" : $"残留 {decoratedDocument.Lines.Count} 行  <<<")}");
if (decoratedDocument.Lines.Count != 0)
{
    failures.Add("带装饰的占位行未被过滤");
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("RESULT: PASS");
    return 0;
}

Console.WriteLine($"RESULT: FAIL（{failures.Count} 项）");
foreach (var failure in failures)
{
    Console.WriteLine($"   - {failure}");
}

return 1;
