using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SignatureProbe;

/// <summary>
/// 网易云特征码位置通道的可行性自检。回答 HANDOFF-NETEASE-SYNC.md §10 里悬着的两个问题：
/// ① 用户当前这版 cloudmusic.dll 里，上游 AF Media Bar 用的那两条特征码还匹不匹配？
/// ② 匹配出来的指针指向的 double，到底是不是"当前播放位置"？
/// Feasibility probe for the signature-based NetEase position channel. It answers the two open questions from the
/// handoff document: do the two signatures still match the user's current cloudmusic.dll, and is the double behind
/// the resolved pointer really the playback position?
/// </summary>
internal static class Program
{
    // 与上游 AF Media Bar 逐字节一致（NetEase.cs:41-44）。上游把这两条特征码归功于
    // Kxnrl/NetEase-Cloud-Music-DiscordRPC；在确认那份代码的许可之前，本探针只留在开发仓库里，不进公开仓库。
    // Byte-for-byte from upstream AF Media Bar (NetEase.cs:41-44), which credits
    // Kxnrl/NetEase-Cloud-Music-DiscordRPC. Until that licence is confirmed this probe stays in the development
    // repository only and does NOT go to the public archive.
    private const string AudioPlayerPattern
        = "48 8D 0D ? ? ? ? E8 ? ? ? ? 48 8D 0D ? ? ? ? E8 ? ? ? ? 90 48 8D 0D ? ? ? ? E8 ? ? ? ? 48 8D 05 ? ? ? ? 48 8D A5 ? ? ? ? 5F 5D C3 CC CC CC CC CC 48 89 4C 24 ? 55 57 48 81 EC ? ? ? ? 48 8D 6C 24 ? 48 8D 7C 24";

    private const string AudioSchedulePattern = "66 0F 2E 0D ? ? ? ? 7A ? 75 ? 66 0F 2E 15";

    private const int SampleCount = 8;
    private const int SampleIntervalMilliseconds = 1000;
    private const int ChunkBytes = 4 * 1024 * 1024;

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== 网易云特征码位置通道：可行性自检 ===");

        var pid = FindNetEaseProcess(out var how);
        if (pid <= 0)
        {
            Console.WriteLine("没有找到网易云进程（窗口类 OrpheusBrowserHost 与进程名 cloudmusic 都没命中）。");
            Console.WriteLine("SKIP：请先启动网易云音乐并播放一首歌，再跑这个探针。");
            return 0;
        }

        Console.WriteLine($"网易云进程: pid={pid}（{how}）");

        using var reader = ProcessMemory.Open(pid);
        if (reader is null)
        {
            Console.WriteLine($"OpenProcess 失败（Win32 {Marshal.GetLastWin32Error()}）。");
            Console.WriteLine("RESULT: FAIL（无法读取进程内存，可能需要以同样权限运行）");
            return 1;
        }

        var module = FindModule(reader, "cloudmusic.dll");
        if (module.Base == IntPtr.Zero)
        {
            Console.WriteLine("没有在目标进程里找到 cloudmusic.dll 模块。");
            Console.WriteLine("RESULT: FAIL");
            return 1;
        }

        Console.WriteLine($"模块: {module.Path}");
        Console.WriteLine($"  基址 0x{module.Base.ToInt64():X}，模块大小 {module.Size / (1024.0 * 1024):0.0} MB");

        if (File.Exists(module.Path))
        {
            var info = new FileInfo(module.Path);
            var version = FileVersionInfo.GetVersionInfo(module.Path);
            Console.WriteLine($"  文件版本 {version.FileVersion}（{info.LastWriteTime:yyyy-MM-dd HH:mm}，{info.Length / (1024.0 * 1024):0.0} MB）");
        }

        var text = ReadTextSection(reader, module.Base);
        if (text is null)
        {
            Console.WriteLine("按 PE 头定位 .text 段失败。");
            Console.WriteLine("RESULT: FAIL");
            return 1;
        }

        Console.WriteLine($"  .text: 0x{text.Value.Start.ToInt64():X} 起，实际读到 {text.Value.Bytes.Length / (1024.0 * 1024):0.0} MB");

        var playerNeedle = ParsePattern(AudioPlayerPattern);
        var scheduleNeedle = ParsePattern(AudioSchedulePattern);
        var playerMatch = FindPattern(text.Value.Bytes, text.Value.Bytes.Length, playerNeedle);
        var scheduleMatch = FindPattern(text.Value.Bytes, text.Value.Bytes.Length, scheduleNeedle);

