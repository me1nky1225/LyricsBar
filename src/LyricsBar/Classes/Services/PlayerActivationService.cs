using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using LyricsBar.Classes.Utils;
using static LyricsBar.Classes.Interop.NativeMethods;

namespace LyricsBar.Classes.Services;

/// <summary>
/// 把"当前在放歌的那个播放器"叫到前台。
///
/// 为什么不能只靠 SMTC：系统媒体会话（SMTC <c>GlobalSystemMediaTransportControlsSession</c>）只提供控制能力
/// （上一首 / 播放暂停 / 下一首），没有"激活这个播放器"这一项。要实现"点歌词栏进播放器"，只能拿会话给出的
/// <c>SourceAppUserModelId</c> 自己去找窗口：先按标识里的可执行文件名找进程、把它的主窗口拉到前台；
/// 标识是打包应用（形如 <c>C:\...\App.exe!App</c>）且找不到进程时，再退回 Shell 的 ActivateApplication。
/// Why SMTC alone is not enough: the system media session only offers control capabilities (previous / play-pause / next),
/// with no "activate this player". Opening the player from a click therefore has to resolve a window from the session's
/// <c>SourceAppUserModelId</c> on its own: first find the process whose executable the identifier names and bring its main
/// window forward, then — for packaged identifiers of the form <c>C:\...\App.exe!App</c> with no matching process — fall back
/// to the shell's ActivateApplication.
/// </summary>
public static class PlayerActivationService
{
    private const int SW_RESTORE = 9;

    /// <summary><c>ShowWindow</c> 的"显示"命令：给藏起来（<c>SW_HIDE</c>）的窗口用，它不会顺手改窗口的大小和位置。</summary>
    private const int SW_SHOW = 5;

    /// <summary>
    /// 等窗口还原完成的最长毫秒数。够 120 Hz 的还原动画走完，又短到用户察觉不到。
    /// The longest wait, in milliseconds, for a restore to finish: long enough for a 120 Hz restore animation, short enough to
    /// go unnoticed.
    /// </summary>
    private const int RestoreBudgetMs = 120;

    /// <summary>
    /// 日志分类。与切歌按钮共用 "Transport"：两者都是"把用户的操作交给播放器"。
    /// The log category, shared with the transport buttons: both hand a user gesture over to the player.
    /// </summary>
    private const string LogCategory = "Transport";

    /// <summary>
    /// 来源标识里的可执行名 → 进程名的兜底映射。SMTC 的标识对 Win32 播放器常常只是 <c>cloudmusic.exe</c> 这样的
    /// 可执行名，但少数播放器会用自家包名或产品名（<c>NeteaseCloudMusic</c>、<c>PotPlayerMini64</c>），这里补上对应关系。
    /// Fallback map from a token inside the identifier to a process name. For Win32 players the SMTC identifier is often just an
    /// executable name such as <c>cloudmusic.exe</c>, but a few use their package or product name
    /// (<c>NeteaseCloudMusic</c>, <c>PotPlayerMini64</c>), so those get an entry.
    /// </summary>
    private static readonly (string Token, string ProcessName)[] TokenToProcess =
    [
        ("cloudmusic", "cloudmusic"),
        ("netease", "cloudmusic"),
        ("163music", "cloudmusic"),
        ("qqmusic", "QQMusic"),
        ("kugou", "KuGou"),
        ("kgmusic", "KuGou"),
        ("sodamusic", "SodaMusic"),
        ("spotify", "Spotify"),
        ("chrome", "chrome"),
        ("msedge", "msedge"),
        ("microsoftedge", "msedge"),
        ("firefox", "firefox"),
        ("vlc", "vlc"),
        ("potplayer", "PotPlayerMini64"),
        ("daum", "PotPlayerMini64"),
        ("zunemusic", "Music.UI"),
        ("media.player", "wmplayer"),
        ("wmplayer", "wmplayer"),
        ("mpv", "mpv"),
        ("foobar", "foobar2000")
    ];

    /// <summary>
    /// 试着把会话对应的播放器叫到前台。找不到就返回 false（并记一条日志），调用方不需要再做别的。
    /// Tries to bring the session's player to the foreground; returns false when nothing could be found (a log line records it).
    /// </summary>
    /// <param name="sourceId">SMTC 来源标识（AppUserModelId）。/ The SMTC source identifier (AppUserModelId).</param>
    public static bool TryActivate(string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            Log.Info(LogCategory, "单击歌词栏：这一条媒体会话没有可识别的来源标识，无法打开播放器。");
            return false;
        }

