using System.Diagnostics;
using System.Runtime.InteropServices;
using LyricsBar.Classes.Interop;
using LyricsBar.Classes.Utils;

namespace LyricsBar.Classes.Services;

/// <summary>
/// 网易云音乐的内存播放位置通道。实测（2026-10-15）：网易云的 SMTC 时间轴恒为零——Start/End/Position/MinSeek/MaxSeek
/// 全是 0，LastUpdatedTime 是 1601-01-01——但它的播放位置确实存在于 <c>cloudmusic.exe</c> 的私有内存里，是一串
/// 以秒为单位、约每秒更新一次、按 1 倍速前进的 double，而且**跟着用户拖动进度条一起跳**。本项目不采用上游那种
/// "按网易云版本记偏移量"的做法（版本一更新就失效），而是每次启动时现场扫描：
/// <list type="number">
///   <item>把私有内存里所有"看起来像秒数"的 double 收集起来；</item>
///   <item>隔两秒再扫一遍，只留"恰好前进了这两秒"的地址；</item>
///   <item>把数值彼此接近（同一份位置的多个副本）的地址聚成一簇，再隔两秒单独复核这十几个地址；</item>
///   <item>之后每 400 毫秒读一次这十几个地址，取中位数附近最靠前的那个值当位置。</item>
/// </list>
/// 扫描约 1-2 秒，之后每次轮询只是十几次 8 字节读取，代价可以忽略。读不到（权限被拒、网易云没运行、版本换成了
/// 别的位置表示）就返回 false，调用方退回本地计时——这个通道是"锦上添花"，不允许它把歌词栏搞坏。
/// The in-memory playback-position channel for NetEase Cloud Music. Measured on 2026-10-15: NetEase's SMTC timeline is
/// permanently zero (Start/End/Position/MinSeek/MaxSeek all 0, LastUpdatedTime 1601-01-01), yet its playback position really
/// does live in cloudmusic.exe's private memory as a seconds-valued double updated about once a second at 1x speed, and it
/// **follows the user dragging the seek bar**. This project rejects the upstream approach of recording per-NetEase-version
/// offsets (one update breaks it) and instead scans for it on every start: collect every double that looks like a second
/// count, rescan two seconds later keeping only the addresses that advanced by exactly those two seconds, cluster the ones
/// that agree with each other, re-verify that handful two seconds after that, and from then on read those dozen addresses
/// every 400 ms and take the leading value near their median. The scan costs a second or two; each later poll is a dozen
/// eight-byte reads. Anything unreadable (rights refused, NetEase not running, a version storing the position differently)
/// returns false and the caller falls back to the local clock: this channel is a bonus and is never allowed to break the bar.
/// </summary>
internal sealed class NeteasePositionService : IDisposable
{
    /// <summary>网易云的主进程名。它是 CEF 外壳，播放位置就在这个进程里。</summary>
    private const string PlayerProcessName = "cloudmusic";

    /// <summary>最多依次尝试几个网易云进程（取私有内存最大的几个）。</summary>
    private const int MaximumProcessCandidates = 3;

    /// <summary>单块区域超过这个大小就跳过：位置不会藏在一块几 GB 的预留堆里。</summary>
    private const long MaximumRegionBytes = 64L * 1024 * 1024;

    /// <summary>扫描时的读取块大小。</summary>
    private const int ScanBufferBytes = 4 * 1024 * 1024;

    /// <summary>两遍扫描之间的间隔：太短则"前进了多少"分辨不出来，太长则用户得干等。</summary>
    private const int ScanGapMilliseconds = 2000;

    /// <summary>锁定之后的轮询间隔。位置本身约每秒更新一次，400 毫秒足够顺滑。</summary>
    private const int PollIntervalMilliseconds = 400;

    /// <summary>连续这么多次轮询都读不到候选值，就认为地址失效（换版本、进程重启），重新扫描。</summary>
    private const int MaximumPollFailures = 4;

    /// <summary>两次扫描之间的最短间隔，失败时避免把自己扫成 CPU 风扇。</summary>
    private const int RescanCooldownMilliseconds = 20_000;

    /// <summary>
    /// 地址失效（多半是换歌）到允许重新扫描之间的等待。换歌是正常事件，不该吃失败退避那份 20 秒冷却——否则每换一首歌
    /// 都要先靠本地计时撑二十来秒才能对回真实进度。
    /// How long to wait before re-scanning once the locked addresses stop reading. A track change is a normal event and must
    /// not inherit the 20-second back-off meant for failed scans: that would leave every new song on the local clock for
    /// twenty-odd seconds before the real position is picked up again.
    /// </summary>
    private const int RescanAfterLossMilliseconds = 1_500;