        Console.WriteLine();
        Console.WriteLine($"[1] AudioPlayerPattern  ：{(playerMatch >= 0 ? $"命中（.text +0x{playerMatch:X}）" : "未命中")}");
        Console.WriteLine($"[2] AudioSchedulePattern：{(scheduleMatch >= 0 ? $"命中（.text +0x{scheduleMatch:X}）" : "未命中")}");

        if (playerMatch < 0 || scheduleMatch < 0)
        {
            Console.WriteLine();
            Console.WriteLine("特征码已经不匹配当前版本的 cloudmusic.dll —— 上游那套偏移不能直接照抄，" +
                              "要么按当前版本重新做特征，要么继续走现有的内存扫描。");
            Console.WriteLine("RESULT: FAIL（特征码失效）");
            return 1;
        }

        // 生产实现不整段读 .text：v1.5.1 起改成一个 4 MB 缓冲流式扫，好省掉附加时那 55 MB 私有提交。
        // 这里用同一份 .text 数据，把块大小压到刚好让特征码跨块，验证流式扫描和整段扫描落在同一个偏移上。
        // Production no longer reads .text whole: since v1.5.1 it streams through one 4 MB buffer, which saves the 55 MB of
        // private commit the attach used to add. This re-runs a mirror of that streaming scan over the same .text with a
        // chunk size chosen so the needle straddles a boundary, and asserts it lands on the same offset.
        Console.WriteLine();
        Console.WriteLine("[2b] 流式扫描自检（跨块的特征码也要命中）");
        var streamPass = true;
        foreach (var chunkSize in new[]
                 {
                     4096,
                     playerMatch + 1,
                     playerMatch + 8,
                     playerMatch + 19,
                     playerMatch + playerNeedle.Length - 1,
                     scheduleMatch + 1,
                     scheduleMatch + scheduleNeedle.Length - 1
                 })
        {
            var streamedPlayer = ScanStreaming(text.Value.Bytes, playerNeedle, chunkSize);
            var streamedSchedule = ScanStreaming(text.Value.Bytes, scheduleNeedle, chunkSize);
            var same = streamedPlayer == playerMatch && streamedSchedule == scheduleMatch;
            streamPass &= same;
            Console.WriteLine($"  块 {chunkSize,9} 字节：player +0x{streamedPlayer:X}（期望 +0x{playerMatch:X}）" +
                              $"，schedule +0x{streamedSchedule:X}（期望 +0x{scheduleMatch:X}） —— {(same ? "一致" : "不一致")}");
        }

        if (!streamPass)
        {
            Console.WriteLine("流式扫描与整段扫描结果不一致 —— 跨块拼接有问题。");
            Console.WriteLine("RESULT: FAIL（流式扫描自检）");
            return 1;
        }

        // RIP 相对寻址：48 8D 0D <disp32> 的 disp 在 +3、指令结束在 +7；66 0F 2E 0D <disp32> 的 disp 在 +4。
        // RIP-relative addressing: the disp32 of `48 8D 0D` sits at +3 with the instruction ending at +7; for
        // `66 0F 2E 0D` the disp32 sits at +4.
        var playerRip = IntPtr.Add(text.Value.Start, playerMatch + 3);
        var scheduleRip = IntPtr.Add(text.Value.Start, scheduleMatch + 4);

        if (!reader.TryReadInt32(playerRip, out var playerDisplacement) ||
            !reader.TryReadInt32(scheduleRip, out var scheduleDisplacement))
        {
            Console.WriteLine("读取特征码处的相对位移失败。");
            Console.WriteLine("RESULT: FAIL");
            return 1;
        }

        var playerPointer = IntPtr.Add(playerRip, playerDisplacement + sizeof(int));
        var schedulePointer = IntPtr.Add(scheduleRip, scheduleDisplacement + sizeof(int));

        Console.WriteLine();
        Console.WriteLine($"[3] AudioPlayer 指针 = 0x{playerPointer.ToInt64():X}" +
                          $"（指令 0x{IntPtr.Add(text.Value.Start, playerMatch).ToInt64():X}，位移 {playerDisplacement}）");
        Console.WriteLine($"[4] Scheduler   指针 = 0x{schedulePointer.ToInt64():X}" +
                          $"（指令 0x{IntPtr.Add(text.Value.Start, scheduleMatch).ToInt64():X}，位移 {scheduleDisplacement}）");

