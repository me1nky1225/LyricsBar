using System.Diagnostics;
using System.IO;
using System.Text;
using LyricsBar.Classes.Interop;
using LyricsBar.Classes.Utils;

namespace LyricsBar.Classes.Services;

/// <summary>网易云播放状态（上游 <c>PlayStatus</c> 的取值顺序，来自真机实测）。/ NetEase's play status, in upstream order.</summary>
internal enum NeteasePlayStatus
{
    /// <summary>还没开始放。/ Not started.</summary>
    Waiting = 0,

    /// <summary>正在放。/ Playing.</summary>
    Playing = 1,

    /// <summary>暂停。/ Paused.</summary>
    Paused = 2,

    /// <summary>含义未确认的取值。/ A value whose meaning is unconfirmed.</summary>
    Unknown3 = 3,

    /// <summary>含义未确认的取值。/ A value whose meaning is unconfirmed.</summary>
    Unknown4 = 4,
}

/// <summary>一次读取的结果：位置、时长、状态与采样时刻。/ One read: position, duration, status and sampling instant.</summary>
internal readonly record struct NeteaseSignatureSample(
    double Position,
    double DurationSeconds,
    NeteasePlayStatus Status,
    DateTimeOffset SampledAt)
{
    /// <summary>播放器是否正在播放（状态字段为 Playing）。/ Whether the player is actually playing.</summary>
    public bool IsPlaying => Status == NeteasePlayStatus.Playing;
}

/// <summary>
/// 网易云的特征码位置通道 —— 与上游 AF Media Bar 同一条路（`NetEase.cs:60-99, 289-346`），本项目自己重写。
/// 它**不扫内存、不猜地址**：在 <c>cloudmusic.dll</c> 的 <c>.text</c> 段里按两条特征码定位两个全局指针
/// （播放器对象 + 调度器），位置就是调度器里那个 double，旁边 <c>+0xa8</c> 是时长、<c>+0x60</c> 是播放状态、
/// <c>+0x50</c> 是当前歌曲 id。
/// <para>
/// 为什么要有它：自研的内存扫描通道分不出"当前播放位置"和"随便一个按 1 倍速前进的计时器"。真机日志里扫描锁到过
/// 一个从当天 12:39 起算的全局计时器（位置变成 4800 秒、换歌不归零），歌词因此越过所有行尾停在最后一行；
/// 特征码通道读的是播放器自己的字段，不存在这个歧义，而且**它还能给出真实时长**——网易云的
/// <c>MediaSnapshot.Duration</c> 以前恒为 0。
/// </para>
/// <para>
/// 代价与风险：特征码只在附加时匹配一次（约 100 毫秒流式扫过 26 MB 的 <c>.text</c>，全程只用一个 4 MB 的复用缓冲，
/// 见 <c>ScanTextSection</c>），之后每拍只读几个字段；
/// 但网易云升级后特征码可能失效，所以 <see cref="TryRead"/> 失败时调用方必须能退回扫描通道，且失效原因要写进日志
/// （见 <see cref="NeteasePositionService"/>，它是本通道的调用方与兜底方）。探针 `tools/SignatureProbe` 用于
/// 单独验证这两条特征码在当前版本上是否仍然命中，以及读到的数值是否自洽。
/// The NetEase signature channel — the same route upstream AF Media Bar takes (NetEase.cs:60-99, 289-346), rewritten
/// here. It does **not** scan memory and does **not** guess addresses: two signatures locate two global pointers inside
/// cloudmusic.dll's .text section (the audio-player object and the scheduler), and the position is the double in the
/// scheduler, with +0xa8 the duration, +0x60 the play status and +0x50 the current song id.
/// </para>
/// <para>
/// Why it exists: the self-written memory scan cannot tell "the current playback position" from "any timer advancing at
/// 1x". The real-hardware log shows it locking a global timer started at 12:39 that day (position became 4800 seconds and
/// never reset on a track change), which sailed the lyrics past every line's end and froze them on the last one. The
/// signature channel reads the player's own fields, so that ambiguity cannot arise — and it also reports the real
/// duration, where NetEase's MediaSnapshot.Duration used to be a constant zero.
/// </para>
/// <para>
/// The cost and the risk: the signatures match once per attach (about 100 ms to stream through 26 MB of .text in one reused
/// 4 MB buffer, see ScanTextSection) and each poll then
/// reads a few fields; but a NetEase update can invalidate the signatures, so a failing TryRead must let the caller fall
/// back to the scan channel and must record why in the log. NeteasePositionService is both the caller and that fallback;
/// the SignatureProbe tool exists to verify the signatures against the installed version on their own.
/// </para>
/// </summary>
internal sealed class NeteaseSignatureChannel : IDisposable
{
    /// <summary>网易云的主进程名。/ NetEase's main process name.</summary>
    private const string PlayerProcessName = "cloudmusic";

