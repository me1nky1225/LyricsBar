using LyricsBar.Classes.Services;

// 播放器激活探针：只钉住"从 SMTC 来源标识推出进程名候选"这条纯函数。
//
// 为什么值得一个探针：这条推导错了不会有编译错误，也不会有日志——用户点一下没反应，就是"没反应"。而真实标识的形状
// 差别很大（网易云给 `cloudmusic.exe`，Store 版给 `...!App`，有的给产品名），这里把见过的每一种形状都固定下来。
// Probe for player activation: it pins only the pure "derive process-name candidates from an SMTC source identifier" step.
//
// Why it deserves a probe: getting the derivation wrong produces neither a compile error nor a log line — the user clicks and
// nothing happens. Real identifiers differ a lot (NetEase hands over `cloudmusic.exe`, a Store build hands over `...!App`,
// some hand over a product name), so every shape seen so far is fixed here.
var failures = new List<string>();
var checks = 0;

void Check(string name, bool ok, string detail)
{
    checks++;
    Console.WriteLine($"{(ok ? "[ok]  " : "[FAIL]")} {name,-56} {detail}");
    if (!ok)
        failures.Add(name);
}

// 候选清单必须逐项相等（顺序也算）：第一个匹配上的进程会被激活，顺序错了就会打开错的东西。
// The candidate list must match exactly, order included: the first matching process is the one activated, so a wrong order
// opens the wrong thing.
void CheckList(string identifier, string[] expected)
{
    var actual = PlayerActivationService.ResolveProcessNameCandidates(identifier).ToArray();
    Check($"candidates for {identifier}", actual.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase),
        $"[{string.Join(" | ", actual)}]");
}

CheckList("cloudmusic.exe", ["cloudmusic"]);
CheckList("CloudMusic.exe", ["CloudMusic"]);
CheckList(@"C:\Program Files\Netease\CloudMusic\cloudmusic.exe", ["cloudmusic"]);
CheckList(@"C:\Program Files\Netease\CloudMusic\cloudmusic.exe!App", ["cloudmusic"]);
CheckList("QQMusic.exe", ["QQMusic"]);
CheckList("Spotify.exe!Spotify", ["Spotify"]);
CheckList("NeteaseCloudMusic", ["NeteaseCloudMusic", "cloudmusic"]);
CheckList("163music.exe", ["163music", "cloudmusic"]);
CheckList("KuGou.exe", ["KuGou"]);
CheckList("PotPlayerMini64.exe", ["PotPlayerMini64"]);
CheckList("Microsoft.ZuneMusic_8wekyb3d8bbwe!Music.UI", ["Microsoft.ZuneMusic_8wekyb3d8bbwe", "Music.UI"]);
CheckList("chrome.exe", ["chrome"]);
CheckList("  spotify.exe  ", ["spotify"]);

var empty = PlayerActivationService.ResolveProcessNameCandidates("").Count;
Check("empty identifier yields no candidates", empty == 0, $"{empty} candidate(s)");

var nul = PlayerActivationService.ResolveProcessNameCandidates(null).Count;
Check("null identifier yields no candidates", nul == 0, $"{nul} candidate(s)");

var blank = PlayerActivationService.ResolveProcessNameCandidates("   ").Count;
Check("blank identifier yields no candidates", blank == 0, $"{blank} candidate(s)");

// 打包标识里的 "App" 不能被当成进程名：机器上真有可能有一个无关的 App.exe，撞上就会把别人的窗口拉到前台。
// The "App" inside a packaged identifier must not count as a process name: an unrelated App.exe really can exist on a machine,
// and matching it would drag someone else's window to the front.
var packaged = PlayerActivationService.ResolveProcessNameCandidates(@"C:\Apps\Music\player.exe!App").ToArray();
Check("packaged '!App' is not a process candidate", !packaged.Contains("App", StringComparer.OrdinalIgnoreCase),
    $"[{string.Join(" | ", packaged)}]");

// 不含 '!' 的标识不会走 AUMID 激活；这里只确认推导本身不抛、不返回奇怪的东西。
var unknown = PlayerActivationService.ResolveProcessNameCandidates("SomeUnknownPlayer").ToArray();
Check("unknown identifier keeps its own name", unknown.Length == 1 && unknown[0] == "SomeUnknownPlayer",
    $"[{string.Join(" | ", unknown)}]");

Console.WriteLine();
Console.WriteLine(failures.Count == 0
    ? $"RESULT: PASS ({checks} checks)"
    : $"RESULT: FAIL ({failures.Count}/{checks}) — {string.Join(", ", failures)}");

return failures.Count == 0 ? 0 : 1;