        sourceId = sourceId.Trim();

        var candidates = ResolveProcessNameCandidates(sourceId);
        if (candidates.Count == 0)
        {
            Log.Info(LogCategory, "单击歌词栏：这一条媒体会话没有可识别的来源标识，无法打开播放器。");
            return false;
        }

        // 计时只为了回答"为什么有时候点了要等一下"：枚举进程、挑窗口、还原这三段各自多久，日志里能看出来。
        // The timings exist to answer "why does a click sometimes take a while": process lookup, window pick and restore are
        // measured separately and reported in the log line.
        var collectWatch = Stopwatch.StartNew();
        var matches = CollectMatchingProcesses(candidates);
        collectWatch.Stop();

        var pickWatch = Stopwatch.StartNew();
        var window = matches.Count > 0 ? FindBestWindow(matches) : IntPtr.Zero;
        pickWatch.Stop();

        var timings = $"进程 {collectWatch.ElapsedMilliseconds} ms + 挑窗口 {pickWatch.ElapsedMilliseconds} ms"
            + (window != IntPtr.Zero ? $"（{DescribeWindow(window)}）" : string.Empty);

        if (window == IntPtr.Zero && matches.Count > 0 && TryRelaunch(matches))
        {
            Log.Info(LogCategory, $"单击歌词栏：播放器进程在，但已经没有窗口（关到托盘时销毁了面板），已重新启动一次把它叫回来（{string.Join('/', candidates)}；{timings}）。");
            return true;
        }

        if (window != IntPtr.Zero)
        {
            // 最小化或"关到托盘"藏起来的窗口都得先还原：只 SetForegroundWindow 一个最小化窗口，它会被拉到前台但仍然最小化，
            // 看着像没反应；而藏起来的窗口（SW_HIDE）连 SetForegroundWindow 都不会让它出现。
            // Both a minimised window and one hidden by "close to tray" have to be restored first: SetForegroundWindow alone
            // brings a minimised window forward while it stays minimised, which looks like nothing happened, and it does not
            // make an SW_HIDE-hidden window appear at all.
            var hidden = !IsWindowVisible(window);
            var iconic = IsIconic(window);

            var restoreWatch = Stopwatch.StartNew();
            if (iconic)
            {
                _ = ShowWindowAsync(window, SW_RESTORE);

                // 还原是异步的：不等它完成就去抢前台，窗口还在"最小化中"的状态，SetForegroundWindow 多半被前台锁挡掉，
                // 于是白走一趟 SwitchToThisWindow——用户看到的就是"点了要等一会儿才出来"。这里等它完成，最多 120 ms。
                // The restore is asynchronous: taking the foreground while the window is still mid-restore usually gets refused
                // by the foreground lock, so the SwitchToThisWindow fallback is spent for nothing — which is what "the click
                // takes a while" looks like. Wait for it, bounded at 120 ms.
                WaitUntilRestored(window, RestoreBudgetMs);
            }
            else if (hidden)
            {
                _ = ShowWindowAsync(window, SW_SHOW);
            }

            restoreWatch.Stop();
            timings += $" + 还原 {restoreWatch.ElapsedMilliseconds} ms";

            if (TryTakeForeground(window))
            {
                Log.Info(LogCategory, hidden
                    ? $"单击歌词栏：播放器原先藏在托盘里，已把窗口重新显示并叫到前台（进程 {string.Join('/', candidates)}；{timings}）。"
                    : $"单击歌词栏：已把播放器窗口叫到前台（进程 {string.Join('/', candidates)}；{timings}）。");
                return true;
            }

            // 前台锁偶尔仍然会挡住：换一条老 API 再试一次，它没有那套限制。
            // The foreground lock can still refuse; one more attempt through the older API, which is not subject to it.
            SwitchToThisWindow(window, true);
            Log.Info(LogCategory, $"单击歌词栏：已通过 SwitchToThisWindow 切换到播放器（进程 {string.Join('/', candidates)}；{timings}）。");
            return true;
        }

