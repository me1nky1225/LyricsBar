// 同名曲自检：验证"按 id 精确取词"只在队列条目被歌手校验确认之后才拿到 id。
//
// 为什么需要它：网易云的"按 id 精确取词"命中即 100 分，而 100 分会让优先源提前结束这一轮取词，
// 其它来源再准也没有机会上场。所以这条路上只要给错一个 id，用户看到的就是**另一首歌的歌词**，
// 而且分数上完全看不出来。这个自检直接调用真实的 NeteaseQueueService（内部类型，走反射），
// 用这台机器上真实的网易云队列文件造用例：
//   唯一曲名 + 正确歌手 → 给 id
//   唯一曲名 + 错误歌手 → 不给 id（歌手对不上，宁可退回搜索）
//   同名多首 + 歌手对上   → 给那一首的 id
//   同名多首 + 歌手对不上 → 不给 id（这就是"放 A 显示 B"的入口）
//   同名多首 + 歌手为空   → 不给 id（有歧义）
//
// 队列文件不存在（没装网易云）时打印 SKIP 并正常退出 0，不影响别的机器。
using System.Reflection;
using System.Text;
using System.Text.Json;

const string BogusArtist = "ZZZ Definitely Not This Artist";
const string AbsentTitle = "LyricsBar同名曲自检不存在的曲名ZZZ";

var queueFiles = new List<string>();
var tracks = new List<QueueTrack>();
foreach (var directory in QueueDirectories())
{
    foreach (var name in new[] { "playingList", "fmPlay" })
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path))
            continue;

        queueFiles.Add(path);
        ReadQueue(path, tracks);
    }

    if (queueFiles.Count > 0)
        break;
}

if (queueFiles.Count == 0)
{
    Console.WriteLine("SKIP: 未找到网易云播放队列文件（这台机器上没装或没开过网易云）。");
    return;
}

Console.WriteLine("=== 网易云队列按 id 取词：同名曲与歌手校验自检 ===");
foreach (var file in queueFiles)
    Console.WriteLine($"队列文件: {file}");
Console.WriteLine($"队列曲目: {tracks.Count} 首");

// 用与 NeteaseQueueService 相同的口径建索引：曲名与别名都算一个键。
var byTitle = new Dictionary<string, List<QueueTrack>>(StringComparer.Ordinal);
foreach (var track in tracks)
{
    Index(byTitle, track.Title, track);
    foreach (var alias in track.Aliases)
        Index(byTitle, alias, track);
}

var serviceType = typeof(LyricsBar.Classes.Services.LyricsCoordinator).Assembly
    .GetType("LyricsBar.Classes.Services.NeteaseQueueService", throwOnError: true)!;
var service = Activator.CreateInstance(serviceType, nonPublic: true)!;
var resolve = serviceType.GetMethod("TryResolveSongId", BindingFlags.Public | BindingFlags.Instance)!;

string? Resolve(string? title, string? artist) => (string?)resolve.Invoke(service, [title, artist]);
string Show(string? id) => string.IsNullOrEmpty(id) ? "(不给 id)" : id!;

var failures = 0;
void Check(string label, string? actual, string? expected)
{
    var ok = string.Equals(actual ?? string.Empty, expected ?? string.Empty, StringComparison.Ordinal);
    if (!ok)
        failures++;

    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {label}");
    Console.WriteLine($"         实际={Show(actual)}  期望={Show(expected)}");
}

// [A]-[D] 无歧义的曲名：队列里只有这一首（含别名）叫这个名字。
var uniqueKey = byTitle
    .Where(pair => pair.Value.Count == 1 && pair.Value[0].Artists.Count > 0)
    .Select(pair => pair.Key)
    .FirstOrDefault();
// [E]-[G] 同名多首：现实里就是同名不同歌，正是错歌词的入口。
var namesakeKey = byTitle
    .Where(pair => pair.Value.Count > 1 &&
        pair.Value.Select(track => Normalize(string.Join(" / ", track.Artists))).Distinct().Count() > 1)
    .Select(pair => pair.Key)
    .FirstOrDefault();

if (uniqueKey is not null)
{
    var track = byTitle[uniqueKey][0];
    var artist = string.Join(" / ", track.Artists);
    Console.WriteLine($"[A] 唯一曲名 + 正确歌手: 「{track.Title}」/「{artist}」");
    Check("唯一曲名 + 正确歌手 → 给该曲 id", Resolve(track.Title, artist), track.Id);

    Console.WriteLine($"[B] 唯一曲名 + 错误歌手: 「{track.Title}」/「{BogusArtist}」");
    Check("唯一曲名 + 错误歌手 → 不给 id", Resolve(track.Title, BogusArtist), null);

    Console.WriteLine($"[C] 唯一曲名 + 歌手为空: 「{track.Title}」/「」");
    Check("唯一曲名 + 歌手为空 → 给该曲 id（无歧义）", Resolve(track.Title, string.Empty), track.Id);

    var alias = track.Aliases.FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(alias))
    {
        Console.WriteLine($"[D] 别名 + 正确歌手: 「{alias}」/「{artist}」");
        Check("别名 + 正确歌手 → 给该曲 id", Resolve(alias, artist), track.Id);
    }
}
else
{
    Console.WriteLine("[A]-[D] SKIP: 队列里没有唯一的曲名，跳过无歧义用例。");
}

