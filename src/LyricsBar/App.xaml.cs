using System.Windows;
using LyricsBar.Classes.Services;
using LyricsBar.Classes.Settings;
using LyricsBar.Classes.Utils;
using LyricsBar.Views;

namespace LyricsBar;

/// <summary>
/// 组合根：整个精简版唯一负责"组装依赖"的地方。设置、媒体读取、歌词协调器、任务栏窗口、托盘图标都在这里接起来。
/// The composition root: the only place in the trimmed build that wires dependencies together — settings, media reading, the
/// lyric coordinator, the taskbar window, and the tray icon.
/// </summary>
// 同时引用 WPF 与 WinForms（托盘图标需要后者），Application 与 CheckBox 这类名字两边都有，
// 因此在本文件里显式限定 WPF 一侧。
// Both WPF and WinForms are referenced (the tray icon needs the latter), and names such as Application and CheckBox exist on
// both sides, so the WPF side is qualified explicitly here.
public partial class App : System.Windows.Application
{
    private const string InstanceMutexName = @"Local\LyricsBar.SingleInstance";

    private Mutex? _instanceMutex;
    private LyricsCoordinator? _coordinator;
    private MediaTransportService? _transport;
    private TaskbarLyricsWindow? _bar;
    private TrayIconService? _tray;
    private SettingsWindow? _settingsWindow;
    private WorkingSetTrimmer? _trimmer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        SettingsManager.Load();
        Log.Info("App", $"LyricsBar 启动，设置文件 {SettingsManager.FilePath}");

        // --diagnose 是命令行自检入口：只查环境与歌词链路，结果写日志后立即退出，不创建任何窗口。
        // 它必须排在单实例检查之前——用户正是在歌词栏出问题、程序还开着的时候才会去跑自检，
        // 若先做单实例判断，自检会被静默吞掉，什么也查不到。
        // The --diagnose entry point runs the environment and lyric-pipeline self-check, writes the result to the log, and exits
        // without creating any window. It must come before the single-instance check: users run it precisely when the bar is
        // misbehaving and the app is still open, and a single-instance check first would swallow the self-check silently.
        if (Diagnostics.TryRun(e.Args))
            return;

        // --render-preview 是渲染层的离屏自检：把原生渲染器画成一组 PNG 后退出，不创建任何窗口。
        // 它和 --diagnose 一样必须在单实例检查之前——排查渲染问题时程序通常还开着。
        // --render-preview is the rendering layer's offscreen self-check: it draws the native renderer into a set of PNGs and
        // exits without creating a window. Like --diagnose it has to come before the single-instance check, because the app is
        // usually still running while a rendering problem is being investigated.
        if (RenderPreview.TryRun(e.Args))
            return;