    /// <summary>位置所在模块。特征码和偏移都是相对它算的。/ The module holding the position; signatures and offsets are relative to it.</summary>
    private const string PlayerModuleName = "cloudmusic.dll";

    /// <summary>最多依次尝试几个网易云进程（私有内存最大的几个）。/ How many NetEase processes to try, largest private memory first.</summary>
    private const int MaximumProcessCandidates = 3;

    /// <summary>流式扫描 .text 时一次读多少字节，也是那个复用缓冲的大小。整段一次读（26 MB）在实测中只能拿到一部分。/ How many bytes one read takes while streaming .text; also the size of the reused buffer. One 26 MB read only returns part of it in practice.</summary>
    private const int PatternChunkBytes = 4 * 1024 * 1024;

    /// <summary>匹配失败后的冷却：网易云升级、没运行、模块换名时不要每拍都去扫一遍 26 MB。/ Cool-down after a failed match.</summary>
    private const int AttachFailureCooldownMilliseconds = 60_000;

    /// <summary>连续这么多次读不到位置就认为附加失效（播放器重启、模块被换掉），重新附加。/ Consecutive read failures before re-attaching.</summary>
    private const int ReadFailureLimit = 5;

    /// <summary>位置字段的合理区间。上面留 6 小时，比任何一首歌都长。/ The sane range for the position field, generous on the upper end.</summary>
    private const double MaximumPositionSeconds = 6 * 60 * 60;

    /// <summary>播放器对象里时长字段的偏移（double 秒）。/ Offset of the duration field (double, seconds) in the player object.</summary>
    private const int DurationOffset = 0xa8;

    /// <summary>播放器对象里播放状态字段的偏移（int）。/ Offset of the play-status field (int) in the player object.</summary>
    private const int StatusOffset = 0x60;

    /// <summary>播放器对象里"播放信息"指针的偏移。/ Offset of the play-info pointer in the player object.</summary>
    private const int PlayInfoOffset = 0x50;

    /// <summary>读歌曲 id 时的试探长度：id 串（含播放器附加的那一截）远比这短。/ Bytes probed when reading the song id.</summary>
    private const int SongIdProbeBytes = 96;

    // 两条特征码与上游逐字节一致（NetEase.cs:41-44），上游注明改自 Kxnrl/NetEase-Cloud-Music-DiscordRPC。
    // 2026-10-16 在本机 cloudmusic.dll（2026-09-18 构建）上实测两条都命中，且读到的位置/时长/状态/id 可与接口互证。
    // Both signatures are byte-for-byte upstream's (NetEase.cs:41-44), which credits
    // Kxnrl/NetEase-Cloud-Music-DiscordRPC. On 2026-10-16 both matched the installed cloudmusic.dll (built 2026-09-18)
    // and the position, duration, status and id read from them cross-checked against the public API.
    private const string AudioPlayerPattern
        = "48 8D 0D ? ? ? ? E8 ? ? ? ? 48 8D 0D ? ? ? ? E8 ? ? ? ? 90 48 8D 0D ? ? ? ? E8 ? ? ? ? 48 8D 05 ? ? ? ? 48 8D A5 ? ? ? ? 5F 5D C3 CC CC CC CC CC 48 89 4C 24 ? 55 57 48 81 EC ? ? ? ? 48 8D 6C 24 ? 48 8D 7C 24";

    private const string AudioSchedulePattern = "66 0F 2E 0D ? ? ? ? 7A ? 75 ? 66 0F 2E 15";

    private static readonly (byte Value, bool Wildcard)[] PlayerNeedle = ParsePattern(AudioPlayerPattern);
    private static readonly (byte Value, bool Wildcard)[] ScheduleNeedle = ParsePattern(AudioSchedulePattern);

