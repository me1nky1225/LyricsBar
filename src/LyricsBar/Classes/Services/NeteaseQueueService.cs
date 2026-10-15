using System.IO;
using System.Text;
using System.Text.Json;
using LyricsBar.Classes.Utils;

namespace LyricsBar.Classes.Services;

/// <summary>
/// 从网易云音乐自己的本地播放队列里反查出"当前这首"的歌曲 id，用来走"按 id 精确取词"那条最准的路。
///
/// **为什么需要它**：网易云通过 SMTC 上报的元数据里没有歌曲 id，而本项目只读它的进程内存取播放位置
/// （<see cref="NeteasePositionService"/>），没读 id。于是 <c>Netease</c> 这个"按 id 精确取词、命中即 100 分"的来源
/// 一直拿不到 id、永远是空跑，取词只能退化成"拿曲名 + 歌手去搜索"，也就可能搜到同名曲、现场版或翻唱。
/// 上游 AF Media Bar 靠读进程内存里的 id（写死偏移量）解决这件事；这里换一条**不碰内存**的路：
/// 网易云自己会把当前播放队列写进
/// <c>%LOCALAPPDATA%\NetEase\CloudMusic\WebData\file\playingList</c>（歌单队列，形如 <c>{"list":[{"track":{...}}]}</c>）
/// 与 <c>fmPlay</c>（私人FM，形如 <c>{"currentIndex":n,"queue":[...]}</c>），两个文件里都带每首歌的 id、曲名、歌手。
/// 于是"按曲名 + 歌手在队列里反查 id"就够了：命中就用那个 id 精确取词，没命中就什么都不做（退回今天的行为）。
///
/// 这条路的稳健性来自三点：① 只读文件，不写、不注入、不依赖任何内存偏移；② 队列文件在播放歌单时是稳定的，
/// 不会像"当前曲目"那样每首歌都变；③ 匹配要求曲名（含别名）**归一化后完全相等**，而且歌手必须对得上——
/// 歌手对不上、或者同名曲不止一首时一律不给 id，最坏情况就是退回原来的搜索路径。
/// （同名曲里随便挑一首交出去，用户看到的就是另一首歌的歌词：那条路命中即 100 分，还会让这一轮取词提前结束。）
/// Reads the id of the currently playing track out of NetEase CloudMusic's own local play queue so the exact-by-id lyric
/// lookup — the most accurate path there is — can actually run.
///
/// **Why it is needed**: NetEase reports no song id through SMTC, and this project only reads its process memory for the
/// playback position, never for the id. The <c>Netease</c> source, which fetches by id and scores a flat 100, therefore never
/// had an id and always ran empty, leaving retrieval to fall back on a title-and-artist search that can land on a namesake,
/// a live take, or a cover. Upstream AF Media Bar solves this by reading the id out of process memory at a hardcoded offset;
/// this takes a **memory-free** route instead: NetEase writes its own play queue to
/// <c>%LOCALAPPDATA%\NetEase\CloudMusic\WebData\file\playingList</c> (the playlist queue, shaped
/// <c>{"list":[{"track":{...}}]}</c>) and <c>fmPlay</c> (private FM, shaped <c>{"currentIndex":n,"queue":[...]}</c>), and both
/// carry each track's id, title, and artists. Looking the current track up by title and artist is therefore enough: a hit
/// yields the id for an exact fetch, and a miss does nothing at all, leaving today's behaviour intact.
///
/// Three things make it robust: it only reads files — no writes, no injection, no memory offsets; the queue file is stable
/// while a playlist plays rather than changing with every track; and a match requires the title (aliases included) to be
/// **exactly equal after normalisation** *and* the artist to be confirmed. An artist mismatch, or more than one namesake in
/// the queue, yields no id at all, and the worst case is simply the old search path — handing back one of several namesakes
/// is precisely how another song's lyrics end up on screen.
/// </summary>
internal sealed class NeteaseQueueService
{
    /// <summary>队列文件所在目录（安装版）。/ Directory holding the queue files (the desktop build).</summary>
    private static readonly string[] QueueDirectories =
    [
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetEase", "CloudMusic", "WebData", "file"),
        // 应用商店版的网易云把数据放在 Packages 下，路径不同但文件名一致。
        // The Store build keeps its data under Packages; the directory differs but the file names do not.
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", "1F8B0F44.1099513045_x64__zy60xjz0syz92", "LocalCache", "Roaming", "NetEase", "CloudMusic", "WebData", "file")
    ];

    /// <summary>缓存：解析出来的候选曲目，以及它对应的文件指纹。/ Cache: the parsed candidates plus the file fingerprint they came from.</summary>
    private readonly List<QueueTrack> _tracks = [];
    private readonly List<(string Path, long Length, DateTime LastWrite)> _fingerprint = [];
    private DateTime _lastAttemptUtc = DateTime.MinValue;

