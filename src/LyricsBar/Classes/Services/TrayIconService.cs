using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LyricsBar.Classes.Settings;
using LyricsBar.Classes.Utils;

namespace LyricsBar.Classes.Services;

/// <summary>
/// 托盘图标与菜单。精简版没有主窗口可以承载菜单，托盘是用户唯一的操作入口：开关歌词、改设置、重新停靠、看日志、退出。
/// The tray icon and its menu. The trimmed build has no main window to host a menu, so the tray is the user's only entry point:
/// toggle lyrics, change settings, re-dock, view the log, and quit.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly Action _toggleLyrics;
    private readonly Action? _resetLyricsPosition;
    private readonly Action<double>? _nudgeLyricsPosition;

    /// <summary>是否拥有 <see cref="_icon"/> 的生命周期：只有退回方案那个自绘图标归托盘管，正式图标由 AppIcon 缓存共用。/ Whether this class owns <see cref="_icon"/>: only the self-drawn fallback is the tray's to dispose; the shipped icon is cached and shared through AppIcon.</summary>
    private readonly bool _ownsIcon;

    private bool _disposed;

    /// <summary>创建托盘图标。/ Creates the tray icon.</summary>
    /// <param name="toggleLyrics">开关歌词显示。/ Toggles lyric visibility.</param>
    /// <param name="openSettings">打开设置窗口。/ Opens the settings window.</param>
    /// <param name="redock">重新查找并停靠任务栏。/ Re-locates the taskbar and docks again.</param>
    /// <param name="openLog">打开日志文件。/ Opens the log file.</param>
    /// <param name="exit">退出程序。/ Exits the application.</param>
    /// <param name="resetLyricsPosition">把歌词时间复位到当前歌曲开头；null 表示不提供该入口。/ Resets the lyric time to the current track's start; null omits the entry.</param>
    /// <param name="nudgeLyricsPosition">把歌词时间相对快进或回退若干秒；null 表示不提供该入口。/ Advances or rewinds the lyric time; null omits the entry.</param>
    public TrayIconService(
        Action toggleLyrics,
        Action openSettings,
        Action redock,
        Action openLog,
        Action exit,
        Action? resetLyricsPosition = null,
        Action<double>? nudgeLyricsPosition = null)
    {
        _toggleLyrics = toggleLyrics ?? throw new ArgumentNullException(nameof(toggleLyrics));
        ArgumentNullException.ThrowIfNull(openSettings);
        ArgumentNullException.ThrowIfNull(redock);
        ArgumentNullException.ThrowIfNull(openLog);
        ArgumentNullException.ThrowIfNull(exit);
        _resetLyricsPosition = resetLyricsPosition;
        _nudgeLyricsPosition = nudgeLyricsPosition;

        // 优先用仓库里那个正式图标；只有它损坏或缺失时才退回运行时画的音符，保证托盘永远有图标可用。
        // 取到的正式图标由 AppIcon 缓存并可能与设置窗口共用，托盘不拥有它的生命周期。
        // The shipped icon is preferred; the runtime-drawn note is only a fallback for when that file is missing or damaged, so
        // the tray always ends up with something. The shipped icon is cached by AppIcon and may be shared with the settings
        // window, so the tray does not own its lifetime.
        var shippedIcon = AppIcon.LoadIcon();
        _ownsIcon = shippedIcon is null;
        _icon = shippedIcon ?? CreateTrayIcon();

        _toggleItem = new ToolStripMenuItem("显示歌词")
        {
            CheckOnClick = false,
            Checked = SettingsManager.Current.LyricsEnabled
        };
        _toggleItem.Click += (_, _) => _toggleLyrics();

        var menu = new ContextMenuStrip();
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("设置…", null, (_, _) => openSettings());
        menu.Items.Add("重新停靠到任务栏", null, (_, _) => redock());

        // 手动同步入口。播放器不上报时间轴时（网易云音乐），用户在音乐软件里拖动进度条，系统接口看不到这个动作，
        // 本地计时无从知晓，只能让用户自己把歌词时间推到当前进度。会上报时间轴的播放器用不到这几项，
        // 但菜单里留着无害——读取器自己会判断并忽略。
        // The manual sync entries. When the player reports no timeline (NetEase) and the user drags the seek bar in the music
        // app, the system interfaces cannot see the action and the local clock has no way to know, so the user has to nudge
        // the lyric time by hand. Players that report a timeline do not need them, but leaving the entries in is harmless:
        // the reader itself decides and ignores them.
        if (_resetLyricsPosition is not null || _nudgeLyricsPosition is not null)
        {
            menu.Items.Add(new ToolStripSeparator());
            if (_resetLyricsPosition is not null)
                menu.Items.Add("歌词同步：回到本首歌开头", null, (_, _) => _resetLyricsPosition());
            if (_nudgeLyricsPosition is not null)
            {
                menu.Items.Add("歌词快退 10 秒", null, (_, _) => _nudgeLyricsPosition(-10));
                menu.Items.Add("歌词快进 10 秒", null, (_, _) => _nudgeLyricsPosition(10));
            }
        }

        menu.Items.Add("打开日志", null, (_, _) => openLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exit());

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "LyricsBar — 任务栏歌词",
            Visible = true,
            ContextMenuStrip = menu
        };
        // 左键单击直接打开设置：比右键找菜单快，也是这类小工具的惯例。
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == MouseButtons.Left)
                openSettings();
        };

        SettingsManager.Changed += OnSettingsChanged;
    }

    /// <summary>弹出气泡提示。托盘不可用时静默忽略。/ Shows a balloon tip; silently ignored while the tray is unavailable.</summary>
    public void ShowBalloon(string title, string message)
    {
        if (_disposed)
            return;

        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = message;
            _notifyIcon.ShowBalloonTip(4000);
        }
        catch (Exception ex)
        {
            Log.Warn("Tray", $"弹出托盘提示失败: {ex.Message}");
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (_disposed)
            return;

        // 菜单可能正开着，Checked 要在 UI 线程上改。
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        dispatcher.BeginInvoke(() => _toggleItem.Checked = SettingsManager.Current.LyricsEnabled);
    }

    /// <summary>
    /// 运行时画一个音符图标。只在仓库里那个正式图标取不到时才会走到这里，因此它是个退路而不是常规路径。
    /// Draws a music-note icon at runtime. It is only reached when the shipped icon cannot be loaded, so it is a fallback
    /// rather than the normal path.
    /// </summary>
    private static Icon CreateTrayIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var pen = new Pen(Color.FromArgb(255, 245, 245, 245), 2.6f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            // 一个八分音符：符头 + 符干 + 符尾。
            g.DrawEllipse(pen, 7.5f, 19f, 9f, 7f);
            g.DrawLine(pen, 16f, 22f, 16f, 5.5f);
            g.DrawBezier(pen, 16f, 5.5f, 21.5f, 7.5f, 24f, 10.5f, 24f, 14f);
        }

        var handle = bitmap.GetHicon();
        try
        {
            // Clone 出一份独立的图标，才能安全地销毁原生句柄。
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        SettingsManager.Changed -= OnSettingsChanged;
        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Tray", $"释放托盘图标失败: {ex.Message}");
        }

        // 嵌入的应用图标由 AppIcon 类持有并可能被设置窗口共用，托盘这里只减引用、不销毁；
        // 只有退回方案那个自己画的图标才需要在这里释放。
        // The embedded app icon is owned by AppIcon and may be shared with the settings window, so the tray only drops its
        // reference instead of destroying it; only the self-drawn fallback needs releasing here.
        if (_ownsIcon)
            _icon.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