        if (!reader.TryReadDouble(schedulePointer, out _))
        {
            Console.WriteLine("调度器地址读不出来 —— 指针解析有问题。");
            Console.WriteLine("RESULT: FAIL");
            return 1;
        }

        // 再看一眼当前歌曲 id（上游 NetEase.cs:304-346：播放信息指针在 +0x50，里面 +0x10 是个 std::wstring）。
        // 它能证明"这个位置属于当前这首歌"，而不是某个全局计时器 —— 上一次扫内存就是栽在这一点上。
        // Also print the current song id (upstream NetEase.cs:304-346: the play-info pointer sits at +0x50 and holds a
        // std::wstring at +0x10). It proves the position belongs to the current track rather than to some global
        // timer — exactly the trap the memory scanner fell into.
        var songId = ReadSongId(reader, playerPointer, out var idRaw);
        Console.WriteLine();
        Console.WriteLine($"[4b] 当前歌曲 id: {(songId.Length > 0 ? songId : "（读不到）")}");
        if (idRaw.Length > 0)
        {
            Console.WriteLine($"     原始字符串: {idRaw}");
        }

        Console.WriteLine();
        Console.WriteLine($"[5] 连续采样（每 {SampleIntervalMilliseconds / 1000.0:0.0} 秒一次，共 {SampleCount} 次）：位置 / 时长 / 状态 / 音量");

        var samples = new List<double>();
        var duration = double.NaN;
        var status = int.MinValue;
        var voltage64 = 0f;
        var voltage68 = 0f;
        var wall = Stopwatch.StartNew();

        for (var i = 0; i < SampleCount; i++)
        {
            if (!reader.TryReadDouble(schedulePointer, out var position))
            {
                Console.WriteLine($"  #{i + 1} 读不到调度器值，采样中止。");
                break;
            }

            reader.TryReadDouble(IntPtr.Add(playerPointer, 0xa8), out duration);
            reader.TryReadInt32(IntPtr.Add(playerPointer, 0x60), out status);
            reader.TryReadFloat(IntPtr.Add(playerPointer, 0x64), out voltage64);
            reader.TryReadFloat(IntPtr.Add(playerPointer, 0x68), out voltage68);

            var delta = samples.Count == 0 ? double.NaN : position - samples[^1];
            samples.Add(position);
            Console.WriteLine($"  #{i + 1} 位置 {position,10:0.000} 秒（Δ {(double.IsNaN(delta) ? "—" : delta.ToString("0.000")),7}）" +
                              $"  时长 {duration,8:0.000} 秒  状态 {status}（{StatusName(status)}）  音量 {voltage64:0.00}/{voltage68:0.00}");

            if (i < SampleCount - 1)
            {
                Thread.Sleep(SampleIntervalMilliseconds);
            }
        }

        wall.Stop();

        if (samples.Count < 3)
        {
            Console.WriteLine("采样次数太少，无法判断。");
            Console.WriteLine("RESULT: FAIL");
            return 1;
        }

        var span = wall.Elapsed.TotalSeconds;
        var changed = samples[^1] - samples[0];
        var ratio = changed / span;

        Console.WriteLine();
        Console.WriteLine("[6] 判定");
        Console.WriteLine($"  位置从 {samples[0]:0.000} 秒推进到 {samples[^1]:0.000} 秒" +
                          $"（{span:0.0} 秒内推进 {changed:0.000} 秒，速率 {ratio:0.00}×）");

        var finite = double.IsFinite(samples[0]) && double.IsFinite(samples[^1]);
        var inRange = samples[0] >= 0 && samples[0] <= 21600;
        var advancing = ratio is > 0.8 and < 1.25;
        var insideDuration = !double.IsFinite(duration) || duration <= 0 || samples[^1] <= duration + 1;
        var statusSane = status is >= 0 and <= 4;

        Console.WriteLine($"  值有限且落在 0–6 小时: {(finite && inRange ? "是" : "否")}");
        Console.WriteLine($"  采样期间按 1 倍速前进: {(advancing ? "是" : "否")}（暂停或未在播放时不算失败）");
        Console.WriteLine($"  落在上报时长之内: {(insideDuration ? "是" : "否")}（时长 {duration:0.000} 秒）");
        Console.WriteLine($"  状态字段在 0–4: {(statusSane ? "是" : "否")}（{status}）");

