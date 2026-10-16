using LyricsBar.Classes.Services.Lyrics;
using LyricsBar.Classes.Services.Lyrics.Presentation;
using LyricsBar.Classes.Settings;

// 悬停分区探针：证明"指针只糊它所在的那一块"这条判定本身是纯函数且边界正确，同时把 v1.5.0 新增的
// 宽度规则、字体清单、颜色预设一起钉住 —— 这些全是设置窗口和宿主共用的常量表，改错了不会有编译错误。
// Probe for the hover split: proves the "the pointer blurs only the part it is over" decision is a pure function with the right
// boundaries, and pins the width rule, the font list and the colour presets added in v1.5.0 — shared constant tables that a bad
// edit would not turn into a compile error.
var failures = new List<string>();
var checks = 0;

void Check(string name, bool ok, string detail)
{
    checks++;
    Console.WriteLine($"{(ok ? "[ok]  " : "[FAIL]")} {name,-56} {detail}");
    if (!ok)
    {
        failures.Add(name);
    }
}

Console.WriteLine("== 分界点宽度 / info-part width ==");

// 只有封面：占"边长 + 间距"。
Check("cover only", LyricsHoverRegionPolicy.ResolveInfoPartWidthDip(true, 40, 8, 0, 0) == 48,
    $"cover 40 + gap 8 = {LyricsHoverRegionPolicy.ResolveInfoPartWidthDip(true, 40, 8, 0, 0)}");

// 只有曲目信息：占"宽度 + 间距"。
Check("track info only", LyricsHoverRegionPolicy.ResolveInfoPartWidthDip(false, 40, 8, 120, 10) == 130,
    $"width 120 + gap 10 = {LyricsHoverRegionPolicy.ResolveInfoPartWidthDip(false, 40, 8, 120, 10)}");

// 两块都在：相加。
Check("both parts", LyricsHoverRegionPolicy.ResolveInfoPartWidthDip(true, 40, 8, 120, 10) == 178,
    $"48 + 130 = {LyricsHoverRegionPolicy.ResolveInfoPartWidthDip(true, 40, 8, 120, 10)}");

// 关掉封面、或者封面边长是 0/不存在，封面都不贡献宽度。
Check("cover off", LyricsHoverRegionPolicy.ResolveInfoPartWidthDip(false, 40, 8, 0, 0) == 0,
    $"{LyricsHoverRegionPolicy.ResolveInfoPartWidthDip(false, 40, 8, 0, 0)}");
Check("cover size zero", LyricsHoverRegionPolicy.ResolveInfoPartWidthDip(true, 0, 8, 0, 0) == 0,
    $"{LyricsHoverRegionPolicy.ResolveInfoPartWidthDip(true, 0, 8, 0, 0)}");
Check("cover size NaN", LyricsHoverRegionPolicy.ResolveInfoPartWidthDip(true, double.NaN, 8, 0, 0) == 0,
    $"{LyricsHoverRegionPolicy.ResolveInfoPartWidthDip(true, double.NaN, 8, 0, 0)}");

// 负数间距不能把宽度吃掉。
Check("negative cover gap clamps", LyricsHoverRegionPolicy.ResolveInfoPartWidthDip(true, 40, -8, 0, 0) == 40,
    $"{LyricsHoverRegionPolicy.ResolveInfoPartWidthDip(true, 40, -8, 0, 0)}");
Check("negative info gap clamps", LyricsHoverRegionPolicy.ResolveInfoPartWidthDip(false, 0, 0, 120, -10) == 120,
    $"{LyricsHoverRegionPolicy.ResolveInfoPartWidthDip(false, 0, 0, 120, -10)}");

Console.WriteLine();
Console.WriteLine("== 指针落在哪一块 / which part is under the pointer ==");