    private readonly object _gate = new();

    private ProcessMemoryReader? _reader;
    private IntPtr _playerPointer;
    private IntPtr _schedulePointer;
    private bool _attached;
    private long _nextAttachTicks;
    private int _readFailures;

    /// <summary>附加时认出来的模块信息（路径 + 版本），只用于日志。/ What the attach identified, for the log.</summary>
    private string _moduleDescription = string.Empty;

    /// <summary>上一次附加失败的原因，避免每 60 秒重复打同一行日志。/ The last attach failure, so the same line is not logged every minute.</summary>
    private string _lastFailure = string.Empty;

    /// <summary>是否已经拿到两个全局指针。/ Whether both global pointers have been resolved.</summary>
    public bool IsAttached
    {
        get
        {
            lock (_gate)
                return _attached;
        }
    }

    /// <summary>附加时认出来的模块信息。/ What the attach identified.</summary>
    public string ModuleDescription
    {
        get
        {
            lock (_gate)
                return _moduleDescription;
        }
    }

    /// <summary>
    /// 读一次位置。失败返回 false，调用方应当退回扫描通道 —— 本通道不允许抛异常，也不允许把歌词栏搞坏。
    /// Reads the position once. A false return means the caller should fall back to the scan channel; this channel never
    /// throws and is never allowed to break the bar.
    /// </summary>
    public bool TryRead(out NeteaseSignatureSample sample)
    {
        sample = default;

        if (!EnsureAttached())
            return false;

        lock (_gate)
        {
            if (_reader is null || _schedulePointer == IntPtr.Zero)
                return false;

            if (!TryReadDouble(_reader, _schedulePointer, out var position) ||
                !double.IsFinite(position) ||
                position < 0 ||
                position > MaximumPositionSeconds)
            {
                NoteReadFailure();
                return false;
            }

            _readFailures = 0;

            var duration = 0d;
            if (TryReadDouble(_reader, IntPtr.Add(_playerPointer, DurationOffset), out var rawDuration) &&
                double.IsFinite(rawDuration) &&
                rawDuration > 0 &&
                rawDuration <= MaximumPositionSeconds)
            {
                duration = rawDuration;
            }

            var status = NeteasePlayStatus.Waiting;
            if (TryReadInt32(_reader, IntPtr.Add(_playerPointer, StatusOffset), out var rawStatus) &&
                rawStatus is >= 0 and <= 4)
            {
                status = (NeteasePlayStatus)rawStatus;
            }

            sample = new NeteaseSignatureSample(position, duration, status, DateTimeOffset.UtcNow);
            return true;
        }
    }

