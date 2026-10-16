using LyricsBar.Classes.Models;
using LyricsBar.Classes.Services.Lyrics;
using LyricsBar.Classes.Settings;

// 选行迟滞探针：证明"位置抖动不会把行号和擦亮位置往回带"，同时证明真回拖与换歌仍然立刻生效。
// Probe for line-selection hysteresis: proves that a jittery position cannot drag the line index or the scan position
// backwards, while a real rewind and a track change still take effect at once.
var failures = new List<string>();
var checks = 0;

void Check(string name, bool ok, string detail)
{
    checks++;
    Console.WriteLine($"{(ok ? "[ok]  " : "[FAIL]")} {name,-52} {detail}");
    if (!ok)
    {
        failures.Add(name);
    }
}

static LyricLine Line(double start, double end, string text) => new(start, end, text);

var lines = new List<LyricLine>
{
    Line(0, 5, "第一行"),
    Line(5, 10, "第二行"),
    Line(10, 15, "第三行"),
    Line(15, 20, "第四行")
};

Console.WriteLine("=== 选行迟滞自检（LyricLineCursor）===");
Console.WriteLine();

// 0) 容差常量没被顺手改掉：它同时决定"抖动多大算抖动"和"回拖多大算回拖"。
Check(
    "容差常量 = 0.6 秒",
    Math.Abs(LyricLineCursor.BackwardSeekToleranceSeconds - 0.6) < 0.0001,
    $"BackwardSeekToleranceSeconds={LyricLineCursor.BackwardSeekToleranceSeconds}");

// 1) 干净前进：位置正常推进时行号照跟，不受迟滞影响。
var cursor = new LyricLineCursor();
var at05 = cursor.Resolve("A", lines, 0.5);
var at55 = cursor.Resolve("A", lines, 5.5);
var at105 = cursor.Resolve("A", lines, 10.5);
Check(
    "正常前进：0.5→0、5.5→1、10.5→2",
    at05.Index == 0 && at55.Index == 1 && at105.Index == 2,
    $"得到 {at05.Index} / {at55.Index} / {at105.Index}");

// 2) 行内抖动：位置掉回本行起点之前一点（9.9 秒，本行起点 10 秒），行号不许回退。
var jitter = cursor.Resolve("A", lines, 9.9);
Check(
    "行内抖动（10.5→9.9）行号不回退",
    jitter.Index == 2,
    $"得到 {jitter.Index}（纯函数会给 {LyricLineIndexPolicy.FindIndex(lines, 9.9)}）");

// 3) 容差内回退（9.5 秒，距行首 0.5 秒 < 0.6 秒）仍算抖动。
var insideTolerance = cursor.Resolve("A", lines, 9.5);
Check(
    "容差内回退（9.5 秒）仍不回退",
    insideTolerance.Index == 2,
    $"得到 {insideTolerance.Index}");

// 4) 擦亮位置在同一行内只增不减。
var scan1 = cursor.Resolve("A", lines, 10.5).ScanSeconds;
var scan2 = cursor.Resolve("A", lines, 10.9).ScanSeconds;
var scan3 = cursor.Resolve("A", lines, 10.2).ScanSeconds;
Check(
    "同一行内擦亮位置只增不减",
    scan1 <= scan2 && Math.Abs(scan3 - scan2) < 0.0001,
    $"{scan1:0.0} → {scan2:0.0} → （位置掉到 10.2）{scan3:0.0}");

// 5) 真回拖（9.3 秒，距行首 0.7 秒 > 0.6 秒）：行号与擦亮位置一起让路。
var seekBack = cursor.Resolve("A", lines, 9.3);
Check(
    "真回拖（9.3 秒）立即让路",
    seekBack.Index == 1 && Math.Abs(seekBack.ScanSeconds - 9.3) < 0.0001,
    $"得到 行号={seekBack.Index}，擦亮位置={seekBack.ScanSeconds:0.0}");

// 6) 回拖到开头：行号跟到第 1 行，擦亮位置归位到当前帧（不留在被拖走的位置上）。
var rewindToStart = cursor.Resolve("A", lines, 0.1);
Check(
    "回拖到开头：行号跟到 0、擦亮位置归位",
    rewindToStart.Index == 0 && Math.Abs(rewindToStart.ScanSeconds - 0.1) < 0.0001,
    $"得到 行号={rewindToStart.Index}，擦亮位置={rewindToStart.ScanSeconds:0.0}");

// 7) 位置早于首行（前奏）时不做任何迟滞，行号就是 -1。
var intro = new LyricLineCursor();
var introAt = intro.Resolve("A", lines, 1.0);
Check(
    "前奏（首行从 0 开始）行号 = 0",
    introAt.Index == 0,
    $"得到 {introAt.Index}");