        // 单实例：任务栏歌词栏只能存在一份，第二个实例会把窗口叠在第一份上，看起来像"歌词闪了一下"。
        _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("App", "未处理的界面线程异常。", args.Exception);
            // 不把异常继续抛给系统：歌词栏崩掉不该让用户看到一个错误对话框就结束进程。
            args.Handled = true;
        };

        var dock = new TaskbarDockService();
        var taskbars = dock.EnumerateTaskbars();
        var target = taskbars.FirstOrDefault(t => t.IsPrimary) ?? taskbars.FirstOrDefault();
        if (target is null)
        {
            Log.Error("App", "未找到任何可停靠的任务栏窗口。");
            MessageBox.Show(
                "没有找到 Windows 任务栏窗口，歌词栏无法停靠。\n请确认 Explorer 正在运行后重试。",
                "LyricsBar",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        _coordinator = new LyricsCoordinator();
        _transport = new MediaTransportService();
        var noDock = e.Args.Any(a => string.Equals(a, "--no-dock", StringComparison.OrdinalIgnoreCase));
        _bar = new TaskbarLyricsWindow(_coordinator, target, dockToTaskbar: !noDock, transport: _transport);
        _bar.Show();

        _tray = new TrayIconService(
            toggleLyrics: ToggleLyrics,
            openSettings: OpenSettings,
            redock: Redock,
            openLog: OpenLog,
            exit: ExitApplication,
            resetLyricsPosition: ResetLyricsPosition,
            nudgeLyricsPosition: NudgeLyricsPosition);

        _ = StartMediaAsync();

        // 闲置工作集裁剪：没有播放中的媒体会话（或已暂停）持续一分钟后，把物理内存还给操作系统。
        // 播放器不上报时间轴的场景下快照里 IsPlaying 仍然可靠，因此判据就是"在不在播"。
        // The idle working-set trim: after a minute without a playing media session (or paused), physical memory goes back to
        // the OS. IsPlaying is reliable in the snapshot even when the player reports no timeline, so "is it playing" is the
        // whole criterion.
        _trimmer = new WorkingSetTrimmer(() => _coordinator?.Latest.IsPlaying == true);
    }

    private async Task StartMediaAsync()
    {
        if (_coordinator is null)
            return;

        // 切歌服务单独初始化：它拿不到会话时只是不能切歌，歌词显示完全不受影响，所以失败不阻断。
        // The skip service initializes separately: without a session it simply cannot skip, lyric display is unaffected, so a
        // failure here does not block anything.
        if (_transport is not null)
            await _transport.InitializeAsync();

        var ok = await _coordinator.StartAsync();
        if (!ok)
        {
            _tray?.ShowBalloon("LyricsBar", "暂时没有找到媒体会话，开始播放音乐后歌词会自动出现。");
        }
    }

    private void ToggleLyrics()
    {
        var settings = SettingsManager.Current.Clone();
        settings.LyricsEnabled = !settings.LyricsEnabled;
        SettingsManager.Apply(settings, nameof(AppSettings.LyricsEnabled));
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(redockRequested: Redock);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>
    /// 托盘 / 设置窗口的「重新停靠到任务栏」。除了重新找任务栏，它还是"换歌词渲染引擎"的落地点：
    /// 换引擎必须把旧渲染器拆掉重建，那件事由窗口自己做。
    /// Tray / settings "re-dock onto the taskbar". Besides finding the taskbar again, this is where a renderer-engine change
    /// lands: switching engines requires tearing the old renderer down and rebuilding, which the window owns.
    /// </summary>
    private void Redock() => _bar?.RedockAndReloadRenderer();

    /// <summary>
    /// 托盘「歌词同步：回到本首歌开头」。非空转发给协调器；协调器还没起来时静默忽略——启动初期点菜单没有意义可做。
    /// Tray → "lyric sync: back to this track's start". Forwarded to the coordinator when it exists and silently ignored
    /// otherwise: clicking the menu before startup has nothing to act on.
    /// </summary>
    private void ResetLyricsPosition() => _coordinator?.ResetLyricsPosition();

    /// <summary>托盘「歌词快退/快进 10 秒」。/ Tray → "lyric rewind/advance by 10 seconds".</summary>
    private void NudgeLyricsPosition(double seconds) => _coordinator?.NudgeLyricsPosition(seconds);

    private void OpenLog()
    {
        try
        {
            var path = System.IO.Path.Combine(SettingsManager.Directory, "lyricsbar.log");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn("App", $"打开日志失败: {ex.Message}");
        }
    }

    private void ExitApplication()
    {
        Log.Info("App", "用户从托盘菜单退出。");
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 退出顺序：先停界面（它会解除停靠并释放 WebView2），再停媒体与取词，最后放托盘。
        // 顺序颠倒会留下"父窗口已销毁的子窗口"或仍在跑的 WebView2 进程。
        try { _settingsWindow?.Close(); } catch (Exception ex) { Log.Warn("App", ex.Message); }
        try { _bar?.Close(); } catch (Exception ex) { Log.Warn("App", ex.Message); }
        _coordinator?.Dispose();
        _transport?.Dispose();
        _tray?.Dispose();
        _trimmer?.Dispose();

        _instanceMutex?.Dispose();
        _instanceMutex = null;

        base.OnExit(e);
    }
}