    /// <summary>
    /// 读当前歌曲 id。它比"按曲名+歌手反查队列"可靠得多：这是播放器自己记的 id，同名曲再多也不会弄错。
    /// 内存里的字符串形如 <c>"&lt;songid&gt;_…"</c>，id 是下划线之前那一段（上游 `NetEase.cs:304-346`）。
    /// <para>
    /// 偏移是**试出来的**，不是假设的：`tools/SignatureProbe` 在本机这一版上确认了 `playInfo+0x10` 处是一个指针，
    /// 指向的缓冲里放着 `153784_IHZ0DX`（UTF-8）；但网易云换个版本就可能改成短字符串内联或换一层间接。所以这里
    /// 依次试"两次解引用"与"直接内联"三种读法，只接受能解出 `<数字…>_` 或纯数字的结果，全都解不出来就返回 false
    /// ——解不出 id 只是退回按曲名+歌手反查队列（那条路自己会校验歌手），绝不会给出一个错的 id。
    /// The offsets are **probed, not assumed**: SignatureProbe confirmed on this machine's build that the qword at
    /// playInfo+0x10 is a pointer whose buffer holds "153784_IHZ0DX" (UTF-8), but a NetEase update could inline a short
    /// string or add another level of indirection. Three readings are therefore tried in turn, and only a result that
    /// decodes to "&lt;digits&gt;_" or plain digits is accepted; if none does, this returns false — failing to read the id
    /// merely falls back to matching title and artist against the queue (which verifies the artist itself) and never
    /// produces a wrong id.
    /// </para>
    /// </summary>
    public bool TryGetSongId(out string songId)
    {
        songId = string.Empty;

        if (!EnsureAttached())
            return false;

        lock (_gate)
        {
            if (_reader is null || _playerPointer == IntPtr.Zero)
                return false;

            var playInfo = IntPtr.Add(_playerPointer, PlayInfoOffset);

            // ① 一次解引用：playInfo+0x10 直接是字符串缓冲。
            // ① One dereference: the qword at playInfo+0x10 is the string buffer itself.
            if (TryReadInt64(_reader, playInfo, out var buffer) && LooksLikePointer(buffer))
            {
                if (TryDecodeSongId(ReadBytes(_reader, new IntPtr(buffer), SongIdProbeBytes), out songId))
                    return true;
            }

            // ② 两次解引用：playInfo+0x10 指向一个对象，对象 +0x10 才是缓冲（本机实测的形状）。
            // ③ 内联：短字符串直接放在 playInfo+0x10 之后。
            // ② Two dereferences: the pointer leads to an object whose +0x10 is the buffer (what this machine showed).
            // ③ Inline: a short string sits right at playInfo+0x10.
            if (TryReadInt64(_reader, playInfo, out var first) && LooksLikePointer(first))
            {
                if (TryReadInt64(_reader, new IntPtr(first + 0x10), out var second) && LooksLikePointer(second) &&
                    TryDecodeSongId(ReadBytes(_reader, new IntPtr(second), SongIdProbeBytes), out songId))
                {
                    return true;
                }
            }

            return TryDecodeSongId(ReadBytes(_reader, playInfo, SongIdProbeBytes), out songId);
        }
    }

    private static bool LooksLikePointer(long value) => value is > 0x10000 and < 0x0000_7FFF_FFFF_FFFF;

    /// <summary>把读到的字节解成歌曲 id：先按 UTF-8（本机实测的编码），再按 UTF-16。/ Decodes the bytes into a song id, UTF-8 first (measuring shows that is what this build uses), then UTF-16.</summary>
    private static bool TryDecodeSongId(byte[] bytes, out string songId)
    {
        songId = string.Empty;
        if (bytes.Length == 0)
            return false;

        var utf8 = ExtractId(TrimAtNul(bytes, Encoding.UTF8));
        if (utf8.Length > 0)
        {
            songId = utf8;
            return true;
        }

        var utf16 = ExtractId(TrimAtNul(bytes, Encoding.Unicode));
        if (utf16.Length == 0)
            return false;

        songId = utf16;
        return true;
    }

    /// <summary>
    /// 从字符串开头取 id：必须是若干数字，后面跟着下划线或者就是结尾。至少要 4 位数字——按 UTF-16 误解 UTF-8 时会
    /// 得到"1"这样的单片字符，靠这条长度门槛挡掉，不会把它当成 id。
    /// Takes the id from the start of the string: digits followed by an underscore or the end. At least four digits are
    /// required, which rejects the single characters a UTF-16 misread of UTF-8 produces.
    /// </summary>
    private static string ExtractId(string text)
    {
        if (text.Length == 0)
            return string.Empty;

        var digits = 0;
        while (digits < text.Length && char.IsAsciiDigit(text[digits]))
            digits++;

        if (digits is < 4 or > 20)
            return string.Empty;

        if (digits < text.Length && text[digits] != '_')
            return string.Empty;

        return text[..digits];
    }

    /// <summary>
    /// 放开当前附加，允许下次调用重新匹配。/ Drops the current attach so the next call matches again.
    /// </summary>
    public void Reset()
    {
        lock (_gate)
        {
            _reader?.Dispose();
            _reader = null;
            _attached = false;
            _playerPointer = IntPtr.Zero;
            _schedulePointer = IntPtr.Zero;
            _readFailures = 0;
            _nextAttachTicks = 0;
        }
    }

    private void NoteReadFailure()
    {
        _readFailures++;
        if (_readFailures < ReadFailureLimit)
            return;

        Log.Warn("Media", $"网易云特征码通道连续 {_readFailures} 次读不到位置（播放器可能已重启），将重新匹配特征码。");
        _reader?.Dispose();
        _reader = null;
        _attached = false;
        _playerPointer = IntPtr.Zero;
        _schedulePointer = IntPtr.Zero;
        _readFailures = 0;
        _nextAttachTicks = 0;
    }

