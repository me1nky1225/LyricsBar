using System.Runtime.InteropServices;
using System.Windows.Threading;
using LyricsBar.Classes.Utils;

namespace LyricsBar.Classes.Services;

/// <summary>
/// 闲置时把进程的物理工作集交还操作系统。任务管理器的"内存"一列立刻掉一大截（常见 100MB 以上），
/// 堆对象一个没动，页面按需换入，用户完全无感。
/// Returns the process's physical working set to the OS while idle. The memory column in Task Manager drops by a third at
/// once, not a single heap object is touched, pages fault back in on demand, and the user notices nothing.
///
/// 为什么值得做：WebView2 的浏览器与渲染进程起来后，工作集里大量是"加载过就不再碰"的页面与二进制镜像，
/// 停歌之后它们只是占着物理内存。AF Media Bar 把这一招当作低压力的关键手段之一，原理与语言无关，
/// 这里照方抓药：只在真正闲置（没有播放中的媒体会话，或播放已暂停）持续超过阈值后才动，
/// 且只裁一次——恢复播放后若再闲置会重新裁，不会每 30 秒反复裁。
/// Why it is worth doing: once WebView2's browser and renderer processes are up, most of the working set is pages and
/// binary images that were loaded once and never touched again; after playback stops they merely sit on physical memory.
/// AF Media Bar counts this among its key low-pressure tricks, the mechanism is language-agnostic, and the same medicine
/// is applied here: it only fires after genuine idleness (no playing media session, or playback paused) has persisted past
/// a threshold, and it trims once — resuming playback re-arms it, so it never fires every 30 seconds in a row.
/// </summary>
internal sealed class WorkingSetTrimmer : IDisposable
{
    /// <summary>持续多久不活跃才算闲置。暂停歌曲去接杯咖啡，回来不该看到歌词经历一次重新加载。/ How long inactivity must persist to count as idle; pausing for a coffee should not come back to reloaded lyrics.</summary>
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(1);

    /// <summary>巡检间隔。闲得越久越不需要频繁看，30 秒足够。/ Survey interval; the longer it has been idle the less often it needs looking at, and 30 seconds is plenty.</summary>
    private static readonly TimeSpan SurveyInterval = TimeSpan.FromSeconds(30);

    private readonly DispatcherTimer _timer;
    private readonly Func<bool> _isActive;
    private bool _disposed;

    /// <summary>最近一次处于活跃状态的时刻；null 表示从没见过活跃（一直闲着）。/ When activity was last seen; null means it has never been active (idle all along).</summary>
    private DateTimeOffset? _lastActiveAt = DateTimeOffset.UtcNow;

    /// <summary>当前工作集是否已被裁剪：恢复活跃时复位，避免连续重复裁。/ Whether the working set has been trimmed; reset when activity resumes so it is not trimmed over and over.</summary>
    private bool _trimmed;

    /// <param name="isActive">当前是否"有料"——有播放中的媒体会话。取不到会话或已暂停都算闲置。/ Whether there is something playing right now; no session or paused both count as idle.</param>
    public WorkingSetTrimmer(Func<bool> isActive)
    {
        ArgumentNullException.ThrowIfNull(isActive);

        _isActive = isActive;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = SurveyInterval
        };
        _timer.Tick += (_, _) => Survey();
        _timer.Start();
    }

    /// <summary>每次巡检：活跃就重置闲置计时；闲置持续超过阈值才裁，且只裁一次。</summary>
    private void Survey()
    {
        if (_disposed)
            return;

        var now = DateTimeOffset.UtcNow;
        if (_isActive())
        {
            _lastActiveAt = now;
            _trimmed = false;
            return;
        }

        _lastActiveAt ??= now;
        if (_trimmed || now - _lastActiveAt < IdleThreshold)
            return;

        if (EmptyWorkingSet(GetCurrentProcess()))
        {
            _trimmed = true;
            Log.Info("Memory", "程序已闲置超过 1 分钟，将闲置工作集交还操作系统。");
        }
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Stop();
    }
}