    /// <summary>这么久没有网易云在放，就松开进程句柄；下次需要时重新扫描。</summary>
    private const int IdleShutdownMilliseconds = 10 * 60 * 1000;

    private const double MinimumPositionSeconds = 0.2;
    private const double MaximumPositionSeconds = 6 * 60 * 60;

    /// <summary>
    /// 第二遍筛选的容差。这一遍只是粗筛——两遍扫描的速度不可能完全一致，靠得住的判据是第三遍的单点复核，
    /// 所以这里宁可放宽一点，别把真身筛掉。
    /// Tolerance for the second pass. That pass only pre-filters: two scans can never run at exactly the same speed and the
    /// trustworthy test is the third pass's point-by-point re-check, so this one is deliberately loose rather than risk
    /// dropping the real position.
    /// </summary>
    private const double ScanAdvanceToleranceSeconds = 1.4;

    /// <summary>
    /// 第三遍复核的容差。复核时这一簇的地址是紧接着一个个读的，彼此间隔只差几微秒，所以这里可以卡得很紧。
    /// Tolerance for the third pass's re-check. Those addresses are read one after another within microseconds of each other,
    /// so this one can be tight.
    /// </summary>
    private const double ConfirmAdvanceToleranceSeconds = 0.45;

    /// <summary>同一份位置的多个副本之间允许相差多少秒。/ How far apart copies of the same position may sit.</summary>
    private const double ClusterToleranceSeconds = 2.5;

    /// <summary>一簇至少要这么多地址才认。/ Minimum addresses in a cluster before it is believed.</summary>
    private const int MinimumClusterSize = 3;

    /// <summary>复核几簇就够。最大的那簇通常就是位置，多备两簇只是防止偶然的成簇把它挤下去。</summary>
    private const int MaximumClustersToConfirm = 3;

    private readonly object _gate = new();

    private CancellationTokenSource? _cancellation;
    private Task? _loop;
    private long _lastRequestTicks;
    private double _position;
    private DateTimeOffset _sampledAt;
    private bool _hasPosition;

    /// <summary>
    /// 取当前播放位置（秒）与采样时刻。来源不是网易云、或还没扫描出结果时返回 false。
    /// 采样时刻要一并返回：上层的 <see cref="TaskbarExperiencePolicy"/> 会以它为基准按 1 倍速外推，
    /// 把内存里每秒才更新一次的位置补成连续的位置。
    /// Returns the playback position in seconds together with the instant it was sampled. Returns false when the source is
    /// not NetEase or nothing has been scanned yet. The instant matters: <see cref="TaskbarExperiencePolicy"/> extrapolates
    /// from it at 1x to turn a position that only updates once a second into a continuous one.
    /// </summary>
    public bool TryGetPosition(string? sourceId, out double seconds, out DateTimeOffset sampledAt)
    {
        if (!MediaSourceNameFormatter.IsNetEase(sourceId))
        {
            seconds = 0;
            sampledAt = default;
            return false;
        }

        Interlocked.Exchange(ref _lastRequestTicks, Environment.TickCount64);
        EnsureLoop();

        lock (_gate)
        {
            if (!_hasPosition)
            {
                seconds = 0;
                sampledAt = default;
                return false;
            }

            seconds = _position;
            sampledAt = _sampledAt;
            return true;
        }
    }

    /// <summary>
    /// 是否已经锁定位置地址。仅供自检读取：正常运行时这条通道是后台自己扫、自己跟，界面永远不需要等它。
    /// Whether position addresses are locked. Read by the self-check only: at runtime the channel scans and follows on its
    /// own and the UI never waits for it.
    /// </summary>
    internal bool IsLocked
    {
        get
        {
            lock (_gate)
                return _hasPosition;
        }
    }