    /// <summary>必要时做一次附加（定位模块 → 匹配特征码 → 解析两个指针）。/ Attaches when needed: locate the module, match the signatures, resolve both pointers.</summary>
    private bool EnsureAttached()
    {
        lock (_gate)
        {
            if (_attached && _reader is not null && _schedulePointer != IntPtr.Zero)
                return true;

            if (Environment.TickCount64 < _nextAttachTicks)
                return false;
        }

        // 附加要读 26 MB 的 .text 并扫描一遍，不能在锁里做：读位置的循环和界面线程都会调到这里。
        // The attach reads and scans 26 MB of .text, so it must not run under the lock: both the position loop and the UI
        // thread can arrive here.
        var result = Attach();

        lock (_gate)
        {
            if (result is not { } attached)
            {
                _nextAttachTicks = Environment.TickCount64 + AttachFailureCooldownMilliseconds;
                return false;
            }

            _reader = attached.Reader;
            _playerPointer = attached.PlayerPointer;
            _schedulePointer = attached.SchedulePointer;
            _moduleDescription = attached.Description;
            _attached = true;
            _readFailures = 0;
            _nextAttachTicks = 0;
            return true;
        }
    }

    private (ProcessMemoryReader Reader, IntPtr PlayerPointer, IntPtr SchedulePointer, string Description)? Attach()
    {
        foreach (var process in FindCandidateProcesses())
        {
            using (process)
            {
                var reader = ProcessMemoryReader.TryOpen(process.Id);
                if (reader is null)
                    continue;

                try
                {
                    var module = FindPlayerModule(process, out var description);
                    if (module == IntPtr.Zero)
                        continue;

                    var section = FindTextSection(reader, module);
                    if (section is null)
                    {
                        Fail($"网易云特征码通道：候选进程读不到 {PlayerModuleName} 的 .text 段，换下一个候选。{description}");
                        continue;
                    }

                    // 段内容不整段读进来：流式扫描，理由见 ScanTextSection 的注释。
                    // The section is never read into one array: it is streamed, see the comment on ScanTextSection.
                    var (playerMatch, scheduleMatch) = ScanTextSection(
                        reader,
                        section.Value.Start,
                        section.Value.VirtualSize,
                        PlayerNeedle,
                        ScheduleNeedle);
                    if (playerMatch < 0 || scheduleMatch < 0)
                    {
                        // 这一条是"网易云升级后特征码失效"的唯一线索，必须说清楚是哪一条不匹配。注意这仍只是
                        // "这个候选不可用"：候选进程里有的加载了模块但指针不可用，全部候选都失败时才会打
                        // 最后那行"附加失败…退回内存扫描通道"。
                        // This is the only clue that a NetEase update invalidated the signatures, so name which one missed.
                        // It still only means this candidate is unusable; the final fallback line comes after all fail.
                        Fail(
                            $"网易云特征码在当前 {PlayerModuleName} 上未命中"
                            + $"（AudioPlayer {(playerMatch >= 0 ? "命中" : "未命中")}，AudioSchedule {(scheduleMatch >= 0 ? "命中" : "未命中")}），"
                            + $"该候选不可用。{description}");
                        continue;
                    }

                    // RIP 相对寻址：48 8D 0D <disp32> 的 disp 在 +3、指令结束在 +7；66 0F 2E 0D <disp32> 的 disp 在 +4。
                    // RIP-relative addressing: the disp32 of `48 8D 0D` sits at +3 with the instruction ending at +7; for
                    // `66 0F 2E 0D` the disp32 sits at +4.
                    var playerRip = IntPtr.Add(section.Value.Start, playerMatch + 3);
                    var scheduleRip = IntPtr.Add(section.Value.Start, scheduleMatch + 4);

                    if (!TryReadInt32(reader, playerRip, out var playerDisplacement) ||
                        !TryReadInt32(reader, scheduleRip, out var scheduleDisplacement))
                    {
                        Fail($"网易云特征码通道：候选进程读不到特征码处的相对位移，换下一个候选。{description}");
                        continue;
                    }

                    var playerPointer = IntPtr.Add(playerRip, playerDisplacement + sizeof(int));
                    var schedulePointer = IntPtr.Add(scheduleRip, scheduleDisplacement + sizeof(int));

                    if (!TryReadDouble(reader, schedulePointer, out var probe) ||
                        !double.IsFinite(probe) ||
                        probe < 0 ||
                        probe > MaximumPositionSeconds)
                    {
                        // 这条日志在本机每次启动都会出现一次：网易云常驻多个进程，先试的那个虽然加载了
                        // cloudmusic.dll，但指针处读出来的不是位置。措辞必须让人看出"换下一个候选"，
                        // 而不是"通道坏了"。
                        // This line appears on every start here: NetEase keeps several processes and the first one
                        // carries the module but not a usable pointer. The wording must read as "try the next
                        // candidate", not "the channel is broken".
                        Fail($"网易云特征码通道：候选进程的调度器读数不合理（{probe:0.###} 秒），换下一个候选。");
                        continue;
                    }

                    Log.Info("Media", $"网易云特征码通道已附加：{description}（调度器读数 {probe:0.###} 秒）。");
                    return (reader, playerPointer, schedulePointer, description);
                }
                catch (Exception ex)
                {
                    Fail($"网易云特征码通道附加失败: {ex.GetType().Name} {ex.Message}");
                }

                reader.Dispose();
            }
        }

        // 所有候选都试过了：这才是"特征码这条通道暂时不可用"，由调用方退回内存扫描通道。
        // Every candidate has been tried: only now is the signature channel itself unavailable, and the caller
        // falls back to the memory-scan channel.
        Fail("网易云特征码通道附加失败：所有候选进程都不可用，退回内存扫描通道。");

        return null;
    }