var laterLines = new List<LyricLine> { Line(10, 15, "第一行"), Line(15, 20, "第二行") };
var late = new LyricLineCursor();
var beforeFirst = late.Resolve("A", laterLines, 2.0);
Check(
    "位置早于首行：行号 = -1",
    beforeFirst.Index == -1,
    $"得到 {beforeFirst.Index}");

// 8) 换歌重置：上一首停在第 4 行，下一首从第 1 行开始，不许被上一首的迟滞拖住。
var acrossTracks = new LyricLineCursor();
acrossTracks.Resolve("A", lines, 16.5);
var newTrack = acrossTracks.Resolve("B", lines, 0.5);
Check(
    "换歌重置（第 4 行 → 新曲 0.5 秒 = 第 1 行）",
    newTrack.Index == 0 && Math.Abs(newTrack.ScanSeconds - 0.5) < 0.0001,
    $"得到 行号={newTrack.Index}，擦亮位置={newTrack.ScanSeconds:0.0}");

// 9) 同一首歌重新取到歌词（行数变了）也要重置，否则下标会错位到别的句子。
var refetched = new LyricLineCursor();
refetched.Resolve("A", lines, 16.5);
var shorter = new List<LyricLine> { Line(0, 5, "新第一行"), Line(5, 18, "新第二行") };
var afterRefetch = refetched.Resolve("A", shorter, 6.0);
Check(
    "行集合换了（4 行 → 2 行）按新集合重算",
    afterRefetch.Index == 1 && Math.Abs(afterRefetch.ScanSeconds - 6.0) < 0.0001,
    $"得到 行号={afterRefetch.Index}，擦亮位置={afterRefetch.ScanSeconds:0.0}");

// 10) 连续抖动（模拟 50 毫秒一拍的脏位置）：100 拍里行号与擦亮位置都不许出现回退。
var storm = new LyricLineCursor();
var stormFailures = 0;
var lastIndex = int.MinValue;
var lastScan = double.MinValue;
foreach (var t in new[] { 10.5, 10.2, 10.8, 10.3, 11.0, 10.6, 11.4, 10.9, 11.7, 11.1 })
{
    var step = storm.Resolve("A", lines, t);
    if (step.Index < lastIndex || step.ScanSeconds < lastScan - 0.0001)
    {
        stormFailures++;
    }

    lastIndex = step.Index;
    lastScan = step.ScanSeconds;
}

Check("连续抖动 10 拍无任何回退", stormFailures == 0, $"回退次数={stormFailures}");

// 11) Reset 之后按新快照重新开始，不留上一首的行号。
var reset = new LyricLineCursor();
reset.Resolve("A", lines, 16.5);
reset.Reset();
var afterReset = reset.Resolve("A", lines, 0.5);
Check(
    "Reset 后从零开始（16.5 秒 → 0.5 秒 = 第 1 行）",
    afterReset.Index == 0 && Math.Abs(afterReset.ScanSeconds - 0.5) < 0.0001,
    $"得到 行号={afterReset.Index}，擦亮位置={afterReset.ScanSeconds:0.0}");

Console.WriteLine();
Console.WriteLine("=== 端到端：投影到呈现帧 ===");
Console.WriteLine();

// 12) 同一份快照连投影三帧，位置抖回去：不传迟滞器（自检/探针的旧行为）行号会退，
//     传了迟滞器（任务栏窗口的行为）行号与行内进度都不退。
var settings = new AppSettings();
var stamp = new DateTimeOffset(2026, 10, 16, 15, 0, 0, TimeSpan.FromHours(8));
var document = new LyricDocument(lines, LyricsSyncType.SyllableSynced, "YRC");
var lyrics = new LyricsResult("Netease", document);

MediaSnapshot At(double position) => new(
    IsConnected: true,
    IsPlaying: true,
    Title: "迟滞自检曲",
    Artist: "自检",
    SourceId: "cloudmusic.exe",
    SourceName: "网易云音乐",
    Position: position,
    Duration: 200,
    PlaybackRate: 1,
    TimelineUpdatedAt: stamp,
    Lyrics: lyrics,
    LyricsLookupCompleted: true);

// TimelineUpdatedAt 与传入的 now 相同，位置就是快照里的 Position，没有任何外推。
var pure = LyricsPresentationProjector.Project(At(10.5), settings, stamp);
var pureJitter = LyricsPresentationProjector.Project(At(10.2), settings, stamp);
Check(
    "不传迟滞器：行内抖动照样退（保持旧行为）",
    pure.CurrentLineIndex == 2 && pureJitter.CurrentLineIndex == 2 && pureJitter.LineProgress < pure.LineProgress,
    $"10.5 秒 → 行 {pure.CurrentLineIndex}（进度 {pure.LineProgress:0.00}）；10.2 秒 → 行 {pureJitter.CurrentLineIndex}（进度 {pureJitter.LineProgress:0.00}）");