// 分界点右侧是歌词，左侧（含正好压在分界点上）是信息块。
Check("right of the boundary", LyricsHoverRegionPolicy.Resolve(300, 200) == LyricsOverlayRegion.Lyrics,
    $"{LyricsHoverRegionPolicy.Resolve(300, 200)}");
Check("left of the boundary", LyricsHoverRegionPolicy.Resolve(100, 200) == LyricsOverlayRegion.Info,
    $"{LyricsHoverRegionPolicy.Resolve(100, 200)}");
Check("exactly on the boundary", LyricsHoverRegionPolicy.Resolve(200, 200) == LyricsOverlayRegion.Info,
    $"{LyricsHoverRegionPolicy.Resolve(200, 200)}");
Check("content start (0)", LyricsHoverRegionPolicy.Resolve(0, 200) == LyricsOverlayRegion.Info,
    $"{LyricsHoverRegionPolicy.Resolve(0, 200)}");
// 左侧内边距会把指针推到负数，那也算"在看封面这一边"，否则最左边会有一段没有悬停效果的死区。
Check("negative (inline padding)", LyricsHoverRegionPolicy.Resolve(-3, 200) == LyricsOverlayRegion.Info,
    $"{LyricsHoverRegionPolicy.Resolve(-3, 200)}");

// 没有信息块时整条栏都是歌词，指针在哪都一样。
Check("no info part -> lyrics", LyricsHoverRegionPolicy.Resolve(0, 0) == LyricsOverlayRegion.Lyrics,
    $"{LyricsHoverRegionPolicy.Resolve(0, 0)}");
Check("no info part, far right", LyricsHoverRegionPolicy.Resolve(9999, 0) == LyricsOverlayRegion.Lyrics,
    $"{LyricsHoverRegionPolicy.Resolve(9999, 0)}");
Check("info width NaN -> lyrics", LyricsHoverRegionPolicy.Resolve(50, double.NaN) == LyricsOverlayRegion.Lyrics,
    $"{LyricsHoverRegionPolicy.Resolve(50, double.NaN)}");

// 指针位置读不到（NaN/无穷）时不能猜：判成"哪一块都不是"，两边都不糊。
Check("pointer NaN -> none", LyricsHoverRegionPolicy.Resolve(double.NaN, 200) == LyricsOverlayRegion.None,
    $"{LyricsHoverRegionPolicy.Resolve(double.NaN, 200)}");
Check("pointer +inf -> none", LyricsHoverRegionPolicy.Resolve(double.PositiveInfinity, 200) == LyricsOverlayRegion.None,
    $"{LyricsHoverRegionPolicy.Resolve(double.PositiveInfinity, 200)}");
Check("pointer -inf -> none", LyricsHoverRegionPolicy.Resolve(double.NegativeInfinity, 200) == LyricsOverlayRegion.None,
    $"{LyricsHoverRegionPolicy.Resolve(double.NegativeInfinity, 200)}");

Console.WriteLine();
Console.WriteLine("== 曲目信息宽度规则 / track-info width rule ==");

Check("0 means automatic", LyricsTrackInfoWidthPolicy.NormalizeManual(0) == 0,
    $"{LyricsTrackInfoWidthPolicy.NormalizeManual(0)}");
Check("negative means automatic", LyricsTrackInfoWidthPolicy.NormalizeManual(-5) == 0,
    $"{LyricsTrackInfoWidthPolicy.NormalizeManual(-5)}");
Check("NaN means automatic", LyricsTrackInfoWidthPolicy.NormalizeManual(double.NaN) == 0,
    $"{LyricsTrackInfoWidthPolicy.NormalizeManual(double.NaN)}");
Check("below the floor clamps up", LyricsTrackInfoWidthPolicy.NormalizeManual(10) == LyricsTrackInfoWidthPolicy.MinimumDip,
    $"10 -> {LyricsTrackInfoWidthPolicy.NormalizeManual(10)}（floor {LyricsTrackInfoWidthPolicy.MinimumDip}）");