        Console.WriteLine();
        if (finite && inRange && insideDuration && statusSane)
        {
            Console.WriteLine(advancing
                ? "结论：特征码在当前 cloudmusic.dll 上仍然匹配，调度器里的 double 就是当前播放位置，固定偏移也能读出时长与状态。"
                : "结论：特征码匹配、字段布局合理，但采样期间位置没有按 1 倍速前进（可能处于暂停，或这次播放已停止）。");
            Console.WriteLine("RESULT: PASS");
            return 0;
        }

        Console.WriteLine("结论：特征码匹配，但读到的值不像是播放位置（详见上面各项判定）。");
        Console.WriteLine("RESULT: FAIL");
        return 1;
    }

    /// <summary>优先按窗口类名定位（上游做法），找不到再退回进程名。/ Window class first (upstream's way), then process name.</summary>
    private static int FindNetEaseProcess(out string how)
    {
        var handle = NativeMethods.FindWindowW("OrpheusBrowserHost", null);
        if (handle != IntPtr.Zero)
        {
            NativeMethods.GetWindowThreadProcessId(handle, out var owner);
            if (owner > 0)
            {
                how = "窗口类 OrpheusBrowserHost";
                return (int)owner;
            }
        }

        var candidates = Process.GetProcessesByName("cloudmusic");
        try
        {
            var best = candidates
                .OrderByDescending(candidate =>
                {
                    try
                    {
                        return candidate.WorkingSet64;
                    }
                    catch
                    {
                        return 0L;
                    }
                })
                .FirstOrDefault();

            if (best is not null)
            {
                how = "进程名 cloudmusic（按工作集取最大）";
                return best.Id;
            }
        }
        finally
        {
            foreach (var candidate in candidates)
            {
                candidate.Dispose();
            }
        }

        how = string.Empty;
        return 0;
    }

    private static ModuleInfo FindModule(ProcessMemory reader, string name)
    {
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.Th32CsSnapModule, (uint)reader.ProcessId);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
        {
            return default;
        }

        try
        {
            var entry = new ModuleEntry { Size = (uint)Marshal.SizeOf<ModuleEntry>() };
            if (!NativeMethods.Module32FirstW(snapshot, ref entry))
            {
                return default;
            }

            do
            {
                if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return new ModuleInfo(entry.Base, entry.ModuleSize, entry.Path);
                }
            }
            while (NativeMethods.Module32NextW(snapshot, ref entry));

            return default;
        }
        finally
        {
            NativeMethods.CloseHandle(snapshot);
        }
    }

    /// <summary>按 PE 头找到 .text 段，分块读出来（一次读几十 MB 会只读到一部分）。/ Locates .text through the PE headers and reads it in chunks.</summary>
    private static (IntPtr Start, byte[] Bytes)? ReadTextSection(ProcessMemory reader, IntPtr moduleBase)
    {
        if (!reader.TryReadInt32(IntPtr.Add(moduleBase, 0x3C), out var ntOffset))
        {
            return null;
        }

        var ntHeader = IntPtr.Add(moduleBase, ntOffset);
        if (!reader.TryReadInt16(IntPtr.Add(ntHeader, 6), out var sectionCount) ||
            !reader.TryReadInt16(IntPtr.Add(ntHeader, 20), out var optionalHeaderSize))
        {
            return null;
        }

        var sectionHeader = IntPtr.Add(ntHeader, 24 + optionalHeaderSize);
        var name = new byte[8];

        for (var i = 0; i < sectionCount; i++)
        {
            var cursor = IntPtr.Add(sectionHeader, i * 40);
            if (reader.Read(cursor, name) != name.Length)
            {
                return null;
            }

            if (Encoding.ASCII.GetString(name).TrimEnd('\0') != ".text")
            {
                continue;
            }

            if (!reader.TryReadInt32(IntPtr.Add(cursor, 8), out var virtualSize) ||
                !reader.TryReadInt32(IntPtr.Add(cursor, 12), out var virtualAddress) ||
                virtualSize <= 0)
            {
                return null;
            }

            var start = IntPtr.Add(moduleBase, virtualAddress);
            var bytes = ReadChunks(reader, start, virtualSize);
            return bytes is null ? null : (start, bytes);
        }

        return null;
    }

    private static byte[]? ReadChunks(ProcessMemory reader, IntPtr start, int size)
    {
        var buffer = new byte[size];
        var offset = 0;

        while (offset < size)
        {
            var chunk = Math.Min(ChunkBytes, size - offset);
            var slice = new byte[chunk];
            var read = reader.Read(IntPtr.Add(start, offset), slice);
            if (read <= 0)
            {
                // 段尾之外可能读不到；把前面读到的部分留下，扫描照样能跑。
                // The tail may lie past the readable range; keep what we have and scan that.
                Array.Resize(ref buffer, offset);
                break;
            }

            Array.Copy(slice, 0, buffer, offset, read);
            offset += read;

            if (read < chunk)
            {
                Array.Resize(ref buffer, offset);
                break;
            }
        }

        return offset == 0 ? null : buffer;
    }

    /// <summary>读当前歌曲 id：+0x50 是播放信息指针，它 +0x10 处是个 std::wstring。/ Reads the current song id from the play-info pointer.</summary>
    private static string ReadSongId(ProcessMemory reader, IntPtr playerPointer, out string raw)
    {
        raw = string.Empty;
        if (!reader.TryReadInt64(IntPtr.Add(playerPointer, 0x50), out var playInfo) || playInfo == 0)
        {
            return string.Empty;
        }

        var strPointer = new IntPtr(playInfo + 0x10);
        if (!reader.TryReadInt64(IntPtr.Add(strPointer, 0x10), out var length) || length <= 0 || length > 512)
        {
            return string.Empty;
        }

        // 长度字段在不同构建里可能是"字符数"也可能是"字节数"，而 SSO 的缓冲就在 strPointer 本身。
        // The length field may count characters or bytes depending on the build; the SSO buffer starts at strPointer.
        var byteCount = (int)Math.Min(256, Math.Max(length, length * 2));
        byte[] bytes;
        if (length <= 15)
        {
            bytes = reader.ReadBytes(strPointer, byteCount);
        }
        else if (reader.TryReadInt64(strPointer, out var heap) && heap != 0)
        {
            bytes = reader.ReadBytes(new IntPtr(heap), byteCount);
        }
        else
        {
            return string.Empty;
        }

        var asUtf8 = TrimAtNul(bytes, Encoding.UTF8);
        var asUtf16 = TrimAtNul(bytes, Encoding.Unicode);
        var digits8 = CountDigits(asUtf8);
        var digits16 = CountDigits(asUtf16);
        var text = digits16 > digits8 ? asUtf16 : asUtf8;
        raw = $"{text}（{(digits16 > digits8 ? "UTF-16" : "UTF-8")}）";

        var separator = text.IndexOf('_');
        var candidate = separator > 0 ? text[..separator] : text;
        var match = Regex.Match(candidate, "[0-9]{3,}");
        return match.Success ? match.Value : candidate.Trim();
    }

    private static string StatusName(int status) => status switch
    {
        0 => "Waiting",
        1 => "Playing",
        2 => "Paused",
        3 => "Unknown3",
        4 => "Unknown4",
        _ => "不在 0–4 之内",
    };

    private static string TrimAtNul(byte[] bytes, Encoding encoding)
    {
        var text = encoding.GetString(bytes);
        var end = text.IndexOf('\0');
        return (end >= 0 ? text[..end] : text).Trim();
    }

    private static int CountDigits(string text)
    {
        var count = 0;
        foreach (var character in text)
        {
            if (char.IsAsciiDigit(character))
            {
                count++;
            }
        }

        return count;
    }

    private static (byte Value, bool Wildcard)[] ParsePattern(string pattern)        => pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Contains('?') ? ((byte)0, true) : (Convert.ToByte(token, 16), false))
            .ToArray();

    /// <summary>朴素扫描：只在首字节上做跳过，和上游 Memory.FindPattern 同样的策略。/ Naive scan skipping on the first byte, upstream's strategy.</summary>
    private static int FindPattern(byte[] haystack, int length, (byte Value, bool Wildcard)[] needle)
    {
        var last = length - needle.Length;
        for (var i = 0; i <= last; i++)
        {
            if (!needle[0].Wildcard && haystack[i] != needle[0].Value)
            {
                continue;
            }

            var found = true;
            for (var j = 1; j < needle.Length; j++)
            {
                if (needle[j].Wildcard || haystack[i + j] == needle[j].Value)
                {
                    continue;
                }

                found = false;
                break;
            }

            if (found)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 生产实现（NeteaseSignatureChannel.ScanTextSection）的镜像：一个复用缓冲块块扫，块间留 needle-1 字节重叠，
    /// 用于验证跨块的特征码不会漏。块大小在这里是参数，好把它压到特征码正好骑在块边界上。
    /// A mirror of the production scan (NeteaseSignatureChannel.ScanTextSection): one reused buffer chunk by chunk with a
    /// needle-1 byte overlap between chunks, here with the chunk size as a parameter so the needle can be forced to straddle
    /// a boundary.
    /// </summary>
    private static int ScanStreaming(byte[] haystack, (byte Value, bool Wildcard)[] needle, int chunkSize)
    {
        var overlap = Math.Max(1, needle.Length - 1);
        var chunk = new byte[chunkSize];
        var bridge = new byte[needle.Length * 2];
        var offset = 0;
        var tail = 0;

        while (offset < haystack.Length)
        {
            var want = Math.Min(chunkSize, haystack.Length - offset);
            Buffer.BlockCopy(haystack, offset, chunk, 0, want);

            if (tail > 0)
            {
                var head = Math.Min(overlap, want);
                Buffer.BlockCopy(chunk, 0, bridge, tail, head);
                var bridged = FindPattern(bridge, tail + head, needle);
                if (bridged >= 0)
                {
                    return offset - tail + bridged;
                }
            }

            var here = FindPattern(chunk, want, needle);
            if (here >= 0)
            {
                return offset + here;
            }

            tail = Math.Min(overlap, want);
            Buffer.BlockCopy(chunk, want - tail, bridge, 0, tail);
            offset += want;
        }

        return -1;
    }

    private readonly record struct ModuleInfo(IntPtr Base, uint Size, string Path);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ModuleEntry
    {
        public uint Size;
        public uint ModuleId;
        public uint ProcessId;
        public uint GlobalUsage;
        public uint ProcessUsage;
        public IntPtr Base;
        public uint ModuleSize;
        public IntPtr Handle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Name;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string Path;
    }
}

/// <summary>目标进程的只读内存视图：带长度校验的读，失败可区分。/ A read-only view of the target process with checked reads.</summary>
internal sealed class ProcessMemory : IDisposable
{
    private readonly IntPtr _handle;
    private bool _disposed;

    private ProcessMemory(IntPtr handle, int processId)
    {
        _handle = handle;
        ProcessId = processId;
    }

    public int ProcessId { get; }

    public static ProcessMemory? Open(int processId)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.ProcessQueryInformation | NativeMethods.ProcessVmRead,
            false,
            processId);

        return handle == IntPtr.Zero ? null : new ProcessMemory(handle, processId);
    }

    public int Read(IntPtr address, byte[] buffer)
        => _disposed
            ? 0
            : NativeMethods.ReadProcessMemory(_handle, address, buffer, (IntPtr)buffer.Length, out var read)
                ? (int)read.ToInt64()
                : 0;

    public byte[] ReadBytes(IntPtr address, int count)
    {
        var buffer = new byte[count];
        var read = Read(address, buffer);
        if (read != count)
        {
            Array.Resize(ref buffer, Math.Max(0, read));
        }

        return buffer;
    }

    public bool TryReadInt16(IntPtr address, out short value) => TryRead(address, 2, bytes => BitConverter.ToInt16(bytes, 0), out value);
    public bool TryReadInt32(IntPtr address, out int value) => TryRead(address, 4, bytes => BitConverter.ToInt32(bytes, 0), out value);
    public bool TryReadInt64(IntPtr address, out long value) => TryRead(address, 8, bytes => BitConverter.ToInt64(bytes, 0), out value);
    public bool TryReadFloat(IntPtr address, out float value) => TryRead(address, 4, bytes => BitConverter.ToSingle(bytes, 0), out value);
    public bool TryReadDouble(IntPtr address, out double value) => TryRead(address, 8, bytes => BitConverter.ToDouble(bytes, 0), out value);

    private bool TryRead<T>(IntPtr address, int size, Func<byte[], T> convert, out T value)
    {
        value = default!;
        if (_disposed)
        {
            return false;
        }

        var buffer = new byte[size];
        if (!NativeMethods.ReadProcessMemory(_handle, address, buffer, (IntPtr)size, out var read) ||
            read.ToInt64() != size)
        {
            return false;
        }

        value = convert(buffer);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NativeMethods.CloseHandle(_handle);
    }
}

internal static class NativeMethods
{
    public const uint ProcessQueryInformation = 0x0400;
    public const uint ProcessVmRead = 0x0010;
    public const uint Th32CsSnapModule = 0x00000008;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] buffer, IntPtr size, out IntPtr bytesRead);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Module32FirstW(IntPtr snapshot, ref Program.ModuleEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Module32NextW(IntPtr snapshot, ref Program.ModuleEntry entry);
}
