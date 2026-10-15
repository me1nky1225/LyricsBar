using System.Runtime.InteropServices;
using LyricsBar.Classes.Utils;

namespace LyricsBar.Classes.Interop;

/// <summary>
/// 跨进程读内存所需的 Win32 声明。只声明查询、枚举区域、读取三件事——写入接口故意不声明：
/// 本项目只需要"看"播放器的播放位置，不需要（也绝不应该）改动别的进程。
/// The Win32 declarations needed to read another process's memory. Only querying, region enumeration and reading are declared;
/// the write call is deliberately absent, because this project only needs to *look at* the player's playback position and must
/// never modify another process.
/// </summary>
internal static class ProcessMemoryNative
{
    internal const uint ProcessQueryInformation = 0x0400;
    internal const uint ProcessVmRead = 0x0010;

    internal const uint MemCommit = 0x1000;
    internal const uint MemPrivate = 0x20000;
    internal const uint MemMapped = 0x40000;

    internal const uint PageNoAccess = 0x01;
    internal const uint PageReadOnly = 0x02;
    internal const uint PageReadWrite = 0x04;
    internal const uint PageWriteCopy = 0x08;
    internal const uint PageExecuteRead = 0x20;
    internal const uint PageExecuteReadWrite = 0x40;
    internal const uint PageExecuteWriteCopy = 0x80;
    internal const uint PageGuard = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr VirtualQueryEx(
        IntPtr process,
        IntPtr address,
        out MemoryBasicInformation information,
        IntPtr length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(
        IntPtr process,
        IntPtr baseAddress,
        [Out] byte[] buffer,
        IntPtr size,
        out IntPtr bytesRead);
}

/// <summary>
/// 另一个进程的只读内存视图：打开、枚举可读区域、按地址读几字节。
/// A read-only view of another process's memory: open it, enumerate its readable regions, read bytes at an address.
/// </summary>
internal sealed class ProcessMemoryReader : IDisposable
{
    private static readonly IntPtr InformationSize =
        (IntPtr)Marshal.SizeOf<ProcessMemoryNative.MemoryBasicInformation>();

    private readonly IntPtr _handle;
    private bool _disposed;

    private ProcessMemoryReader(IntPtr handle) => _handle = handle;

    /// <summary>
    /// 尝试以"查询 + 读取"权限打开进程。权限不足（受保护进程、反作弊、杀软拦截）时返回 null 而不是抛异常：
    /// 读不到位置只该让歌词退回本地计时，不该让整个歌词栏出问题。
    /// Tries to open the process with query and read rights. Returns null instead of throwing when the rights are refused
    /// (protected process, anti-cheat, anti-virus): failing to read a position must only drop the lyrics back to the local
    /// clock, never break the bar itself.
    /// </summary>
    public static ProcessMemoryReader? TryOpen(int processId)
    {
        var handle = ProcessMemoryNative.OpenProcess(
            ProcessMemoryNative.ProcessQueryInformation | ProcessMemoryNative.ProcessVmRead,
            false,
            processId);

        if (handle == IntPtr.Zero)
        {
            Log.Warn("Media", $"无法读取播放器进程内存（OpenProcess 失败，Win32 {Marshal.GetLastWin32Error()}），歌词将退回本地计时。");
            return null;
        }

        return new ProcessMemoryReader(handle);
    }

    /// <summary>区域是否可读：已提交、不是守卫页、也不是无访问权限的页。/ Whether a region is committed and readable.</summary>
    public static bool IsReadable(in ProcessMemoryNative.MemoryBasicInformation region)
    {
        if (region.State != ProcessMemoryNative.MemCommit)
            return false;

        var protect = region.Protect;
        if ((protect & ProcessMemoryNative.PageGuard) != 0)
            return false;

        var basic = protect & 0xFF;
        return basic is ProcessMemoryNative.PageReadOnly
            or ProcessMemoryNative.PageReadWrite
            or ProcessMemoryNative.PageWriteCopy
            or ProcessMemoryNative.PageExecuteRead
            or ProcessMemoryNative.PageExecuteReadWrite
            or ProcessMemoryNative.PageExecuteWriteCopy;
    }