    private void EnsureLoop()
    {
        lock (_gate)
        {
            if (_loop is not null)
                return;

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _loop = Task.Run(() => LoopAsync(token), token);
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        ProcessMemoryReader? reader = null;
        var addresses = new List<long>();
        var failures = 0;
        var nextScanAllowed = 0L;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (Environment.TickCount64 - Interlocked.Read(ref _lastRequestTicks) > IdleShutdownMilliseconds)
                {
                    // 很久没有网易云在放了：松开句柄，别一直持有别的进程。
                    reader?.Dispose();
                    reader = null;
                    addresses.Clear();
                    SetPosition(0, default, false);
                    await Task.Delay(2000, token).ConfigureAwait(false);
                    continue;
                }

                if (addresses.Count == 0)
                {
                    if (Environment.TickCount64 < nextScanAllowed)
                    {
                        await Task.Delay(500, token).ConfigureAwait(false);
                        continue;
                    }

                    reader?.Dispose();
                    reader = null;

                    var scanned = await Task.Run(() => ScanAsync(token), token).ConfigureAwait(false);
                    if (scanned is not { } result)
                    {
                        nextScanAllowed = Environment.TickCount64 + RescanCooldownMilliseconds;
                        await Task.Delay(500, token).ConfigureAwait(false);
                        continue;
                    }

                    reader = result.Reader;
                    addresses.AddRange(result.Addresses);
                    failures = 0;
                    Log.Info("Media", $"网易云内存通道已锁定 {addresses.Count} 个播放位置地址。");
                    continue;
                }

                var values = ReadValues(reader!, addresses);
                var position = SelectPosition(values);
                if (position is null)
                {
                    failures++;
                    if (failures >= MaximumPollFailures)
                    {
                        Log.Info("Media", "网易云内存位置已失效，将重新扫描。");
                        addresses.Clear();
                        nextScanAllowed = Environment.TickCount64 + RescanAfterLossMilliseconds;
                    }
                }
                else
                {
                    failures = 0;
                    SetPosition(position.Value, DateTimeOffset.UtcNow, true);
                }

                await Task.Delay(PollIntervalMilliseconds, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出路径。
        }
        catch (Exception ex)
        {
            Log.Warn("Media", $"网易云内存通道异常退出: {ex.GetType().Name} {ex.Message}");
        }
        finally
        {
            reader?.Dispose();
            lock (_gate)
            {
                _loop = null;
                _cancellation?.Dispose();
                _cancellation = null;
            }
        }
    }

    private void SetPosition(double seconds, DateTimeOffset sampledAt, bool hasPosition)
    {
        lock (_gate)
        {
            _position = seconds;
            _sampledAt = sampledAt;
            _hasPosition = hasPosition;
        }
    }

    /// <summary>
    /// 依次尝试几个网易云进程，返回第一个扫描成功的"读取器 + 地址清单"。调用方负责释放读取器。
    /// Tries the NetEase processes in turn and returns the first reader plus address list that scans successfully; the caller
    /// owns the reader.
    /// </summary>
    private (ProcessMemoryReader Reader, List<long> Addresses)? ScanAsync(CancellationToken token)
    {
        foreach (var process in FindCandidateProcesses())
        {
            token.ThrowIfCancellationRequested();

            using (process)
            {
                var reader = ProcessMemoryReader.TryOpen(process.Id);
                if (reader is null)
                    continue;

                try
                {
                    var startedTicks = Environment.TickCount64;
                    var first = CollectCandidates(reader, token);
                    if (first.Count == 0)
                    {
                        reader.Dispose();
                        continue;
                    }

                    if (token.WaitHandle.WaitOne(ScanGapMilliseconds))
                    {
                        reader.Dispose();
                        return null;
                    }

                    // 两遍扫描之间的真实间隔 = 第一遍开始到第二遍开始。绝不能写成"第一遍耗时"，更不能拿它减去等待时间：
                    // 那样算出来的间隔会偏小甚至为负，判据就退化成"值几乎没变"，churn 中的内存几乎全都能通过。
                    // The real gap between the two passes runs from the start of the first to the start of the second. It must not
                    // be the duration of the first pass, and least of all that minus the wait: the gap then comes out too small or
                    // even negative, the test degrades into "the value barely changed", and almost all churning memory passes.
                    var elapsed = (Environment.TickCount64 - startedTicks) / 1000d;
                    var advancing = SelectAdvancing(reader, first, elapsed, token);
                    var clusters = SelectClusters(advancing);

                    // 第三遍只复核最大的那几簇（每簇十几个地址，代价可以忽略）：能紧接着再按 1 倍速前进一次，才当它是播放位置。
                    // 先成簇再复核，是因为"位置"在内存里有好几份副本，而巧合前进的地址零散；复核则把"恰好改了差不多多少"的残余全部挡掉。
                    // The third pass re-checks only the few largest clusters (a dozen addresses each, a negligible cost): a value
                    // has to advance at 1x once more, measured back to back, to be believed. Clustering comes first because the
                    // position exists as several copies while coincidences are scattered, and the re-check then rejects every
                    // remaining "changed by roughly the right amount" coincidence.
                    foreach (var cluster in clusters)
                    {
                        var confirmTicks = Environment.TickCount64;
                        if (token.WaitHandle.WaitOne(ScanGapMilliseconds))
                        {
                            reader.Dispose();
                            return null;
                        }

                        var confirmElapsed = (Environment.TickCount64 - confirmTicks) / 1000d;
                        var confirmed = new List<long>(cluster.Count);
                        foreach (var (address, value) in cluster)
                        {
                            if (!TryReadDouble(reader, address, out var now))
                                continue;

                            var delta = now - value;
                            if (delta > 0 && Math.Abs(delta - confirmElapsed) <= ConfirmAdvanceToleranceSeconds)
                                confirmed.Add(address);
                        }

                        if (confirmed.Count >= MinimumClusterSize)
                        {
                            Log.Info(
                                "Media",
                                $"网易云内存位置已找到：候选 {first.Count} 个，粗筛通过 {advancing.Count} 个，成簇 {cluster.Count} 个，复核通过 {confirmed.Count} 个（间隔 {elapsed:0.0} 秒）。");
                            return (reader, confirmed);
                        }
                    }

                    Log.Info(
                        "Media",
                        $"网易云内存位置扫描未通过：候选 {first.Count} 个，粗筛通过 {advancing.Count} 个，最大簇 {clusters.FirstOrDefault()?.Count ?? 0} 个（间隔 {elapsed:0.0} 秒）。");
                }
                catch (OperationCanceledException)
                {
                    reader.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    Log.Warn("Media", $"扫描网易云内存失败: {ex.GetType().Name} {ex.Message}");
                }

                reader.Dispose();
            }
        }

        return null;
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

        // 播放位置在 CEF 外壳进程里，它是私有内存最大的那个；小的几个是辅助进程，扫了也是白扫。
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

    /// <summary>
    /// 这个 double 像不像一个播放位置。必须显式挡掉 NaN：NaN 与任何数比较都是 false，所以"小于下限"和"大于上限"
    /// 两个判断都拦不住它，而后面"前进了多少"的判断同样拦不住——NaN 会一路通过，让进程里每一段未初始化或已释放的
    /// 堆内存都变成候选（实测上百万个），真正的播放位置就淹没在里面了。
    /// Whether a double could be a playback position. NaN has to be rejected explicitly: every comparison with NaN is false,
    /// so neither the lower bound nor the upper bound catches it, and the later "how far did it advance" test does not catch it
    /// either — NaN sails through and turns every stretch of uninitialised or freed heap into a candidate (measured: over a
    /// million of them), drowning the real playback position.
    /// </summary>
    private static bool IsPlausiblePosition(double value)
        => double.IsFinite(value) && value >= MinimumPositionSeconds && value <= MaximumPositionSeconds;

    /// <summary>第一遍：把所有"看起来像秒数"的 double 连同地址一起收下来。/ First pass: every double that looks like a second count, with its address.</summary>
    private static Dictionary<long, double> CollectCandidates(ProcessMemoryReader reader, CancellationToken token)
    {
        var candidates = new Dictionary<long, double>();
        var buffer = new byte[ScanBufferBytes];

        foreach (var region in reader.EnumerateScannableRegions(MaximumRegionBytes))
        {
            token.ThrowIfCancellationRequested();

            var baseAddress = region.BaseAddress.ToInt64();
            var size = region.RegionSize.ToInt64();

            for (long offset = 0; offset + 8 <= size; offset += ScanBufferBytes)
            {
                var want = (int)Math.Min(ScanBufferBytes, size - offset);
                if (reader.Read((IntPtr)(baseAddress + offset), buffer, want) != want)
                    continue;

                var span = MemoryMarshal.Cast<byte, double>(buffer.AsSpan(0, want));
                for (var i = 0; i < span.Length; i++)
                {
                    var value = span[i];
                    if (!IsPlausiblePosition(value))
                        continue;

                    candidates[baseAddress + offset + (i * 8L)] = value;
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// 第二遍：只留下"与两遍之间的真实间隔相符地前进了"的地址。判据是"前进了约 1 倍速"，而不是"前进了任意值"，
    /// 所以随机数据中恰好落在秒数区间里的 double 会被筛掉。
    /// Second pass: keeps only the addresses that advanced by as much as the real gap between the two passes. The test is
    /// "advanced at about 1x", not "advanced by anything", which is what filters out the random doubles that merely happen to
    /// fall inside the seconds range.
    /// </summary>
    private static List<(long Address, double Value)> SelectAdvancing(
        ProcessMemoryReader reader,
        Dictionary<long, double> previous,
        double elapsedSeconds,
        CancellationToken token)
    {
        var advancing = new List<(long, double)>();
        var buffer = new byte[ScanBufferBytes];

        foreach (var region in reader.EnumerateScannableRegions(MaximumRegionBytes))
        {
            token.ThrowIfCancellationRequested();

            var baseAddress = region.BaseAddress.ToInt64();
            var size = region.RegionSize.ToInt64();

            for (long offset = 0; offset + 8 <= size; offset += ScanBufferBytes)
            {
                var want = (int)Math.Min(ScanBufferBytes, size - offset);
                if (reader.Read((IntPtr)(baseAddress + offset), buffer, want) != want)
                    continue;

                var span = MemoryMarshal.Cast<byte, double>(buffer.AsSpan(0, want));
                for (var i = 0; i < span.Length; i++)
                {
                    var value = span[i];
                    if (!IsPlausiblePosition(value))
                        continue;

                    var address = baseAddress + offset + (i * 8L);
                    if (!previous.TryGetValue(address, out var before))
                        continue;

                    var delta = value - before;
                    if (delta <= 0 || Math.Abs(delta - elapsedSeconds) > ScanAdvanceToleranceSeconds)
                        continue;

                    advancing.Add((address, value));
                }
            }
        }

        return advancing;
    }

    /// <summary>
    /// 把数值互相接近的候选聚成簇，按成员数从多到少返回前几簇。同一份播放位置在内存里通常有好几个副本
    /// （音频线程、界面线程各一份，彼此差不到一秒），而巧合前进的地址散落各处，所以成员最多的那簇最可能是位置；
    /// 但"最多"只是猜测，真正说了算的是后面的单点复核，于是这里多给几簇让复核去挑。
    /// Groups candidates whose values are close to each other and returns the biggest few groups, largest first. One position
    /// normally has several copies in memory (one per thread, within a second of each other) while coincidences are scattered,
    /// so the biggest group is the best guess; but "biggest" is only a guess and the point-by-point re-check has the final say,
    /// which is why a few groups are handed over rather than just one.
    /// </summary>
    private static List<List<(long Address, double Value)>> SelectClusters(List<(long Address, double Value)> advancing)
    {
        if (advancing.Count == 0)
            return [];

        var sorted = advancing.OrderBy(pair => pair.Value).ToList();
        var clusters = new List<List<(long, double)>>();
        var current = new List<(long, double)> { sorted[0] };

        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i].Value - sorted[i - 1].Value <= ClusterToleranceSeconds)
            {
                current.Add(sorted[i]);
                continue;
            }

            clusters.Add(current);
            current = [sorted[i]];
        }

        clusters.Add(current);

        return clusters
            .OrderByDescending(cluster => cluster.Count)
            .Take(MaximumClustersToConfirm)
            .ToList();
    }

    private static List<double> ReadValues(ProcessMemoryReader reader, List<long> addresses)
    {
        var values = new List<double>(addresses.Count);
        foreach (var address in addresses)
        {
            if (TryReadDouble(reader, address, out var value) && IsPlausiblePosition(value))
                values.Add(value);
        }

        return values;
    }

    /// <summary>
    /// 从这一轮的副本里定出一个位置：先取中位数，丢掉离群太远的（切歌、重建瞬间的残值），再在其中取**最大**的那个——
    /// 各个副本都滞后于真实播放位置，最靠前的那个才最接近此刻。
    /// Picks one position out of this round's copies: take the median, drop anything far from it (leftovers from a track change
    /// or a rebuild), then take the **largest** survivor, because every copy lags the true position and the leading one is
    /// closest to now.
    /// </summary>
    private static double? SelectPosition(List<double> values)
    {
        if (values.Count == 0)
            return null;

        var sorted = values.OrderBy(value => value).ToList();
        var median = sorted[sorted.Count / 2];
        double? best = null;

        foreach (var value in values)
        {
            if (Math.Abs(value - median) > ClusterToleranceSeconds)
                continue;

            if (best is null || value > best)
                best = value;
        }

        return best;
    }

    private static bool TryReadDouble(ProcessMemoryReader reader, long address, out double value)
    {
        value = 0;
        var buffer = new byte[sizeof(double)];
        if (reader.Read((IntPtr)address, buffer) != buffer.Length)
            return false;

        value = BitConverter.ToDouble(buffer, 0);
        return true;
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            cancellation = _cancellation;
            _cancellation = null;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 循环已经自己收尾了。
        }
    }
}