        // 走到这里说明进程列表里没有它：多半是打包应用（Store / MSIX），交给 Shell 按 AUMID 激活。
        // Nothing in the process list means it is most likely a packaged app (Store / MSIX), so the shell activates it by AUMID.
        if (TryActivatePackaged(sourceId))
        {
            Log.Info(LogCategory, $"单击歌词栏：已按 AUMID 激活打包应用（{sourceId}）。");
            return true;
        }

        Log.Info(LogCategory, $"单击歌词栏：没找到对应的播放器进程（{sourceId}），什么也没打开。");
        return false;
    }

    /// <summary>
    /// 由来源标识推出可能的进程名（不含 <c>.exe</c>），按可信度排序。纯函数，命令行自检直接对它断言。
    /// Derives the plausible process names (without <c>.exe</c>) from a source identifier, most trustworthy first. A pure
    /// function, so the command-line self-test asserts on it directly.
    /// </summary>
    /// <param name="sourceId">SMTC 来源标识。/ The SMTC source identifier.</param>
    public static IReadOnlyList<string> ResolveProcessNameCandidates(string? sourceId)
    {
        var text = sourceId?.Trim() ?? string.Empty;
        if (text.Length == 0)
            return [];

        var names = new List<string>();

        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return;

            var name = candidate.Trim();
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                name = name[..^4];

            // 只收像进程名的东西：带反斜杠的整条路径、带通配符的包名一律跳过（它们匹配不到任何进程名）。
            // Only process-name-like strings are kept: a full path or a wildcarded package name would match no process name.
            if (name.Length == 0 || name.Contains('\\') || name.Contains('/') || name.Contains('*'))
                return;

            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }

        var bang = text.LastIndexOf('!');
        var preBang = bang >= 0 ? text[..bang] : text;
        var preBangIsExe = preBang.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

        // `C:\...\cloudmusic.exe!App` 这种打包标识：`!` 之前是可执行文件全路径，取文件名就是进程名。
        // For a packaged identifier like `C:\...\cloudmusic.exe!App`, everything before `!` is the executable's full path, and
        // its file name is the process name.
        //
        // 只有带路径分隔符时才走 Path.GetFileName：包名 `Microsoft.ZuneMusic_8wekyb3d8bbwe` 里也有点，
        // GetFileNameWithoutExtension 会把 `.ZuneMusic_...` 当成扩展名吃掉，只剩 "Microsoft"。
        // Path.GetFileName is used only when a path separator is present: a package name such as
        // `Microsoft.ZuneMusic_8wekyb3d8bbwe` contains dots too, and GetFileNameWithoutExtension would treat `.ZuneMusic_...` as
        // the extension and leave "Microsoft".
        Add(preBang.Contains('\\') || preBang.Contains('/') ? Path.GetFileName(preBang) : preBang);

        // 整条标识也可能是产品名或包名（`NeteaseCloudMusic`、`Microsoft.ZuneMusic_8wekyb3d8bbwe!Music.UI`），
        // 按令牌表补上真正的可执行名。
        // The identifier may also be a product or package name, so the token table supplies the real executable name.
        foreach (var (token, processName) in TokenToProcess)
        {
            if (text.Contains(token, StringComparison.OrdinalIgnoreCase))
                Add(processName);
        }

        // `!` 之后那一段也试一次，但只在前面不是可执行文件路径时：`cloudmusic.exe!App` 里的 "App" 是应用标识，
        // 不是进程名，把它当进程名去找反而可能撞上一个叫 App.exe 的无关程序。
        // The part after `!` is tried too, but only when the part before it is not an executable path: the "App" in
        // `cloudmusic.exe!App` is an application identifier rather than a process name, and looking for it as one could match an
        // unrelated program called App.exe.
        if (bang >= 0 && !preBangIsExe && bang < text.Length - 1)
            Add(text[(bang + 1)..]);

        return names;
    }

    /// <summary>
    /// 一个候选项：进程 id。可执行文件路径要等到真要"重启播放器"时才去读——读 <c>MainModule</c> 是这套流程里最贵的操作。
    /// One candidate: the process id. The executable path is read only when a relaunch is actually needed, because reading
    /// <c>MainModule</c> is the most expensive step in this whole flow.
    /// </summary>
    private readonly record struct PlayerProcess(uint Id);

    /// <summary>
    /// 按候选名找进程。用 <c>GetProcessesByName</c> 而不是 <c>GetProcesses()</c> 再逐个比对：后者要为系统里每个进程建一个
    /// <see cref="Process"/>，还得逐个碰 <c>MainModule</c>（跨进程的模块查询），几百个进程就是几百次——那正是"点了要等一下"
    /// 的来源。这里只查名字，名字不对的进程根本不会被创建。
    /// Looks the process up with <c>GetProcessesByName</c> instead of <c>GetProcesses()</c> plus a walk: the latter builds a
    /// <see cref="Process"/> for every process on the machine and touches <c>MainModule</c> on each one (a cross-process module
    /// query) — hundreds of them, which is exactly where the delay came from. Here only the name is queried, and processes
    /// whose name does not match are never opened at all.
    /// </summary>
    private static List<PlayerProcess> CollectMatchingProcesses(IReadOnlyList<string> candidates)
    {
        var result = new List<PlayerProcess>();
        var seen = new HashSet<uint>();

        foreach (var candidate in candidates)
        {
            Process[] found;
            try
            {
                found = Process.GetProcessesByName(candidate);
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory, $"按进程名查找失败（{candidate}）：{ex.Message}");
                continue;
            }

            foreach (var process in found)
            {
                try
                {
                    var id = (uint)process.Id;
                    if (seen.Add(id))
                        result.Add(new PlayerProcess(id));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // 进程在枚举与查询之间退出：跳过这一个，不影响其它候选。
                    // The process exited between enumeration and inspection: skip this one.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 在这些进程的**所有顶层窗口**里挑一个来激活，可见的优先、其次按面积。
    ///
    /// 不能再用 <see cref="Process.MainWindowHandle"/>：「关闭到托盘」是把窗口 <c>SW_HIDE</c> 掉，
    /// 而 MainWindowHandle 只认可见的主窗口——藏起来之后它直接返回 0，于是"最小化能点开、最小化到托盘点不开"。
    /// 这里改用 <c>EnumWindows</c> 按进程 id 找，可见与否都收，再排除托盘辅助用的工具窗口。
    /// The window is picked among **all top-level windows** of those processes, visible ones first and larger ones next.
    ///
    /// <see cref="Process.MainWindowHandle"/> cannot be used any more: "close to tray" hides the window with <c>SW_HIDE</c>
    /// while MainWindowHandle only ever reports a *visible* main window, so it returns 0 as soon as the player is in the tray —
    /// which is exactly the "minimising works, minimising to the tray does not" symptom. <c>EnumWindows</c> is used instead,
    /// filtered by process id, accepting hidden windows and excluding the tool windows a tray helper creates.
    /// </summary>
    private static IntPtr FindBestWindow(IReadOnlyList<PlayerProcess> matches)
    {
        var processIds = new HashSet<uint>();
        foreach (var match in matches)
            processIds.Add(match.Id);

        var best = IntPtr.Zero;
        var bestScore = long.MinValue;

        EnumWindows((hwnd, _) =>
        {
            if (GetWindowThreadProcessId(hwnd, out var processId) == 0 || !processIds.Contains(processId))
                return true;

            // 托盘辅助窗口（通知气泡、隐藏的消息窗）都是工具窗口，激活它们毫无意义。
            // Tray helper windows (notification popups, hidden message windows) are tool windows; activating them is pointless.
            if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0)
                return true;

            if (!GetWindowRect(hwnd, out var rect))
                return true;

            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;

            // 0×0 或极小的顶层窗口是隐藏的中间层，不是用户认得的那块面板。
            // Zero-area and tiny top-level windows are hidden plumbing, not the panel the user knows.
            if (width < 120 || height < 60)
                return true;

            var visible = IsWindowVisible(hwnd);

            // 可见的压过一切（哪怕面积小一点），同类之间再比面积：主面板通常远大于歌词窗、设置窗这类附属窗口。
            // Visibility outweighs everything (even a smaller area); within the same class the area decides, because the main
            // panel is far larger than the little sub-windows a player also owns (lyrics, settings).
            var score = (visible ? 1L << 40 : 0) + (long)width * height;
            if (score <= bestScore)
                return true;

            bestScore = score;
            best = hwnd;
            return true;
        }, IntPtr.Zero);

        return best;
    }

    /// <summary>
    /// 进程还在、但一个像样的窗口都没有——有些播放器"关闭到托盘"时会把面板整个销毁，靠托盘图标或第二次启动重建。
    /// 这些播放器都是单实例的：再启动一次同一个 exe，它就自己把那块面板叫回来，而不是开出第二个程序。
    /// The process is alive but owns no decent window: some players destroy the panel when closing to the tray and rebuild it
    /// from the tray icon or on a second launch. They are all single-instance, so starting the same executable again makes the
    /// player bring the panel back rather than opening a second copy.
    /// </summary>
    private static bool TryRelaunch(IReadOnlyList<PlayerProcess> matches)
    {
        foreach (var match in matches)
        {
            var path = TryGetExecutablePath(match.Id);
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!IsSingleInstancePlayer(Path.GetFileNameWithoutExtension(path)))
                continue;

            if (!File.Exists(path))
                continue;

            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                Log.Info(LogCategory, $"播放器没有窗口，重新启动 {path}（单实例播放器会自己把面板叫回来）。");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory, $"重新启动播放器失败（{path}）：{ex.Message}");
            }
        }

        return false;
    }

    /// <summary>
    /// 只对单实例的播放器重新启动：多实例的（VLC、mpv、浏览器）再来一次只会多开一个窗口，反而更乱。
    /// Only single-instance players are relaunched: for multi-instance ones (VLC, mpv, browsers) a second launch just opens
    /// another window, which is worse than doing nothing.
    /// </summary>
    private static bool IsSingleInstancePlayer(string? processName)
    {
        if (string.IsNullOrEmpty(processName))
            return false;

        return processName.ToLowerInvariant() switch
        {
            "cloudmusic" or "qqmusic" or "kugou" or "sodamusic" or "spotify" or "music.ui" or "wmplayer" or "foobar2000" => true,
            _ => false
        };
    }

    /// <summary>
    /// 按进程 id 读可执行文件全路径；读不到（权限、进程已退出）返回 null。只在要重启播放器时调用。
    /// The executable's full path by process id, or null; only called when a relaunch is on the table.
    /// </summary>
    private static string? TryGetExecutablePath(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// 把窗口抢到前台。<c>SetForegroundWindow</c> 有一条"只有前台线程才能改前台窗口"的规则，而歌词栏本身是
    /// <c>WS_EX_NOACTIVATE</c> 的贴附窗口，点击它并不会让自己成为前台线程——所以常常被拒。
    ///
    /// 被拒时把**当前前台线程**的输入队列临时接上来（<c>AttachThreadInput</c>），这样调用方在这段时间里就具备前台线程的资格，
    /// 抢前台能成功，也就不用退到 <c>SwitchToThisWindow</c> 那条更慢的路（它会走一遍完整的切换到窗口流程）。
    /// Takes the foreground. <c>SetForegroundWindow</c> only allows the foreground thread to change the foreground window, while
    /// the lyrics bar is a <c>WS_EX_NOACTIVATE</c> window docked to the taskbar — clicking it does not make its thread the
    /// foreground one, so the call is usually refused.
    ///
    /// When refused, the current foreground thread's input queue is attached for the duration of the call
    /// (<c>AttachThreadInput</c>), which lends this thread the foreground thread's rights: the call then succeeds and the slower
    /// <c>SwitchToThisWindow</c> fallback — a full switch-to dance — is not needed.
    /// </summary>
    private static bool TryTakeForeground(IntPtr window)
    {
        if (SetForegroundWindow(window))
            return true;

        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
            return false;

        var foregroundThread = GetWindowThreadProcessId(foreground, out _);
        var self = GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == self)
            return false;

        var attached = false;
        try
        {
            attached = AttachThreadInput(foregroundThread, self, true);
            _ = BringWindowToTop(window);
            return SetForegroundWindow(window);
        }
        catch (Exception ex)
        {
            // 接不上去就算了：外面还有 SwitchToThisWindow 那一手。
            // Failing to attach is fine: SwitchToThisWindow is still there as the last resort.
            Log.Warn(LogCategory, $"抢前台失败：{ex.Message}");
            return false;
        }
        finally
        {
            if (attached)
                _ = AttachThreadInput(foregroundThread, self, false);
        }
    }

    /// <summary>
    /// 日志里形容一个窗口：类名 + 标题。挑错窗口时（比如挑中"桌面歌词"而不是主界面）只有这份描述能看出来。
    /// Describes a window for the log: class name plus title. If the wrong window ever gets picked (the desktop lyrics panel
    /// instead of the main one, say), this description is the only way to see it.
    /// </summary>
    private static string DescribeWindow(IntPtr window)
    {
        var className = new System.Text.StringBuilder(256);
        var title = new System.Text.StringBuilder(256);

        _ = GetClassName(window, className, className.Capacity);
        _ = GetWindowText(window, title, title.Capacity);

        var state = IsIconic(window) ? "最小化" : IsWindowVisible(window) ? "可见" : "隐藏";
        return $"{className}「{title}」，{state}";
    }

    /// <summary>
    /// 等窗口不再是"最小化"状态。纯轮询加睡 10 ms：没有事件可等，而这段最多只走 <see cref="RestoreBudgetMs"/>。
    /// Waits for the window to stop being minimised: there is no event to wait on, and the whole wait is capped at
    /// <see cref="RestoreBudgetMs"/>.
    /// </summary>
    private static void WaitUntilRestored(IntPtr window, int budgetMs)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < budgetMs)
        {
            if (!IsIconic(window))
                return;

            Thread.Sleep(10);
        }
    }

    /// <summary>
    /// 按 AUMID 激活打包应用。只对 Store / MSIX 应用有效；Win32 程序会返回失败，调用方据此记日志。
    /// Activates a packaged app by AUMID. Only Store / MSIX apps honour it; a Win32 program fails here, which the caller logs.
    /// </summary>
    private static bool TryActivatePackaged(string sourceId)
    {
        // 没有 '!' 的标识不是打包标识，ActivateApplication 一定失败，先挡掉省一次 COM 启动。
        // An identifier without '!' is not a package identifier, where ActivateApplication is certain to fail, so it is rejected
        // up front and one COM start-up is saved.
        if (!sourceId.Contains('!'))
            return false;

        try
        {
            // 用 CLSID 现取类型再实例化：ComImport 的包装类在这里没法直接 new 出来（编译期类型转换不成立），
            // 而 ActivateApplication 只需要一个实现了对应接口的 RCW。
            // The type is fetched from the CLSID and instantiated at runtime: the ComImport wrapper class cannot be
            // constructed directly here (the compile-time conversion does not hold), while ActivateApplication only needs an
            // RCW implementing the interface.
            var classId = Type.GetTypeFromCLSID(new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C"), throwOnError: false);
            if (classId is null)
            {
                Log.Warn(LogCategory, "这台机器上没有 ApplicationActivationManager，按 AUMID 激活不可用。");
                return false;
            }

            if (Activator.CreateInstance(classId) is not IApplicationActivationManager manager)
            {
                Log.Warn(LogCategory, "ApplicationActivationManager 不能作为 IApplicationActivationManager 使用。");
                return false;
            }

            var hr = manager.ActivateApplication(sourceId, null, 0 /* AO_NONE */, out _);
            if (hr >= 0)
                return true;

            Log.Warn(LogCategory, $"按 AUMID 激活失败（HRESULT 0x{hr:X8}）：{sourceId}");
            return false;
        }
        catch (Exception ex)
        {
            // 机器上根本没有这个 COM 类、或激活被策略禁止：都只影响"点一下打开播放器"，不往上抛。
            // The COM class may be missing or activation may be blocked by policy: both only affect "click to open", so the
            // failure is not propagated.
            Log.Warn(LogCategory, $"按 AUMID 激活出错：{ex.Message}");
            return false;
        }
    }

    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments,
            int options,
            out uint processId);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    /// <summary>
    /// 单独声明一个带 <c>out</c> 的版本：共享的 <c>NativeMethods</c> 里那个只读线程 id、不读进程 id，
    /// 而这里正是要按进程 id 认窗口。两个签名互不冲突（分属不同类）。
    /// A separate overload with <c>out</c>: the one in the shared <c>NativeMethods</c> reports the thread id and not the
    /// process id, which is exactly what this file needs to attribute a window to a process. The two signatures do not clash
    /// because they live in different classes.
    /// </summary>
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