    /// <summary>只在失败原因变化时打一行日志。/ Logs a failure only when the reason changed.</summary>
    private void Fail(string message)
    {
        lock (_gate)
        {
            if (string.Equals(message, _lastFailure, StringComparison.Ordinal))
                return;

            _lastFailure = message;
        }

        Log.Info("Media", message);
    }

    private static List<Process> FindCandidateProcesses()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(PlayerProcessName);
        }
        catch (Exception ex)
        {
            Log.Warn("Media", $"枚举网易云进程失败: {ex.GetType().Name}");
            return [];
        }

        // 播放位置在 CEF 外壳进程里，它是私有内存最大的那个；小的几个是辅助进程。是否真的找得到模块，一路试过去就知道。
        // The position lives in the CEF shell process, the one with the largest private memory; the smaller ones are helpers.
        // Whether the module is really there is decided by trying each in turn.
        return processes
            .OrderByDescending(GetPrivateBytes)
            .Take(MaximumProcessCandidates)
            .ToList();
    }

    private static long GetPrivateBytes(Process process)
    {
        try
        {
            return process.PrivateMemorySize64;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>在目标进程里找播放器模块，顺带拼一段"路径 + 版本"的说明。/ Finds the player module and builds a path-and-version note.</summary>
    private static IntPtr FindPlayerModule(Process process, out string description)
    {
        description = string.Empty;

        try
        {
            foreach (ProcessModule module in process.Modules)
            {
                if (!string.Equals(module.ModuleName, PlayerModuleName, StringComparison.OrdinalIgnoreCase))
                    continue;

                description = $"{module.FileName}";
                try
                {
                    var info = new FileInfo(module.FileName);
                    var version = FileVersionInfo.GetVersionInfo(module.FileName);
                    description += $"（{version.FileVersion ?? "版本未知"}，{info.LastWriteTime:yyyy-MM-dd}）";
                }
                catch (Exception)
                {
                    // 版本信息拿不到不影响用它读位置。
                    // Failing to read the version does not stop the position being read.
                }

                return module.BaseAddress;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Media", $"枚举网易云模块失败: {ex.GetType().Name} {ex.Message}");
        }

        return IntPtr.Zero;
    }

    /// <summary>按 PE 头找到 .text 段的位置与长度。/ Locates the .text section's address and length through the PE headers.</summary>
    private static (IntPtr Start, int VirtualSize)? FindTextSection(ProcessMemoryReader reader, IntPtr moduleBase)
    {
        if (!TryReadInt32(reader, IntPtr.Add(moduleBase, 0x3C), out var ntOffset))
            return null;

        var ntHeader = IntPtr.Add(moduleBase, ntOffset);
        if (!TryReadInt16(reader, IntPtr.Add(ntHeader, 6), out var sectionCount) ||
            !TryReadInt16(reader, IntPtr.Add(ntHeader, 20), out var optionalHeaderSize))
        {
            return null;
        }

        var sectionHeader = IntPtr.Add(ntHeader, 24 + optionalHeaderSize);
        var name = new byte[8];

        for (var i = 0; i < sectionCount; i++)
        {
            var cursor = IntPtr.Add(sectionHeader, i * 40);
            if (reader.Read(cursor, name) != name.Length)
                return null;

            if (Encoding.ASCII.GetString(name).TrimEnd('\0') != ".text")
                continue;

            if (!TryReadInt32(reader, IntPtr.Add(cursor, 8), out var virtualSize) ||
                !TryReadInt32(reader, IntPtr.Add(cursor, 12), out var virtualAddress) ||
                virtualSize <= 0)
            {
                return null;
            }

            return (IntPtr.Add(moduleBase, virtualAddress), virtualSize);
        }

        return null;
    }

    /// <summary>
    /// 在 .text 段里流式找两条特征码，返回它们在段内的偏移（没找到是 -1）。
    /// <para>
    /// 这里刻意**不**把整段读成一个数组：本机实测该段 27,188,323 字节（25.9 MB），整段读会让私有提交在附加那一刻
    /// 一次性多出约 55 MB（20 毫秒采样到的台阶），而那个大数组是 LOH 垃圾，不触发 gen 2 回收就一直算在常驻内存里；
    /// 候选进程里第一个位置读数不合理时还会再整读一遍。改成"一个 4 MB 缓冲反复用 + 块与块之间留 needle-1 字节重叠"
    /// 之后，触及的页面从约 31 MB 降到约 4 MB，也不再产生 LOH 垃圾。
    /// </para>
    /// Streams both signatures through the .text section, returning their offsets inside it (-1 when missing).
    /// <para>
    /// The section is deliberately **not** read into one array: on this machine it measures 27,188,323 bytes (25.9 MB) and
    /// reading it whole added roughly 55 MB to the private commit in a single step at attach time (sampled at 20 ms), while the
    /// resulting large-object-heap array stays resident until a gen 2 collection — and a first candidate whose position reads
    /// implausibly makes it read the whole thing a second time. One 4 MB buffer reused with a needle-1 byte overlap between
    /// chunks cuts the touched pages from about 31 MB to about 4 MB and produces no LOH garbage at all.
    /// </para>
    /// </summary>
    private static (int Player, int Schedule) ScanTextSection(
        ProcessMemoryReader reader,
        IntPtr start,
        int virtualSize,
        (byte Value, bool Wildcard)[] playerNeedle,
        (byte Value, bool Wildcard)[] scheduleNeedle)
    {
        var longestNeedle = Math.Max(playerNeedle.Length, scheduleNeedle.Length);
        var overlap = Math.Max(1, longestNeedle - 1);
        var chunk = new byte[PatternChunkBytes];

        // bridge 前部放"上一块的尾巴"、后面接"这一块的头"，用来接上跨块的特征码；特征码只有几十字节，所以它很小。
        // The bridge holds the previous chunk's tail followed by this chunk's head, which is what catches a needle spanning
        // two chunks; the needles are tens of bytes, so the bridge stays tiny.
        var bridge = new byte[longestNeedle * 2];

        var player = -1;
        var schedule = -1;
        var offset = 0;
        var tail = 0;

        while (offset < virtualSize && (player < 0 || schedule < 0))
        {
            var want = Math.Min(PatternChunkBytes, virtualSize - offset);
            var read = reader.Read(IntPtr.Add(start, offset), chunk, want);
            if (read <= 0)
                break;

            if (tail > 0)
            {
                var head = Math.Min(overlap, read);
                Buffer.BlockCopy(chunk, 0, bridge, tail, head);
                ScanForNeedles(bridge, tail + head, offset - tail, playerNeedle, scheduleNeedle, ref player, ref schedule);
            }

            ScanForNeedles(chunk, read, offset, playerNeedle, scheduleNeedle, ref player, ref schedule);

            tail = Math.Min(overlap, read);
            Buffer.BlockCopy(chunk, read - tail, bridge, 0, tail);
            offset += read;

            if (read < want)
            {
                // 段尾之外可能读不到；已经扫过的部分照样算数。
                // The tail may lie past the readable range; whatever was scanned still counts.
                break;
            }
        }

        return (player, schedule);
    }

    /// <summary>在这段字节上补齐还没找到的那条特征码；<paramref name="baseOffset"/> 是这段字节在段内的起点。/ Fills in whichever needle is still missing; baseOffset is where these bytes start inside the section.</summary>
    private static void ScanForNeedles(
        byte[] bytes,
        int length,
        int baseOffset,
        (byte Value, bool Wildcard)[] playerNeedle,
        (byte Value, bool Wildcard)[] scheduleNeedle,
        ref int player,
        ref int schedule)
    {
        if (player < 0)
        {
            var index = FindPattern(bytes, length, playerNeedle);
            if (index >= 0)
                player = baseOffset + index;
        }

        if (schedule < 0)
        {
            var index = FindPattern(bytes, length, scheduleNeedle);
            if (index >= 0)
                schedule = baseOffset + index;
        }
    }

    private static (byte Value, bool Wildcard)[] ParsePattern(string pattern)
        => pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Contains('?') ? ((byte)0, true) : (Convert.ToByte(token, 16), false))
            .ToArray();

    /// <summary>
    /// 朴素扫描：只在首字节上做跳过，而且只看前 <paramref name="length"/> 个字节——缓冲区是复用的，尾部的旧数据不算数。
    /// A naive scan that only skips on the first byte and only looks at the first <paramref name="length"/> bytes, because the
    /// buffer is reused and whatever sits past that point is stale.
    /// </summary>
    private static int FindPattern(byte[] haystack, int length, (byte Value, bool Wildcard)[] needle)
    {
        var last = length - needle.Length;
        for (var i = 0; i <= last; i++)
        {
            if (!needle[0].Wildcard && haystack[i] != needle[0].Value)
                continue;

            var found = true;
            for (var j = 1; j < needle.Length; j++)
            {
                if (needle[j].Wildcard || haystack[i + j] == needle[j].Value)
                    continue;

                found = false;
                break;
            }

            if (found)
                return i;
        }

        return -1;
    }

    private static byte[] ReadBytes(ProcessMemoryReader reader, IntPtr address, int count)
    {
        var buffer = new byte[count];
        var read = reader.Read(address, buffer, count);
        if (read != count)
            Array.Resize(ref buffer, Math.Max(0, read));

        return buffer;
    }

    private static string TrimAtNul(byte[] bytes, Encoding encoding)
    {
        var text = encoding.GetString(bytes);
        var end = text.IndexOf('\0');
        return (end >= 0 ? text[..end] : text).Trim();
    }

    private static bool TryReadDouble(ProcessMemoryReader reader, IntPtr address, out double value)
    {
        value = 0;
        var buffer = new byte[sizeof(double)];
        if (reader.Read(address, buffer, buffer.Length) != buffer.Length)
            return false;

        value = BitConverter.ToDouble(buffer, 0);
        return true;
    }

    private static bool TryReadInt64(ProcessMemoryReader reader, IntPtr address, out long value)
    {
        value = 0;
        var buffer = new byte[sizeof(long)];
        if (reader.Read(address, buffer, buffer.Length) != buffer.Length)
            return false;

        value = BitConverter.ToInt64(buffer, 0);
        return true;
    }

    private static bool TryReadInt32(ProcessMemoryReader reader, IntPtr address, out int value)
    {
        value = 0;
        var buffer = new byte[sizeof(int)];
        if (reader.Read(address, buffer, buffer.Length) != buffer.Length)
            return false;

        value = BitConverter.ToInt32(buffer, 0);
        return true;
    }

    private static bool TryReadInt16(ProcessMemoryReader reader, IntPtr address, out short value)
    {
        value = 0;
        var buffer = new byte[sizeof(short)];
        if (reader.Read(address, buffer, buffer.Length) != buffer.Length)
            return false;

        value = BitConverter.ToInt16(buffer, 0);
        return true;
    }

    public void Dispose() => Reset();
}