Check("at the floor stays", LyricsTrackInfoWidthPolicy.NormalizeManual(LyricsTrackInfoWidthPolicy.MinimumDip) == LyricsTrackInfoWidthPolicy.MinimumDip,
    $"{LyricsTrackInfoWidthPolicy.NormalizeManual(LyricsTrackInfoWidthPolicy.MinimumDip)}");
Check("above the ceiling clamps down", LyricsTrackInfoWidthPolicy.NormalizeManual(9999) == LyricsTrackInfoWidthPolicy.MaximumDip,
    $"9999 -> {LyricsTrackInfoWidthPolicy.NormalizeManual(9999)}（ceiling {LyricsTrackInfoWidthPolicy.MaximumDip}）");
Check("slider ceiling fits the rule", LyricsTrackInfoWidthPolicy.MaximumDip >= 400,
    $"MaximumDip {LyricsTrackInfoWidthPolicy.MaximumDip} >= 滑块上限 400");
Check("auto ceiling is a real width too", LyricsTrackInfoWidthPolicy.AutoMaximumDip >= LyricsTrackInfoWidthPolicy.MinimumDip,
    $"auto {LyricsTrackInfoWidthPolicy.AutoMaximumDip} >= floor {LyricsTrackInfoWidthPolicy.MinimumDip}");

Console.WriteLine();
Console.WriteLine("== 字体清单 / font list ==");

var fonts = LyricsFontCatalog.Candidates;
Check("font list is not empty", fonts.Count > 0, $"{fonts.Count} 项");
Check("no duplicate fonts", fonts.Distinct(StringComparer.Ordinal).Count() == fonts.Count,
    $"{fonts.Count} 项 / {fonts.Distinct(StringComparer.Ordinal).Count()} 不重复");
Check("no blank font name", fonts.All(f => !string.IsNullOrWhiteSpace(f)),
    string.Join(", ", fonts));
Check("system default label is not blank", !string.IsNullOrWhiteSpace(LyricsFontCatalog.SystemDefaultLabel),
    $"「{LyricsFontCatalog.SystemDefaultLabel}」");
Check("font list is not the label itself", !fonts.Contains(LyricsFontCatalog.SystemDefaultLabel, StringComparer.Ordinal),
    "清单里不该再出现「系统默认」，它由下拉自己加在最前面");
Check("chinese-capable faces present", fonts.Any(f => f.Contains("YaHei", StringComparison.OrdinalIgnoreCase)) && fonts.Any(f => f.Contains("SimSun", StringComparison.OrdinalIgnoreCase)),
    "至少要有雅黑与宋体这类随系统分发的中文字体");

Console.WriteLine();
Console.WriteLine("== 颜色预设 / colour presets ==");

var presets = LyricsTextColorPalette.Presets;
Check("preset list is not empty", presets.Count > 0, $"{presets.Count} 项");
Check("no duplicate preset names", presets.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == presets.Count,
    string.Join(" / ", presets.Select(p => p.Name)));
Check("no blank preset name", presets.All(p => !string.IsNullOrWhiteSpace(p.Name)),
    string.Join(" / ", presets.Select(p => p.Name)));
Check("every preset is #RRGGBB", presets.All(p => p.Hex.Length == 7 && p.Hex[0] == '#' && p.Hex.Skip(1).All(Uri.IsHexDigit)),
    string.Join(" ", presets.Select(p => p.Hex)));
Check("no duplicate preset colours", presets.Select(p => p.Hex.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Count() == presets.Count,
    $"{presets.Count} 项 / {presets.Select(p => p.Hex.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Count()} 不重复");

Console.WriteLine();
Console.WriteLine(failures.Count == 0
    ? $"RESULT: PASS（{checks} 项断言）"
    : $"RESULT: FAIL（{failures.Count}/{checks}）：{string.Join("；", failures)}");
return failures.Count == 0 ? 0 : 1;