var windowCursor = new LyricLineCursor();
var first = LyricsPresentationProjector.Project(At(10.5), settings, stamp, windowCursor);
var jittered = LyricsPresentationProjector.Project(At(9.9), settings, stamp, windowCursor);
Check(
    "传迟滞器：行号与行内进度都不退",
    first.CurrentLineIndex == 2 && jittered.CurrentLineIndex == 2
        && Math.Abs(jittered.LineProgress - first.LineProgress) < 0.0001,
    $"10.5 秒 → 行 {first.CurrentLineIndex}（进度 {first.LineProgress:0.00}）；9.9 秒 → 行 {jittered.CurrentLineIndex}（进度 {jittered.LineProgress:0.00}）");

var seeked = LyricsPresentationProjector.Project(At(9.3), settings, stamp, windowCursor);
Check(
    "传迟滞器：真回拖仍然立刻换行",
    seeked.CurrentLineIndex == 1,
    $"9.3 秒 → 行 {seeked.CurrentLineIndex}");

Console.WriteLine();
Console.WriteLine("=== 行级歌词的合成擦亮（LyricHighlightPolicy）===");
Console.WriteLine();

// 13) 只有行级时间轴时也要有擦亮：进度在行窗口内从 0 爬到 1，并且此后保持 1（不倒退、不慢慢爬）。
//     六个字 → 估算 6×0.22 = 1.32 秒，行窗口 4 秒，所以扫描头用 1.32 秒走完，剩下的时间保持全亮。
var lineLevel = Line(10, 14, "一二三四五六");
var synthetic0 = LyricHighlightPolicy.ResolveProgress(lineLevel, 10);
var syntheticHalf = LyricHighlightPolicy.ResolveProgress(lineLevel, 10.66);
var syntheticFull = LyricHighlightPolicy.ResolveProgress(lineLevel, 11.32);
var syntheticTail = LyricHighlightPolicy.ResolveProgress(lineLevel, 13.5);
Check(
    "行级歌词：0 → 约一半 → 1 → 保持 1",
    synthetic0 == 0 && syntheticHalf is > 0.4 and < 0.6 && syntheticFull == 1 && syntheticTail == 1,
    $"{synthetic0:0.00} / {syntheticHalf:0.00} / {syntheticFull:0.00} / {syntheticTail:0.00}");

// 14) 长尾行不许慢慢爬：4 个字 → 估算 0.9 秒（下限），行窗口 20 秒，1 秒时就应该全亮。
var longTail = Line(0, 20, "四字歌词");
var tailAtOneSecond = LyricHighlightPolicy.ResolveProgress(longTail, 1.0);
var tailAtTenSeconds = LyricHighlightPolicy.ResolveProgress(longTail, 10);
Check(
    "长尾行：按字数估算上限后保持全亮",
    tailAtOneSecond == 1 && tailAtTenSeconds == 1,
    $"1 秒 {tailAtOneSecond:0.00} / 10 秒 {tailAtTenSeconds:0.00}");

// 15) 行窗口缺失（End 未补齐）时仍按估算走，而不是整行一次性点亮。
var noWindow = Line(10, 0, "一二三四五六");
var noWindowHalf = LyricHighlightPolicy.ResolveProgress(noWindow, 10.66);
Check(
    "行窗口缺失：仍按字数估算擦亮",
    noWindowHalf is > 0.4 and < 0.6,
    $"10.66 秒 → {noWindowHalf:0.00}");

// 16) 有逐字时间轴的行不受合成路径影响：仍然只按真实逐字片段推进。
var worded = new LyricLine(0, 10, "甲乙丙丁")
{
    Words =
    [
        new LyricWord(0, 4, "甲乙"),
        new LyricWord(4, 8, "丙丁")
    ]
};
var wordedQuarter = LyricHighlightPolicy.ResolveProgress(worded, 2);
var wordedAfterWords = LyricHighlightPolicy.ResolveProgress(worded, 9);
var wordedPast = LyricHighlightPolicy.ResolveProgress(worded, 12);
Check(
    "有逐字时间轴：仍按字推进（2 秒 → 0.25，词尾后 1，越界仍 1）",
    wordedQuarter is > 0.24 and < 0.26 && wordedAfterWords == 1 && wordedPast == 1,
    $"{wordedQuarter:0.00} / {wordedAfterWords:0.00} / {wordedPast:0.00}");

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine($"RESULT: PASS（{checks} 项断言）");
    return 0;
}

Console.WriteLine($"RESULT: FAIL（{failures.Count} 项不符合预期）：{string.Join("；", failures)}");
return 1;
