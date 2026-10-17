using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using LyricsBar.Classes.Utils;

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

        var window = FindPlayerWindow(candidates);
        if (window != IntPtr.Zero)
        {
            // 最小化的窗口先还原：只 SetForegroundWindow 一个最小化窗口，它会被拉到前台但仍然最小化，看着像没反应。
            // A minimised window is restored first: calling SetForegroundWindow alone brings it forward while still minimised,
            // which looks like nothing happened.
            _ = ShowWindowAsync(window, SW_RESTORE);

            if (SetForegroundWindow(window))
            {
                Log.Info(LogCategory, $"单击歌词栏：已把播放器窗口叫到前台（进程 {string.Join('/', candidates)}）。");
                return true;
            }

            // 前台锁（SetForegroundWindow 的权限规则）偶尔仍然会挡住：换一条老 API 再试一次，它没有那套限制。
            // The foreground lock can still refuse; one more attempt through the older API, which is not subject to it.
            SwitchToThisWindow(window, true);
            Log.Info(LogCategory, $"单击歌词栏：已通过 SwitchToThisWindow 切换到播放器（进程 {string.Join('/', candidates)}）。");
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
    /// 在正在运行的进程里找第一个匹配且有主窗口的那个。
    /// Picks the first matching running process that has a main window.
    /// </summary>
    private static IntPtr FindPlayerWindow(IReadOnlyList<string> candidates)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex)
        {
            Log.Warn(LogCategory, $"枚举进程失败：{ex.Message}");
            return IntPtr.Zero;
        }

        var fallback = IntPtr.Zero;

        foreach (var process in processes)
        {
            try
            {
                if (!IsCandidate(candidates, process))
                    continue;

                var handle = process.MainWindowHandle;
                if (handle == IntPtr.Zero)
                    continue;

                // 最小化时 MainWindowHandle 仍然有效，优先用它；没有窗口的进程（只跑托盘）留作最后手段也没有意义。
                // MainWindowHandle stays valid while minimised, so it is used directly; a process with no window at all (a
                // tray-only helper) leaves nothing to activate.
                if (IsWindowVisible(handle))
                    return handle;

                if (fallback == IntPtr.Zero)
                    fallback = handle;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 进程在枚举与查询之间退出、或权限不足读不到路径：跳过这一个，不影响其它候选。
                // The process exited between enumeration and inspection, or its path is unreadable: skip this one.
            }
            finally
            {
                process.Dispose();
            }
        }

        return fallback;
    }

    /// <summary>
    /// 进程名（或可执行文件路径的文件名）是否在候选里。同名可执行文件很常见（多个 Chrome 实例、多个 PotPlayer），
    /// 这里只认名字，不看路径：SMTC 给的标识本来就没有可靠路径。
    /// Whether the process name (or the executable's file name) is among the candidates. Same-named executables are common
    /// (several Chrome instances, several PotPlayer windows), and only the name is compared: the SMTC identifier carries no
    /// reliable path anyway.
    /// </summary>
    private static bool IsCandidate(IReadOnlyList<string> candidates, Process process)
    {
        foreach (var candidate in candidates)
        {
            if (string.Equals(process.ProcessName, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        string? path = null;
        try
        {
            path = process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }

        if (string.IsNullOrEmpty(path))
            return false;

        var fileName = Path.GetFileNameWithoutExtension(path);
        foreach (var candidate in candidates)
        {
            if (string.Equals(fileName, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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
}