    /// <summary>上一次记过日志的判定文本：同一首歌会被反复解析（重试、诊断），日志不该反复刷同样的行。
    /// The last verdict that was logged: one track is resolved repeatedly (retries, diagnostics), so each verdict is logged once.</summary>
    private string _lastResolution = string.Empty;

    /// <summary>重新读取队列的最小间隔：换歌才查一次，短时间内连切几首时不要反复读 1.4MB 的文件。</summary>
    private static readonly TimeSpan MinimumReloadInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 反查当前曲目的网易云歌曲 id。
    /// Resolves the NetEase song id of the currently playing track.
    /// </summary>
    /// <param name="title">SMTC 报出的曲名。/ Title reported by SMTC.</param>
    /// <param name="artist">SMTC 报出的歌手。/ Artist reported by SMTC.</param>
    /// <returns>歌曲 id；查不到或不该查时为 null。/ The song id, or null when it cannot be resolved or should not be looked up.</returns>
    public string? TryResolveSongId(string? title, string? artist)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        try
        {
            ReloadIfNeeded();
            var wantedTitle = Normalize(title);
            if (wantedTitle.Length == 0)
                return null;

            var wantedArtist = Normalize(artist);
            List<QueueTrack>? unconfirmed = null;
            foreach (var track in _tracks)
            {
                if (track.Id.Length == 0 || !MatchesTitle(track, wantedTitle))
                    continue;

                // 歌手对得上才叫确认。这是唯一一条"确定就是这首歌"的路。
                // An artist match is the only confirmed identification.
                if (wantedArtist.Length > 0 && MatchesArtist(track, wantedArtist))
                {
                    LogResolution($"网易云队列命中「{Describe(track)}」，按 id 精确取词：ID={track.Id}");
                    return track.Id;
                }

                (unconfirmed ??= []).Add(track);
            }

            // 歌手没确认过的时候，只有"队列里只此一首同名曲"这种没有歧义的情况才给 id。
            // 同名多首却挑一首交出去，就是"放 A 显示 B 的歌词"：那条路命中即 100 分，还会让这一轮取词提前结束，
            // 其它来源再准也没有机会上场，用户在界面上完全看不出是另一个 id 的歌词。
            // Without a confirmed artist, an id is handed back only when the queue holds exactly one track of that title.
            // Picking one of several namesakes is how the *other* song's lyrics get shown: that path scores a flat 100 and
            // ends the round early, so no other source can compete and nothing on screen reveals the wrong id.
            if (unconfirmed is not { Count: 1 })
            {
                if (unconfirmed is { Count: > 1 })
                    LogResolution($"网易云队列里有 {unconfirmed.Count} 首同名曲，歌手都对不上，跳过按 id 精确取词：" +
                        $"当前「{title} — {artist}」，队列同名曲 {string.Join("、", unconfirmed.Select(Describe))}");
                return null;
            }

            var only = unconfirmed[0];
            if (wantedArtist.Length == 0 || only.Artists.Count == 0)
            {
                LogResolution($"网易云队列里唯一同名曲「{Describe(only)}」，按 id 精确取词：ID={only.Id}");
                return only.Id;
            }

            LogResolution($"网易云队列里「{Describe(only)}」的歌手与当前曲目对不上，跳过按 id 精确取词：" +
                $"当前「{title} — {artist}」");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // 网易云没装、没在播、文件被独占、格式变了……都只意味着"这次查不到"，不影响其它来源。
            // NetEase missing, idle, holding the file open, or a changed format all just mean "not this time"; no other source
            // is affected.
            Log.Warn("Lyrics", $"读取网易云播放队列失败: {ex.GetType().Name} {ex.Message}");
            return null;
        }
    }

    /// <summary>队列条目的可读写法，只用于日志。/ Human-readable queue entry, for logs only.</summary>
    private static string Describe(QueueTrack track) =>
        track.Artists.Count == 0 ? track.Title : $"{track.Title} — {string.Join(" / ", track.Artists)}";

    /// <summary>把"这次到底按哪个 id 取词、为什么没给 id"写进日志，只写一次：这是这类错配事后唯一能查的凭据。
    /// Records which id was used, or why none was, and only once — this is the evidence a mismatch needs afterwards.</summary>
    private void LogResolution(string message)
    {
        if (string.Equals(message, _lastResolution, StringComparison.Ordinal))
            return;

        _lastResolution = message;
        Log.Info("Lyrics", message);
    }

    /// <summary>队列文件变化时才重新解析；短时间内的连续调用直接复用上次结果。/ Re-parses only when a queue file changed; rapid repeat calls reuse the last result.</summary>
    private void ReloadIfNeeded()
    {
        var now = DateTime.UtcNow;
        if (now - _lastAttemptUtc < MinimumReloadInterval)
            return;

        _lastAttemptUtc = now;

        var fingerprint = new List<(string, long, DateTime)>();
        foreach (var directory in QueueDirectories)
        {
            foreach (var name in (string[])["playingList", "fmPlay"])
            {
                var path = Path.Combine(directory, name);
                var info = new FileInfo(path);
                if (info.Exists)
                    fingerprint.Add((path, info.Length, info.LastWriteTimeUtc));
            }

            if (fingerprint.Count > 0)
                break;
        }

        if (fingerprint.Count == 0)
        {
            _tracks.Clear();
            _fingerprint.Clear();
            return;
        }

        if (fingerprint.Count == _fingerprint.Count && fingerprint.SequenceEqual(_fingerprint))
            return;

        _tracks.Clear();
        foreach (var (path, _, _) in fingerprint)
            ReadQueue(path);

        _fingerprint.Clear();
        _fingerprint.AddRange(fingerprint);
        Log.Info("Lyrics", $"已载入网易云播放队列：{_tracks.Count} 首（用于按 id 精确取词）。");
    }

    /// <summary>
    /// 读一个队列文件。两种格式都按"有没有 <c>list</c> 数组"来分辨，条目里的曲目可能直接就是条目本身，
    /// 也可能包在 <c>track</c> 里。
    /// Reads one queue file. The two shapes are told apart by the presence of a <c>list</c> array, and a track may either be
    /// the entry itself or sit inside a <c>track</c> object.
    /// </summary>
    private void ReadQueue(string path)
    {
        // 网易云一直开着这些文件，必须允许共享读写，否则会拿到"文件被占用"。
        // NetEase keeps these files open, so shared read/write is required or the read fails as "in use".
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true });

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return;

        if (root.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in list.EnumerateArray())
                AddTrack(entry);
            return;
        }

        if (root.TryGetProperty("queue", out var queue) && queue.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in queue.EnumerateArray())
                AddTrack(entry);
        }
    }

    private void AddTrack(JsonElement entry)
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

        var id = ReadId(track);
        if (id.Length == 0)
            return;

        var title = track.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString() ?? string.Empty
            : string.Empty;
        if (title.Length == 0)
            return;

        var artists = new List<string>();
        if (track.TryGetProperty("artists", out var artistArray) && artistArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var artist in artistArray.EnumerateArray())
            {
                if (artist.ValueKind == JsonValueKind.Object &&
                    artist.TryGetProperty("name", out var artistName) &&
                    artistName.ValueKind == JsonValueKind.String)
                {
                    var value = artistName.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        artists.Add(value);
                }
            }
        }

        // 别名（alia / alias）也算曲名：SMTC 报出来的有时是别名而不是主名。
        // Aliases count as titles too: SMTC sometimes reports the alias rather than the main name.
        var aliases = new List<string>();
        foreach (var key in (string[])["alia", "alias"])
        {
            if (!track.TryGetProperty(key, out var aliasArray) || aliasArray.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var alias in aliasArray.EnumerateArray())
            {
                if (alias.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(alias.GetString()))
                    aliases.Add(alias.GetString()!);
            }
        }

        _tracks.Add(new QueueTrack(id, title, artists, aliases));
    }

    private static string ReadId(JsonElement track)
    {
        if (!track.TryGetProperty("id", out var id))
            return string.Empty;

        return id.ValueKind switch
        {
            JsonValueKind.String => id.GetString() ?? string.Empty,
            JsonValueKind.Number => id.TryGetInt64(out var value) ? value.ToString() : string.Empty,
            _ => string.Empty
        };
    }

    private static bool MatchesTitle(QueueTrack track, string wantedTitle)
    {
        if (Normalize(track.Title) == wantedTitle)
            return true;

        foreach (var alias in track.Aliases)
        {
            if (Normalize(alias) == wantedTitle)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 歌手校验用"包含"而不是相等：播放器常把"周杰伦"报成"周杰伦 / 方文山"，反过来队列里也可能只写主歌手。
    /// The artist check uses containment rather than equality: a player often reports "Jay Chou / Vincent Fang" where the
    /// queue lists only the lead artist, and vice versa.
    /// </summary>
    private static bool MatchesArtist(QueueTrack track, string wantedArtist)
    {
        foreach (var artist in track.Artists)
        {
            var normalized = Normalize(artist);
            if (normalized.Length == 0)
                continue;
            if (wantedArtist.Contains(normalized, StringComparison.Ordinal) ||
                normalized.Contains(wantedArtist, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 归一化：转小写、丢掉所有非字母数字与非汉字字符。只用来比较，不用于显示。
    /// Normalises by lowercasing and dropping every character that is not a letter, a digit, or a CJK ideograph. Used for
    /// comparison only, never for display.
    /// </summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    private readonly record struct QueueTrack(string Id, string Title, List<string> Artists, List<string> Aliases);
}