if (namesakeKey is not null)
{
    var entries = byTitle[namesakeKey];
    var first = entries[0];
    var firstArtist = string.Join(" / ", first.Artists);
    Console.WriteLine($"[E] 同名多首（真实队列数据）: 「{first.Title}」");
    foreach (var track in entries)
        Console.WriteLine($"         ID={track.Id} 歌手=[{string.Join(" / ", track.Artists)}]");

    Check($"同名 + 歌手对上（「{firstArtist}」）→ 给那一首的 id",
        Resolve(first.Title, firstArtist), first.Id);

    Console.WriteLine($"[F] 同名多首 + 歌手对不上: 「{first.Title}」/「{BogusArtist}」");
    Check("同名 + 歌手对不上 → 不给 id（放 A 显示 B 的入口）", Resolve(first.Title, BogusArtist), null);

    Console.WriteLine($"[G] 同名多首 + 歌手为空: 「{first.Title}」/「」");
    Check("同名 + 歌手为空 → 不给 id（有歧义）", Resolve(first.Title, string.Empty), null);
}
else
{
    Console.WriteLine("[E]-[G] SKIP: 队列里没有同名不同歌手的曲目，跳过错歌词用例。");
}

Console.WriteLine($"[H] 曲名不在队列里: 「{AbsentTitle}」");
Check("不存在的曲名 → 不给 id", Resolve(AbsentTitle, "Some Artist"), null);

Console.WriteLine();
Console.WriteLine(failures == 0 ? "RESULT: PASS" : $"RESULT: FAIL（{failures} 项不符合预期）");
Environment.ExitCode = failures == 0 ? 0 : 1;

static void Index(Dictionary<string, List<QueueTrack>> byTitle, string? rawTitle, QueueTrack track)
{
    var key = Normalize(rawTitle);
    if (key.Length == 0)
        return;

    if (!byTitle.TryGetValue(key, out var list))
        byTitle[key] = list = [];

    if (!list.Contains(track))
        list.Add(track);
}

// 与 NeteaseQueueService 同口径的归一化：只保留字母数字与汉字，转小写。
static string Normalize(string? value)
{
    if (string.IsNullOrWhiteSpace(value))
        return string.Empty;

    var builder = new StringBuilder(value.Length);
    foreach (var character in value)
    {
        if (char.IsLetterOrDigit(character))
            builder.Append(char.ToLowerInvariant(character));
    }

    return builder.ToString();
}

static IEnumerable<string> QueueDirectories()
{
    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    yield return Path.Combine(local, "NetEase", "CloudMusic", "WebData", "file");
    yield return Path.Combine(local, "Packages", "1F8B0F44.1099513045_x64__zy60xjz0syz92",
        "LocalCache", "Roaming", "NetEase", "CloudMusic", "WebData", "file");
}

static void ReadQueue(string path, List<QueueTrack> tracks)
{
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true });
    var root = document.RootElement;
    if (root.ValueKind != JsonValueKind.Object)
        return;

    if (root.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
    {
        foreach (var entry in list.EnumerateArray())
            Add(entry, tracks);
        return;
    }

    if (root.TryGetProperty("queue", out var queue) && queue.ValueKind == JsonValueKind.Array)
    {
        foreach (var entry in queue.EnumerateArray())
            Add(entry, tracks);
    }
}

static void Add(JsonElement entry, List<QueueTrack> tracks)
{
    var track = entry;
    if (entry.ValueKind == JsonValueKind.Object &&
        entry.TryGetProperty("track", out var nested) &&
        nested.ValueKind == JsonValueKind.Object)
    {
        track = nested;
    }

    if (track.ValueKind != JsonValueKind.Object)
        return;

    var id = track.TryGetProperty("id", out var idElement)
        ? idElement.ValueKind switch
        {
            JsonValueKind.String => idElement.GetString() ?? string.Empty,
            JsonValueKind.Number => idElement.TryGetInt64(out var value) ? value.ToString() : string.Empty,
            _ => string.Empty
        }
        : string.Empty;

    var title = track.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
        ? name.GetString() ?? string.Empty
        : string.Empty;
    if (id.Length == 0 || title.Length == 0)
        return;

    var artists = new List<string>();
    if (track.TryGetProperty("artists", out var artistArray) && artistArray.ValueKind == JsonValueKind.Array)
    {
        foreach (var artist in artistArray.EnumerateArray())
        {
            if (artist.ValueKind == JsonValueKind.Object &&
                artist.TryGetProperty("name", out var artistName) &&
                artistName.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(artistName.GetString()))
            {
                artists.Add(artistName.GetString()!);
            }
        }
    }

    var aliases = new List<string>();
    foreach (var key in new[] { "alia", "alias" })
    {
        if (!track.TryGetProperty(key, out var aliasArray) || aliasArray.ValueKind != JsonValueKind.Array)
            continue;

        foreach (var alias in aliasArray.EnumerateArray())
        {
            if (alias.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(alias.GetString()))
                aliases.Add(alias.GetString()!);
        }
    }

    tracks.Add(new QueueTrack(id, title, artists, aliases));
}

internal sealed record QueueTrack(string Id, string Title, List<string> Artists, List<string> Aliases);