    /// <summary>
    /// 枚举可读区域。超过 <paramref name="maximumRegionBytes"/> 的区域跳过：位置不可能藏在一块几 GB 的预留堆里，
    /// 而挨个读完它们只会让启动时多等好几秒。
    /// Enumerates readable regions. Regions larger than <paramref name="maximumRegionBytes"/> are skipped: a position cannot
    /// hide inside a multi-gigabyte reserved heap, and walking those would only add seconds to start-up.
    /// </summary>
    public IEnumerable<ProcessMemoryNative.MemoryBasicInformation> EnumerateRegions(long maximumRegionBytes)
    {
        var address = IntPtr.Zero;

        while (true)
        {
            if (_disposed)
                yield break;

            if (ProcessMemoryNative.VirtualQueryEx(_handle, address, out var region, InformationSize) == IntPtr.Zero)
                yield break;

            var size = region.RegionSize.ToInt64();
            if (size <= 0)
                yield break;

            if (IsReadable(region) && size <= maximumRegionBytes)
                yield return region;

            var next = region.BaseAddress.ToInt64() + size;
            if (next <= address.ToInt64())
                yield break;

            address = (IntPtr)next;
        }
    }

    /// <summary>
    /// 枚举"值得扫描"的区域：已提交、没有守卫页、可读可写、且不超过给定大小。
    /// 这里刻意**不**按 Type（MEM_PRIVATE / MEM_MAPPED）过滤：实测确认播放位置会在两者之间搬家
    /// （网易云把播放状态放在 Chromium 的共享内存里，那块就是 MEM_MAPPED），只看私有堆会整段漏掉位置。
    /// 真正把无关数据挡在外面的是后面"必须按 1 倍速前进"的判据，而 NaN 由取值判断显式挡掉。
    /// Enumerates the regions worth scanning: committed, no guard page, readable and writable, and no larger than the given
    /// size. It deliberately does **not** filter on Type (MEM_PRIVATE vs MEM_MAPPED): the playback position was measured
    /// moving between the two — NetEase keeps its playback state in Chromium shared memory, which is MEM_MAPPED — so looking
    /// only at the private heap drops the position entirely. What actually keeps unrelated data out is the later "must advance
    /// at 1x" test, while NaN is rejected explicitly by the value check.
    /// </summary>
    public IEnumerable<ProcessMemoryNative.MemoryBasicInformation> EnumerateScannableRegions(long maximumRegionBytes)
    {
        foreach (var region in EnumerateRegions(maximumRegionBytes))
        {
            if ((region.Protect & 0xFF) != ProcessMemoryNative.PageReadWrite)
                continue;

            yield return region;
        }
    }

    /// <summary>读一段内存，返回实际读到的字节数；失败返回 0。/ Reads a block, returning the bytes actually read, or 0 on failure.</summary>
    public int Read(IntPtr address, byte[] buffer) => Read(address, buffer, buffer.Length);

    /// <summary>
    /// 只读前 <paramref name="count"/> 个字节。必须能指定长度而不是"整块缓冲区"：调用方拿缓冲区当复用的中转区，
    /// 一块缓冲区要装多段内存，若这里按缓冲区全长去读，短于缓冲区的区域就会因为"读回来的比要的多"被判失败——
    /// 而实测进程里绝大多数区域都小于缓冲区，于是几乎整片内存都被静默跳过。
    /// Reads only the first <paramref name="count"/> bytes. Being able to name the length rather than "the whole buffer" is
    /// required because callers reuse one buffer as a staging area for many blocks: reading the buffer's full length here makes
    /// any region shorter than the buffer fail the caller's "read exactly what was asked" check, and since most regions in a
    /// real process are shorter than the buffer, nearly the whole address space gets skipped in silence.
    /// </summary>
    public int Read(IntPtr address, byte[] buffer, int count)
        => _disposed
            ? 0
            : ProcessMemoryNative.ReadProcessMemory(_handle, address, buffer, (IntPtr)count, out var read)
                ? (int)read
                : 0;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ProcessMemoryNative.CloseHandle(_handle);
    }
}
