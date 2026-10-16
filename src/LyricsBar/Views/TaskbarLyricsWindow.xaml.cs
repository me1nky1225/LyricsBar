using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LyricsBar.Classes.Interop;
using LyricsBar.Classes.Models;
using LyricsBar.Classes.Services;
using LyricsBar.Classes.Services.Lyrics;
using LyricsBar.Classes.Services.Lyrics.Presentation;
using LyricsBar.Classes.Settings;
using LyricsBar.Classes.Utils;
using Microsoft.Web.WebView2.Wpf;
using static LyricsBar.Classes.Interop.NativeMethods;

namespace LyricsBar.Views;

/// <summary>
/// 任务栏歌词窗口。它是整个精简版唯一的可见界面：一个被停靠进 Explorer 任务栏的透明窗口，
/// 里面是 WebView2 歌词宿主加一条底部进度。
/// The taskbar lyric window. It is the trimmed build's only visible surface: a transparent window docked into the Explorer
/// taskbar, holding the WebView2 lyric host and a progress strip.
///
/// 与原项目的差异：原项目用 Canvas 承载一个可自定义的多组件媒体控件（封面、标题、控制按钮、频谱、性能指标……），
/// 窗口尺寸与布局由 LayoutRenderEngine 计算；精简版没有布局引擎，窗口宽度直接由当前歌词文本量出来。
/// Difference from the original: the original used a Canvas to host a configurable multi-component media control (artwork,
/// title, transport buttons, spectrum, performance metrics) with sizes computed by LayoutRenderEngine. The trimmed build has
/// no layout engine; the window width is measured straight from the current lyric text.
/// </summary>
public partial class TaskbarLyricsWindow : Window
{
    /// <summary>呈现帧间隔。50 毫秒足以让逐字擦亮看起来连续，也不会把 UI 线程压满。</summary>
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>任务栏存活检查间隔。Explorer 崩溃重启后父窗口会失效，必须重新停靠。</summary>
    private static readonly TimeSpan TaskbarWatchInterval = TimeSpan.FromSeconds(2);

    /// <summary>Web 歌词视图为文字预留的水平内边距（DIP），与 style.css 的 .layout / .lyrics-pane 相加一致。</summary>
    private const double WebLyricsHorizontalInsetDip = 16;

    /// <summary>封面与歌词之间的间距（DIP），与 style.css 的 <c>--cover-gap</c> 默认值一致。</summary>
    private const double CoverGapDip = 8;

    /// <summary>
    /// 曲目信息块与歌词之间的间距（DIP），与 app.js 的 <c>trackInfoGapPx</c> 一致。
    /// 这个数被算进栏宽，所以两边一旦不同，歌词栏就会差一个间距那么宽。
    /// Gap between the track-info block and the lyrics in DIP, matching <c>trackInfoGapPx</c> in app.js. It is budgeted into the
    /// bar width, so any disagreement leaves the bar one gap too wide or too narrow.
    /// </summary>
    private const double TrackInfoGapDip = 8;

    // 曲目信息块的宽度规则（自动宽度的上限、手动宽度的区间）都在 LyricsTrackInfoWidthPolicy 里，
    // 这样探针能直接验它，宿主这边只负责量文字。

    /// <summary>
    /// Web 歌词布局的水平内边距（DIP），与 style.css 的 <c>--layout-padding-inline</c> 一致。
    /// 按钮要居中在歌词框里，就得知道歌词框的左右边界，而这个值同时给出左右两侧的内缩。
    /// Horizontal padding of the web lyric layout in DIP, matching <c>--layout-padding-inline</c> in style.css. Centring the
    /// buttons inside the lyrics box needs that box's left and right edges, and this single constant supplies both insets.
    /// </summary>
    private const double LayoutPaddingInlineDip = 4;

    /// <summary>封面相对歌词栏高度的内缩量（DIP）。让封面比栏本身矮一点，看起来才不像被裁掉。</summary>
    private const double CoverVerticalInsetDip = 12;

    /// <summary>最小栏宽，避免只有一两个字时栏变得极窄。</summary>
    private const double MinimumBarWidthDip = 120;

    private readonly TaskbarDockService _dock = new();
    private readonly LyricsCoordinator _coordinator;
    private readonly DispatcherTimer _frameTimer;
    private readonly DispatcherTimer _taskbarWatchTimer;
    private readonly bool _dockToTaskbar;

    private TaskbarTarget _target;
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _docked;
    private bool _hostLoaded;
    private bool _closing;

    /// <summary>指针是否正停在歌词栏窗口内（消息钩子维护）。按钮区的悬停显隐看它，不看 WPF 的 IsMouseOver。</summary>
    private bool _pointerInsideWindow;

    /// <summary>
    /// 指针在客户区里的横向位置（DIP），不在窗口内时为 null。判定指针落在"封面 + 曲目信息"还是"歌词"上要用它。
    /// The pointer's horizontal position inside the client area in DIP, null while it is outside the window. It is what decides
    /// whether the pointer rests on the artwork-and-track-info part or on the lyrics.
    /// </summary>
    private double? _pointerInsideDipX;

    /// <summary>指针当前所在的块，由 <see cref="ResolveOverlayRegion"/> 算出来。/ The part the pointer is over, from <see cref="ResolveOverlayRegion"/>.</summary>
    private LyricsOverlayRegion _overlayRegion = LyricsOverlayRegion.None;

    /// <summary>渲染器降级的最高级别，见 <see cref="ResolveBrowserArguments"/>。</summary>
    private const int MaxRendererFallbackTier = 2;

    /// <summary>第 1 级降级：只关掉 GPU 进程。安全，能治"GPU 起不来"这一类失败。</summary>
    private const string DisableGpuArguments = "--disable-gpu";

    /// <summary>
    /// 第 2 级降级：关 GPU 之外再关掉 Chromium 沙箱。虚拟机、远程会话与部分受限企业终端上沙箱无法初始化，
    /// 实测（2026-10-14）这类环境里只关 GPU 完全无效，必须连沙箱一起关。
    /// 安全性可以接受：歌词页是本程序内嵌的自包含文档，不导航到任何外部地址，
    /// 且已关闭 DevTools、右键菜单与状态栏，没有不可信网页内容可被沙箱逃逸利用。
    /// Tier 2 fallback: disables the Chromium sandbox in addition to the GPU. Sandbox initialization fails on virtual
    /// machines, remote sessions, and some locked-down corporate endpoints, and measured 2026-10-14 disabling the GPU alone
    /// does nothing there. The security trade-off is acceptable: the lyric page is a self-contained document embedded in this
    /// program with no external navigation, and DevTools, context menus, and the status bar are already disabled, so there is
    /// no untrusted web content a sandbox escape could reach.
    /// </summary>
    private const string DisableGpuAndNoSandboxArguments = "--disable-gpu --no-sandbox";

    /// <summary>
    /// 歌词文档从开始初始化到加载完成允许花的最长时间，超过就当作一次失败去降级。
    /// 崩溃会通过 <c>ProcessFailed</c> 通知到程序，但"进程还活着、文档永远加载不出来"不会通知任何人——
    /// 实测（2026-10-14）上一轮的 WebView2 进程残留占用用户数据目录时就是这个表现：日志停住、歌词栏空白、
    /// 程序一直不恢复，只能手动结束进程。超时看门狗把这种情况也拉回同一条降级路径。
    /// Longest the lyric document may take from initialization to load completion before it counts as a failure and triggers
    /// the fallback. A crash arrives through <c>ProcessFailed</c>, but "process alive, document never loads" notifies nobody;
    /// measured 2026-10-14, that is exactly what happens when leftover WebView2 processes from a previous instance still hold
    /// the user data folder: the log stops, the bar stays blank, the app never recovers, and only a manual kill helps. The
    /// timeout watchdog routes that case through the same fallback path.
    /// </summary>
    private static readonly TimeSpan RendererReadyTimeout = TimeSpan.FromSeconds(15);

    /// <summary>上一次推给渲染器的悬停磨砂区域；null 表示渲染器刚重建、页面是空白的，需要重推一次。</summary>
    private LyricsOverlayRegion? _appliedOverlayRegion;

    /// <summary>上一次摆好的悬停提示宽度（DIP）；-1 表示还没摆过。/ Last laid-out hint width in DIP; -1 means it has not been placed yet.</summary>
    private double _appliedInfoHintWidthDip = -1;

    /// <summary>上一次设过的悬停提示字号。/ Last hint font size applied.</summary>
    private double _appliedInfoHintFontSize;

    /// <summary>
    /// 两次切歌之间的最短间隔。滚轮转一格就是一条消息，转一下手腕能产生十几条，
    /// 不设间隔的话"轻轻滚一下"会直接跳掉好几首；按钮连点同理。
    /// Minimum interval between two skips. One wheel notch is one message and a flick of the wrist produces a dozen, so without
    /// a floor a light scroll would jump several tracks; the same goes for double-clicking the buttons.
    /// </summary>
    private static readonly TimeSpan SkipCooldown = TimeSpan.FromMilliseconds(400);

    private WebView2CompositionControl? _webView;
    private ILyricsRenderer? _renderer;
    private int _rendererGeneration;
    private bool _rendererFailed;

    /// <summary>
    /// 当前降级级别：0 默认，1 关 GPU，2 关 GPU + 关沙箱。逐级递增，见 <see cref="OnRendererFailed"/>。
    /// 初始值取上一次跑通的级别（<see cref="AppSettings.WebView2RendererTier"/>），这样在这台必须关沙箱的机器上
    /// 不必每次启动都先崩两轮。
    /// Current fallback tier: 0 default, 1 no GPU, 2 no GPU + no sandbox. Escalates in <see cref="OnRendererFailed"/>. It
    /// starts from the tier that worked last time (<see cref="AppSettings.WebView2RendererTier"/>) so a machine that needs
    /// the sandbox disabled does not have to crash twice on every single start.
    /// </summary>
    private int _rendererFallbackTier;

    /// <summary>当前级别是否已经因"文档加载超时"重试过一次，见 <see cref="WatchRendererReadyAsync"/>。换级别时清零。</summary>
    private bool _rendererRetriedCurrentTier;

    private LyricsPresentationFrame _frame = LyricsPresentationFrame.Hidden;
    private LyricsWebStyle? _appliedStyle;
    private double _appliedWidthDip = -1;
    private bool _appliedVisible;

    /// <summary>已经记过日志的曲目标识，用来判断"换歌了，第一行也要记"。</summary>
    private string _lastLoggedTrackKey = string.Empty;

    /// <summary>已经记过日志的歌词行下标。诊断"歌词不跟手"时，这条日志给出每次换行的时刻与实际播放位置。</summary>
    private int _lastLoggedLineIndex = int.MinValue;

    /// <summary>判定"擦亮倒退"的进度差阈值；小抖动（渲染端的平滑）不算。</summary>
    private const double ScanRegressionTolerance = 0.05;

    /// <summary>上一帧擦亮进度所属的行下标，以及那一帧的进度；换行就重新计时。</summary>
    private int _lastScanLineIndex = int.MinValue;
    private double? _lastScanProgress;

    /// <summary>本行是否已经记过"擦亮倒退"，同一行只记一条，避免刷屏。</summary>
    private bool _scanRegressionLogged;

    /// <summary>已经推给 Web 端的封面 data URI，用来避免每帧重复推送同一张封面。</summary>
    private string _appliedCoverUri = string.Empty;

    /// <summary>选行迟滞器：位置抖动时行号与擦亮位置都不回退（见 <see cref="LyricLineCursor"/>）。</summary>
    private readonly LyricLineCursor _lineCursor = new();

    /// <summary>已推送封面的曲目标识；空封面也要随切歌更新首字母占位。</summary>
    private string _appliedCoverTrackId = string.Empty;

    /// <summary>已推送给 Web 端的曲目信息文本与宽度；三者都没变就不再重复下发。</summary>
    private string _appliedTrackInfoTitle = string.Empty;
    private string _appliedTrackInfoArtist = string.Empty;

    /// <summary>已下发的曲目信息块宽度（DIP）；-1 表示还没下发过。</summary>
    private double _appliedTrackInfoWidthDip = -1;

    /// <summary>已写入按钮组的歌词框左边界（DIP）；-1 表示还没写过。它同时是按钮组的外边距。</summary>
    private double _appliedTransportLeftDip = -1;

    /// <summary>已写入窗口的左边界（物理像素），用来避免每帧重复记位置日志。null 表示还没写过。</summary>
    private int? _appliedLeftPx;

    /// <summary>用户可见的显隐状态。窗口的实际不透明度还受"渲染器是否就绪"约束，见 <see cref="SetBarVisible"/>。</summary>
    private bool _barVisible;

    /// <summary>已经写入窗口的不透明度状态，用来避免每帧重复设置。</summary>
    private bool? _appliedOpacityVisible;

    /// <summary>已经写入窗口的"鼠标可交互"状态，用来避免每帧重复改扩展样式。</summary>
    private bool? _appliedInteractive;

    /// <summary>切歌服务；为 null 表示当前进程没有它（诊断模式下窗口可能单独构造），此时按钮不显示。</summary>
    private readonly MediaTransportService? _transport;

    /// <summary>上一次切歌的时刻，用于 <see cref="SkipCooldown"/> 去抖。</summary>
    private DateTimeOffset _lastSkipAt = DateTimeOffset.MinValue;

    /// <summary>创建任务栏歌词窗口。/ Creates the taskbar lyric window.</summary>
    /// <param name="coordinator">提供当前曲目与歌词的协调器。/ The coordinator supplying the current track and lyrics.</param>
    /// <param name="target">要停靠的任务栏。/ The taskbar to dock into.</param>
    /// <param name="dockToTaskbar">
    /// 是否真正停靠进任务栏。<c>false</c> 时窗口只作为一个普通顶层窗口运行，用于把"WebView2 自身的问题"与
    /// "跨进程停靠引起的问题"分开——任务栏停靠用 <c>SetParent</c> 把窗口变成别的进程的子窗口，
    /// 这是整条链路上最容易被怀疑的一环，能单独关掉它排障非常重要。
    /// Whether to actually dock into the taskbar. When <c>false</c> the window runs as a plain top-level window, which
    /// separates "a WebView2 problem" from "a cross-process docking problem": docking turns the window into a child of
    /// another process via <c>SetParent</c>, the most suspect link in the chain, so being able to switch it off alone matters.
    /// </param>
    /// <param name="transport">
    /// 切歌服务；为 null 时歌词栏只显示不控制（切歌按钮不出现，滚轮也不响应）。
    /// The skip service; when null the bar is display-only (no skip buttons, no wheel handling).
    /// </param>
    public TaskbarLyricsWindow(
        LyricsCoordinator coordinator,
        TaskbarTarget target,
        bool dockToTaskbar = true,
        MediaTransportService? transport = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _dockToTaskbar = dockToTaskbar;
        _transport = transport;

        // 沿用上次跑通的降级级别，避免重复经历"崩溃—重建"。
        // 兼容模式是用户显式要求的最重降级，优先级高于记忆值。
        // Reuse the tier that worked last time to avoid repeating the crash-and-rebuild cycle. Compatibility mode is the
        // heaviest fallback the user explicitly asked for, so it wins over the remembered value.
        var savedTier = SettingsManager.Current.WebView2CompatibilityMode
            ? MaxRendererFallbackTier
            : SettingsManager.Current.WebView2RendererTier;
        _rendererFallbackTier = Math.Clamp(savedTier, 0, MaxRendererFallbackTier);
        if (_rendererFallbackTier > 0)
            Log.Info("Lyrics", $"沿用上次跑通的 WebView2 降级级别 {_rendererFallbackTier}（{ResolveBrowserArguments()}）。");

        InitializeComponent();

        // 窗口必须先 Show 才能拿到 HWND 并停靠，但"显示出来"和"停靠完成"之间隔着一个消息循环，
        // 直接可见会有一帧闪在屏幕中央。先用 0 不透明度挡掉这一帧，停靠成功后再恢复。
        Opacity = 0;

        _frameTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = FrameInterval };
        _frameTimer.Tick += OnFrameTick;
        _taskbarWatchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TaskbarWatchInterval };
        _taskbarWatchTimer.Tick += OnTaskbarWatchTick;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
        SettingsManager.Changed += OnSettingsChanged;
        _coordinator.SnapshotUpdated += OnSnapshotUpdated;

        PreviousButton.Click += (_, _) => RequestSkip(previous: true, source: "按钮");
        NextButton.Click += (_, _) => RequestSkip(previous: false, source: "按钮");
        PlayPauseButton.Click += (_, _) => RequestPlayPauseToggle(source: "按钮");
    }

    /// <summary>
    /// 窗口消息钩子，目前只处理滚轮。
    ///
    /// 为什么不用 WPF 的 <c>MouseWheel</c> 事件：那个事件由 WPF 的鼠标输入提供器发出，而提供器只处理来自
    /// "活动鼠标源"的滚轮消息，活动源又依赖窗口被激活。本窗口带 <c>WS_EX_NOACTIVATE</c>（绝不能抢任务栏的焦点），
    /// 于是它从来不是活动源，WPF 会把滚轮消息整条丢掉——实测（2026-10-14）外部投递的 WM_MOUSEWHEEL 完全没有反应。
    /// 钩子拿到的消息早于输入提供器，不依赖激活状态，因此在这里处理才可靠。
    /// The window-message hook, currently handling only the wheel.
    ///
    /// Why not WPF's <c>MouseWheel</c> event: that event is raised by WPF's mouse input provider, and the provider only handles
    /// wheel messages coming from the "active mouse source", which in turn depends on the window being activated. This window
    /// carries <c>WS_EX_NOACTIVATE</c> (it must never steal focus from the taskbar), so it is never the active source and WPF
    /// drops the wheel message outright — measured 2026-10-14, an externally posted WM_MOUSEWHEEL did nothing at all. The hook
    /// sees the message before the input provider and does not depend on activation, which makes it the reliable place.
    /// </summary>
    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_MOUSEWHEEL)
        {
            // wParam 高 16 位是"滚了几格"的有符号倍数，低 16 位是按键状态，这里只要前者。
            // The high word of wParam is a signed multiple of one notch; the low word holds key state, which is not needed here.
            var delta = (short)((long)wParam >> 16);
            if (delta != 0)
            {
                Log.Info("Transport", $"收到滚轮事件（delta {delta}）。");
                if (RequestSkip(previous: delta > 0, source: "滚轮"))
                    handled = true;
            }
        }
        else if (message == WM_MOUSEMOVE)
        {
            // lParam 的low/high 16 位就是指针在客户区里的坐标；坐标为负说明指针在非客户区（标题栏一类的边缘）。
            // 悬停显隐不看 WPF 的 IsMouseOver：WebView2 那一块的命中测试不完全可靠，而钩子拿到的是原始窗口消息，
            // 指针进没进窗口由这里一句话定死。
            // The low and high words of lParam are the pointer's client-area coordinates; negative values mean the pointer is
            // over the non-client area (frame edges and the like). Hover reveal does not consult WPF's IsMouseOver, whose hit
            // testing over the WebView2 region is not fully reliable, while the hook sees the raw window messages, so "is the
            // pointer inside" is settled here in one line.
            UpdatePointerInside(new PointerPosition(lParam));
        }
        else if (message == WM_MOUSELEAVE)
        {
            UpdatePointerInside(null);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// 指针在客户区内的位置；null 表示不在窗口内（收到 WM_MOUSELEAVE 或压根没有指针）。
    /// The pointer's position inside the client area; null means it is not over the window (WM_MOUSELEAVE arrived, or there
    /// is no pointer at all).
    /// </summary>
    private readonly record struct PointerPosition(int X, int Y)
    {
        public PointerPosition(IntPtr lParam) : this(
            (short)(lParam.ToInt64() & 0xFFFF),
            (short)((lParam.ToInt64() >> 16) & 0xFFFF))
        {
        }
    }

    /// <summary>
    /// 根据钩子送来的指针位置刷新"指针是否在窗口内"，状态真的变化时才动按钮区——
    /// 鼠标每移动一像素都会送一条 WM_MOUSEMOVE，不去重就是每像素一次重算。
    /// Refreshes "is the pointer inside the window" from the hook's position, touching the button cluster only on a real
    /// change: every pixel of movement posts a WM_MOUSEMOVE, and without the guard each pixel would trigger a recompute.
    /// </summary>
    private void UpdatePointerInside(PointerPosition? position)
    {
        if (_closing)
            return;

        // WM_MOUSEMOVE 的 lParam 是**物理像素**的客户区坐标，而 ActualWidth/ActualHeight 是 DIP，
        // 两者直接比较在非 100% 缩放下就是错的：150% 时窗口右下角那一块（切歌按钮正好在那里）的物理坐标
        // 大于以 DIP 计的窗口尺寸，"指针在窗口内"被误判成 false，按钮刚露头就被收起——
        // 鼠标越朝按钮方向走越必然触发，所以按钮永远点不到。比较前必须把 DIP 尺寸换算成物理像素。
        // WM_MOUSEMOVE's lParam holds client coordinates in **physical pixels** while ActualWidth/ActualHeight are in DIP,
        // so comparing them directly is wrong at any scaling other than 100%: at 150% the physical coordinates of the
        // window's lower-right region — exactly where the skip buttons sit — exceed the DIP size, "pointer inside" was judged
        // false, and the buttons folded away the instant they appeared. Moving toward them was itself the trigger, which is
        // why they could never be clicked. The DIP size has to be converted to physical pixels before the comparison.
        var dpi = VisualTreeHelper.GetDpi(this);
        var inside = position is { } p &&
                     p.X >= 0 && p.Y >= 0 &&
                     p.X <= ActualWidth * dpi.DpiScaleX &&
                     p.Y <= ActualHeight * dpi.DpiScaleY;

        // 指针的横向位置要留到 DIP 里：判定"在哪一块上"用的是 DIP 布局，而 lParam 是物理像素。
        // 换算只做一次，之后每像素移动只是一次浮点除法加一次比较。
        // The pointer's horizontal position is kept in DIP: the region decision works in the DIP layout while lParam is in
        // physical pixels. The conversion happens once, after which each pixel of movement costs a division and a comparison.
        var wasInside = _pointerInsideWindow;
        _pointerInsideDipX = inside && position is { } q ? q.X / Math.Max(1, dpi.DpiScaleX) : null;
        _pointerInsideWindow = inside;

        // 区域变化也要往下走：指针从歌词划到曲目信息上时"在窗口内"没变，但磨砂该换到另一块。
        // A region change has to fall through as well: moving from the lyrics onto the track info leaves "inside" unchanged while
        // the frost has to move to the other part.
        var region = ResolveOverlayRegion();

        if (inside == wasInside && region == _overlayRegion)
            return;

        UpdateTransportTag();
    }

    /// <summary>
    /// 指针现在落在哪一块上。不在窗口内就是 <see cref="LyricsOverlayRegion.None"/>。
    ///
    /// 块的宽度用与栏宽同一套 footprint 算（封面列 + 曲目信息列），所以判定与排版不会各说各话：
    /// 栏宽公式里少算一列，这里就会在错的像素上切开两块。
    /// Which part the pointer is over; outside the window it is <see cref="LyricsOverlayRegion.None"/>.
    ///
    /// The part's width uses the same footprints the bar-width calculation uses (the artwork column plus the track-info column),
    /// so the decision and the layout cannot disagree: omitting one column in the bar-width formula would make this split the two
    /// parts at the wrong pixel.
    /// </summary>
    private LyricsOverlayRegion ResolveOverlayRegion()
    {
        if (!_pointerInsideWindow || _pointerInsideDipX is not { } x)
            return LyricsOverlayRegion.None;

        var settings = SettingsManager.Current;
        var infoPartWidthDip = LyricsHoverRegionPolicy.ResolveInfoPartWidthDip(
            settings.LyricsCoverArtEnabled,
            ResolveCoverSizeDip(),
            CoverGapDip,
            _appliedTrackInfoWidthDip > 0 ? _appliedTrackInfoWidthDip : 0,
            TrackInfoGapDip);

        return LyricsHoverRegionPolicy.Resolve(x - LayoutPaddingInlineDip, infoPartWidthDip);
    }

    /// <summary>
    /// 把"滚轮上下曲"的提示摆在封面与曲目信息那一块上。
    ///
    /// 它只在指针停在这一块上时出现（Tag 由 <see cref="UpdateTransportTag"/> 设置），而且必须盖住整块：
    /// 提示出现时这一块正好被磨砂糊掉，提示压在上面才读得清；位置每帧跟着块宽走，定宽与自适应两种模式都不会跑偏。
    /// Places the "scroll wheel skips tracks" hint over the artwork-and-track-info part.
    ///
    /// It appears only while the pointer rests on that part (the Tag is set by <see cref="UpdateTransportTag"/>) and has to cover
    /// the whole part: that part is exactly what the frost blurs while the hint is up, so sitting on top of it is what keeps the
    /// hint readable. Its position follows the block's width every frame, so neither a fixed nor an automatic width can push it
    /// off.
    /// </summary>
    private void UpdateInfoHintLayout(AppSettings settings)
    {
        var infoPartWidthDip = LyricsHoverRegionPolicy.ResolveInfoPartWidthDip(
            settings.LyricsCoverArtEnabled,
            ResolveCoverSizeDip(),
            CoverGapDip,
            _appliedTrackInfoWidthDip > 0 ? _appliedTrackInfoWidthDip : 0,
            TrackInfoGapDip);

        if (Math.Abs(infoPartWidthDip - _appliedInfoHintWidthDip) > 0.5)
        {
            _appliedInfoHintWidthDip = infoPartWidthDip;
            InfoHintPanel.Margin = new Thickness(LayoutPaddingInlineDip, 0, 0, 0);
            InfoHintPanel.Width = Math.Max(0, infoPartWidthDip);
        }

        // 字号跟着栏高走：任务栏高度从小图标到大任务栏差一倍，写死的字号在小任务栏上会溢出、
        // 在大任务栏上又小得读不出来。
        // The font size follows the bar height: taskbar heights differ by a factor of two, so a hard-coded size either overflows
        // on a small taskbar or becomes unreadably small on a large one.
        var hintFontSize = Math.Clamp(ResolveBarHeightDip() * 0.32, 10, 14);
        if (Math.Abs(hintFontSize - _appliedInfoHintFontSize) > 0.1)
        {
            _appliedInfoHintFontSize = hintFontSize;
            InfoHintText.FontSize = hintFontSize;
        }
    }

    /// <summary>
    /// 请求播放/暂停。与切歌共用 <see cref="TryBeginTransport"/> 的开关、可见性与冷却判断。
    /// Requests a play/pause toggle. It shares the setting, visibility, and cooldown checks with skipping through
    /// <see cref="TryBeginTransport"/>.
    /// </summary>
    private bool RequestPlayPauseToggle(string source)
    {
        if (!TryBeginTransport(source))
            return false;

        if (!_coordinator.Latest.CanPlayPause)
        {
            // 播放器没有通过 SMTC 声明这项能力，按钮本应就是灰的；走到这里多半是能力刚变化，忽略即可。
            // The player did not declare this capability over SMTC and the button should be grey; reaching here means the
            // capability just changed, so ignoring it is right.
            Log.Info("Transport", $"忽略{source}播放/暂停请求：当前播放器未提供该控制。");
            return false;
        }

        _ = TogglePlayPauseAsync();
        return true;
    }

    /// <summary>
    /// 一次传输控制（切歌或播放/暂停）的公共前置判断：窗口是否在用、有没有内容可呈现、鼠标操作是否开着、
    /// 以及是否还在冷却期内。四项任一不满足就直接返回 false，调用方不需要再判一遍。
    /// Shared preconditions for one transport action (skip or play/pause): is the window alive, is there anything to present,
    /// is mouse control on, and is the cooldown over. Any failure returns false and the caller need not re-check.
    /// </summary>
    private bool TryBeginTransport(string source)
    {
        if (_closing || _transport is null || !_appliedVisible)
            return false;

        if (!SettingsManager.Current.LyricsMouseControlEnabled)
        {
            // 鼠标操作被关掉时窗口是点击穿透的，正常情况下收不到事件；真收到了说明扩展样式还没刷新，忽略即可。
            // With mouse control off the window is click-through and normally receives nothing; if it does, the extended style
            // has not been refreshed yet, and ignoring it is the right answer.
            Log.Info("Transport", $"忽略{source}控制请求：设置里的「鼠标操作」已关闭。");
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastSkipAt < SkipCooldown)
        {
            // 去抖挡下的请求不记日志：滚一下手腕就会产生十几条，记下来只会淹没有用信息。
            // Debounced requests are not logged: a flick of the wrist produces a dozen of them and logging each would drown
            // out everything useful.
            return false;
        }

        _lastSkipAt = now;
        return true;
    }

    /// <summary>
    /// 请求切歌。滚轮与按钮共用这一条路径，因此去抖、开关判断与日志只写一遍。
    /// Requests a skip. The wheel and the buttons share this one path, so the debounce, the setting check, and the logging live
    /// in a single place.
    /// </summary>
    /// <param name="previous">true 表示上一首。/ true for the previous track.</param>
    /// <param name="source">触发来源，只用于日志。/ What triggered the request; used only for logging.</param>
    /// <returns>是否真的发出了切歌请求（被开关或去抖挡下时为 false）。/ Whether a skip was actually issued (false when a setting or the debounce filtered it).</returns>
    private bool RequestSkip(bool previous, string source)
    {
        if (!TryBeginTransport(source))
            return false;

        // 播放器不支持的按钮本应已置灰；这里再判一次是防"能力刚变化"的窗口期。
        // A button the player cannot honour should already be greyed out; checking once more guards the window in which the
        // capability just changed.
        if (previous ? !_coordinator.Latest.CanSkipPrevious : !_coordinator.Latest.CanSkipNext)
        {
            Log.Info("Transport", $"忽略{source}切歌请求：当前播放器未提供该控制。");
            return false;
        }

        _ = SkipAsync(previous);
        return true;
    }

    private async Task SkipAsync(bool previous)
    {
        try
        {
            if (previous)
                await _transport!.SkipPreviousAsync();
            else
                await _transport!.SkipNextAsync();
        }
        catch (Exception ex)
        {
            // 切歌失败不该让歌词栏出事：播放器可能刚好在这一刻退出。
            // A failed skip must not take the bar down: the player may have exited at that very moment.
            Log.Warn("Transport", $"切歌请求出错: {ex.Message}");
        }
    }

    private async Task TogglePlayPauseAsync()
    {
        try
        {
            await _transport!.TogglePlayPauseAsync();
        }
        catch (Exception ex)
        {
            // 播放/暂停失败同样不能让歌词栏出事。
            // A failed play/pause toggle must not take the bar down either.
            Log.Warn("Transport", $"播放/暂停请求出错: {ex.Message}");
        }
    }

    /// <summary>换一块任务栏停靠（设置界面切换显示器时调用）。/ Re-docks onto another taskbar, used when the settings UI switches displays.</summary>
    public void Retarget(TaskbarTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Handle == _target.Handle)
            return;

        Undock();
        _target = target;
        if (_hostLoaded)
            Dock();
    }

    /// <summary>
    /// 拆掉当前渲染器、重新找一次任务栏并重新停靠。设置窗口的「重新停靠到任务栏」走这里，
    /// 因为歌词渲染引擎（原生 / 网页）就是在那里切换的，而换引擎必须把旧渲染器整个拆掉——
    /// 两者的宿主元素完全不同，不拆就会两个都挂在歌词区里。
    /// Tears down the current renderer, finds the taskbar again, and re-docks. The settings window's "re-dock" button routes
    /// here because that is where the renderer engine (native / web) is switched, and switching it has to tear the old
    /// renderer down entirely: the two have completely different host elements, and leaving the old one would stack both in
    /// the lyric area.
    /// </summary>
    public void RedockAndReloadRenderer()
    {
        if (_closing)
            return;

        ReleaseRenderer();
        _rendererFailed = false;
        _rendererRetriedCurrentTier = false;
        SetBarVisible(false);
        _appliedVisible = false;

        var taskbars = _dock.EnumerateTaskbars();
        var replacement = taskbars.FirstOrDefault(t => t.IsPrimary) ?? taskbars.FirstOrDefault();
        if (replacement is not null)
            _target = replacement;

        Undock();
        if (!_hostLoaded)
            return;

        Dock();
        EnsureRendererAsync();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // 滚轮必须走窗口消息钩子，不能依赖 WPF 的 MouseWheel 事件，原因见 OnWindowMessage。
        // The wheel has to go through the window-message hook rather than WPF's MouseWheel event; see OnWindowMessage.
        HwndSource.FromHwnd(_hwnd)?.AddHook(OnWindowMessage);

        ApplyClickThrough();
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_hostLoaded)
            return;

        _hostLoaded = true;
        Dock();
        _frameTimer.Start();
        _taskbarWatchTimer.Start();
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        _frameTimer.Stop();
        _taskbarWatchTimer.Stop();
        HwndSource.FromHwnd(_hwnd)?.RemoveHook(OnWindowMessage);
        SettingsManager.Changed -= OnSettingsChanged;
        _coordinator.SnapshotUpdated -= OnSnapshotUpdated;
        ReleaseRenderer();
        Undock();
    }

    /// <summary>
    /// 把窗口变成任务栏的子窗口并摆好位置。停靠失败（例如任务栏句柄已失效）时只记日志并保持隐藏，
    /// 绝不抛异常——启动阶段抛异常会让整个程序起不来。
    /// Docks the window into the taskbar and positions it. A failed dock (a stale taskbar handle, for instance) is logged and
    /// leaves the window hidden rather than throwing, because throwing during startup would keep the whole app from launching.
    /// </summary>
    private void Dock()
    {
        if (!_dockToTaskbar)
        {
            // --no-dock 诊断模式：不做任何 SetParent，只把窗口当作普通顶层窗口摆在屏幕上。
            // 用它可以把"WebView2 自己起不来"与"任务栏跨进程停靠把它带崩了"彻底分开。
            _docked = false;
            _barVisible = true;
            _appliedOpacityVisible = true;
            Opacity = 1;
            Width = 900;
            Height = 64;
            Log.Warn("Taskbar", "已按 --no-dock 跳过任务栏停靠，窗口以普通顶层窗口运行（仅供排障）。");
            return;
        }

        if (_hwnd == IntPtr.Zero || _target.Handle == IntPtr.Zero)
            return;

        try
        {
            _dock.Dock(_hwnd, _target.Handle);
            _docked = true;
            ApplyGeometry();

            // 停靠完成，撤掉构造函数里用来挡住"闪在屏幕中央"那一帧的不透明度 0。
            // 此后窗口保持不透明，直到 WebView2 初始化完成——渲染器就绪前变透明会让它崩溃。
            _barVisible = true;
            _appliedOpacityVisible = true;
            Opacity = 1;
            ApplyClickThrough();

            Log.Info("Taskbar", $"已停靠到任务栏 {_target.MonitorDeviceId}（{_target.Width}×{_target.Height}）");
        }
        catch (Exception ex)
        {
            _docked = false;
            Log.Error("Taskbar", "停靠任务栏失败。", ex);
        }
    }

    /// <summary>
    /// 显示或隐藏歌词栏。
    /// 关键约束：WebView2 的合成控件（<c>WebView2CompositionControl</c>）**在不可见窗口里无法完成初始化**——
    /// 窗口一旦变成不透明 0（或调用 <c>ShowWindow(SW_HIDE)</c>），GPU 与工具子进程立刻以 <c>0x00000007</c> 成串崩溃，
    /// 连 <c>--disable-gpu</c> 都救不回来（实测 2026-10-14：改软件渲染后仍然崩溃并停用歌词显示）。
    /// 所以渲染器就绪之前，窗口必须保持不透明；这期间 Web 端显示的是"正在检索歌词…"，正好是想要的过渡提示。
    /// 渲染器就绪之后，才允许按"有没有内容可呈现"真正把歌词栏变透明。
    /// Shows or hides the bar. The binding constraint is that WebView2's composition control cannot finish initializing inside
    /// an invisible window: as soon as the window goes to opacity 0 (or <c>ShowWindow(SW_HIDE)</c> is called) its GPU and
    /// utility subprocesses crash in bursts with <c>0x00000007</c>, and <c>--disable-gpu</c> cannot rescue it — measured
    /// 2026-10-14, the software-rendering rebuild still crashed and lyric display was disabled. The window therefore stays
    /// opaque until the renderer is ready; the web side shows "searching for lyrics…" during that window, which is exactly the
    /// transition hint we want. Only once the renderer is ready may the bar actually go transparent for lack of content.
    /// </summary>
    private void SetBarVisible(bool visible)
    {
        _barVisible = visible;

        // 渲染器已经彻底失败时不必再护着窗口不透明——没有 WebView2 就没有崩溃风险，直接隐藏即可。
        var effective = visible || (!_rendererFailed && _renderer?.IsReady != true);
        if (_appliedOpacityVisible == effective)
            return;

        _appliedOpacityVisible = effective;
        Opacity = effective ? 1 : 0;
        ApplyClickThrough();
    }

    private void Undock()
    {
        if (!_docked || _hwnd == IntPtr.Zero)
            return;

        try
        {
            _dock.Undock(_hwnd);
        }
        catch (Exception ex)
        {
            Log.Warn("Taskbar", $"解除停靠时出错: {ex.Message}");
        }
        finally
        {
            _docked = false;
        }
    }

    private void OnTaskbarWatchTick(object? sender, EventArgs e)
    {
        if (_closing || _hwnd == IntPtr.Zero || !_dockToTaskbar)
            return;

        // Explorer 重启会让父窗口消失：子窗口此时会跟着被销毁或悬空，必须重新找任务栏并重新停靠。
        if (!IsWindow(_target.Handle))
        {
            Log.Warn("Taskbar", "任务栏窗口已失效（Explorer 可能已重启），正在重新停靠。");
            _docked = false;
            var replacement = FindMatchingTaskbar();
            if (replacement is not null)
            {
                _target = replacement;
                Dock();
            }
            return;
        }

        if (!_docked)
            Dock();
    }

    private TaskbarTarget? FindMatchingTaskbar()
    {
        var taskbars = _dock.EnumerateTaskbars();
        return taskbars.FirstOrDefault(t => t.IsPrimary) ?? taskbars.FirstOrDefault();
    }

    private void OnSnapshotUpdated(MediaSnapshot snapshot) => Dispatcher.BeginInvoke(UpdateFrame);

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (_closing)
            return;

        Dispatcher.BeginInvoke(() =>
        {
            ApplyClickThrough();
            ApplyRendererStyle();
            UpdateFrame();
        });
    }

    private void OnFrameTick(object? sender, EventArgs e) => UpdateFrame();

    /// <summary>
    /// 每帧把最新快照投影成目标画面并推给 Web 端。位置外推、选行、第二行语义全在
    /// <c>LyricsPresentationProjector</c> 里完成，这里只做"发出去 + 调尺寸 + 显隐"。
    /// Projects the latest snapshot into a target frame every tick and pushes it to the web side. Position extrapolation, line
    /// selection, and second-row semantics all happen inside <c>LyricsPresentationProjector</c>; this method only sends,
    /// resizes, and toggles visibility.
    /// </summary>
    private void UpdateFrame()
    {
        if (_closing || !_hostLoaded)
            return;

        var settings = SettingsManager.Current;
        var previous = _frame;
        // 迟滞器常驻这一条窗口：位置抖动时行号与擦亮位置都不回退，换歌时它自己重置（见 LyricLineCursor）。
        // The cursor lives for the whole window: the line index and the scan position stay put while the position jitters, and it
        // resets itself on a track change (see LyricLineCursor).
        _frame = LyricsPresentationProjector.Project(
            _coordinator.Latest,
            settings,
            DateTimeOffset.UtcNow,
            _lineCursor);

        // 封面独立于歌词帧：即使这一帧没有可呈现的歌词，封面也要跟上当前曲目，
        // 否则重新显示歌词栏时会先闪一下上一首的封面。
        ApplyCover(_coordinator.Latest);

        // 曲目信息与封面一样独立于歌词帧：它不属于任何一行歌词，也不该跟着换行、淡入淡出一起动。
        // Track info, like the artwork, is independent of the lyric frame: it belongs to no lyric row and has no business moving
        // with a line transition or a crossfade.
        ApplyTrackInfo(settings);

        var canPresent = _frame.IsVisible && !_rendererFailed && _renderer?.IsReady == true;
        if (canPresent)
        {
            if (!ReferenceEquals(previous, _frame) || !_appliedVisible)
            {
                _renderer!.Present(_frame, animateTransition: true);
            }
        }

        if (canPresent != _appliedVisible)
        {
            _appliedVisible = canPresent;

            // 显隐切换是排障时最有价值的一条线索：歌词栏该出现却没出现、或该消失却留着占位文案，
            // 都要靠它来判断是"没内容可呈现"还是"渲染器没就绪"。
            // The visibility transition is the single most useful clue when troubleshooting: whether the bar failed to
            // appear or kept showing a placeholder, this line separates "nothing to present" from "renderer not ready".
            Log.Info("Lyrics", canPresent
                ? $"歌词栏显示: 「{_frame.Current}」（行下标 {_frame.CurrentLineIndex}）"
                : "歌词栏隐藏（没有可呈现的歌词行）。");
        }

        LogLineChange();
        LogScanRegression();

        // 每帧都走一次：渲染器就绪前这个方法会拒绝把窗口变透明，就绪之后才真正生效，
        // 因此必须持续调用，而不是只在 canPresent 跳变时调用。
        SetBarVisible(canPresent);
        UpdateTransport();

        if (canPresent)
        {
            ApplyGeometry();
            UpdateProgress();
        }
    }

    /// <summary>
    /// 每换一行歌词记一条日志（换歌后的第一行也记）。只记"行下标变化"这一种事件：诊断"歌词不跟手"时，
    /// 需要把每次换行的时刻与实际播放位置对上，而位置通道之间的切换由 <see cref="SmtcMediaReader"/> 那边记。
    /// 这里打印的是投影时真正用来选行的那个位置（含外推），所以两边的日志可以直接对照。
    /// Logs one line per lyric line change (including the first line of a new track). Only the index change is logged: diagnosing
    /// "the lyrics do not follow" needs each line switch next to the real playback position, while the position-channel
    /// switches are logged by <see cref="SmtcMediaReader"/>. The position printed here is the very one the projector used to
    /// pick the line (extrapolation included), so the two logs line up.
    /// </summary>
    private void LogLineChange()
    {
        var snapshot = _coordinator.Latest;
        var trackKey = string.Join('\u001f', snapshot.SourceId, snapshot.Title, snapshot.Artist);

        if (!string.Equals(trackKey, _lastLoggedTrackKey, StringComparison.Ordinal))
        {
            _lastLoggedTrackKey = trackKey;
            _lastLoggedLineIndex = int.MinValue;
        }

        var index = _frame.CurrentLineIndex;
        if (index < 0 || index == _lastLoggedLineIndex)
            return;

        _lastLoggedLineIndex = index;
        var position = TaskbarExperiencePolicy.GetPosition(snapshot, DateTimeOffset.UtcNow);
        var text = _frame.Current.Length > 40 ? string.Concat(_frame.Current.AsSpan(0, 40), "…") : _frame.Current;

        // 这一行的歌词身份也一起记：用户报"有的歌擦亮、有的不擦亮"时，日志里就能直接看出这次拿到的是逐字时间轴
        // （Yrc/SyllableSynced，本行有逐字片段）还是只有行级时间轴（Lrc/LineSynced，合成擦亮）。
        // The lyric identity of this line is logged too: when a user reports that some songs highlight and others do not, the log
        // shows directly whether this retrieval carries a word timeline (Yrc/SyllableSynced with word fragments) or only a
        // line-level one (Lrc/LineSynced, which gets the synthesised wipe).
        Log.Info("Lyrics", $"行切换: 第 {index + 1} 行「{text}」（播放位置 {position:0.##} 秒，来源 {snapshot.SourceName}，"
            + $"歌词 {DescribeLyrics(snapshot)}，本行 {DescribeLineWords(snapshot, index)}）");
    }

    /// <summary>把这次取到的歌词文档摊成一行诊断文本（来源格式 / 同步级别 / 匹配分）。</summary>
    private static string DescribeLyrics(MediaSnapshot snapshot)
    {
        var lyrics = snapshot.Lyrics;
        return lyrics is null
            ? "未取到"
            : $"{lyrics.Source}/{lyrics.Document.SourceFormat}/{lyrics.Document.SyncType}（分 {lyrics.MatchScore}）";
    }

    /// <summary>这一行的逐字片段数与总字数；没有逐字时间轴时是 0 段，擦亮由行窗口合成。</summary>
    private static string DescribeLineWords(MediaSnapshot snapshot, int index)
    {
        var lines = snapshot.Lyrics?.Document.Lines;
        if (lines is null || index < 0 || index >= lines.Count)
        {
            return "逐字 ? 段";
        }

        var line = lines[index];
        var characters = 0;
        foreach (var word in line.Words)
        {
            characters += word.Text?.Length ?? 0;
        }

        return $"逐字 {line.Words.Count} 段/{characters} 字";
    }

    /// <summary>
    /// 擦亮倒退检测：同一行内投影给出的擦亮进度只增不减（迟滞器保证喂进去的位置单调），所以出现倒退就说明画面上
    /// 的扫描头会"跳回开头"。用户报过"每句开头擦亮一两个词又跳回开头重来"，这条日志是它唯一的现场证据。
    /// Scan-regression detection: inside one line the projected highlight progress only moves forward (the cursor keeps the
    /// position it feeds monotonic), so a backward step means the scan head visibly snaps back — the exact symptom users
    /// reported as "the first words light up, then the wipe restarts". This line is the only on-site evidence of it.
    /// </summary>
    private void LogScanRegression()
    {
        var progress = _frame.WordScanProgress;
        var index = _frame.CurrentLineIndex;
        if (index < 0 || progress is not double value)
        {
            _lastScanProgress = null;
            _scanRegressionLogged = false;
            return;
        }

        if (index != _lastScanLineIndex)
        {
            _lastScanLineIndex = index;
            _lastScanProgress = value;
            _scanRegressionLogged = false;
            return;
        }

        if (!_scanRegressionLogged && _lastScanProgress is double previous && value < previous - ScanRegressionTolerance)
        {
            _scanRegressionLogged = true;
            var position = TaskbarExperiencePolicy.GetPosition(_coordinator.Latest, DateTimeOffset.UtcNow);
            Log.Warn("Lyrics", $"擦亮倒退: 行下标 {index}，进度 {previous:0.###} → {value:0.###}"
                + $"（播放位置 {position:0.##} 秒）。同一行内进度不该回退，说明画面上的扫描头会跳回开头。");
        }

        _lastScanProgress = value;
    }

    /// <summary>
    /// 计算并应用窗口几何。宽度按当前行与第二行里更长的那条量出来（关闭固定宽度时），
    /// 高度取任务栏在 DIP 下的高度，让歌词栏正好铺满任务栏的厚度。
    /// Computes and applies the window geometry. The width comes from whichever of the active line and the second row is longer
    /// (unless a fixed width is configured), and the height is the taskbar's height in DIP so the bar fills the taskbar thickness.
    /// </summary>
    private void ApplyGeometry()
    {
        if (!_docked || _hwnd == IntPtr.Zero)
            return;

        var settings = SettingsManager.Current;
        var widthDip = MeasureBarWidthDip(_frame, settings);
        var heightDip = Math.Max(16, _target.Height / Math.Max(1, _target.DpiScale));

        if (Math.Abs(widthDip - _appliedWidthDip) > 0.5)
        {
            _appliedWidthDip = widthDip;
            Width = widthDip;
            Height = heightDip;
        }

        // 按钮组居中在歌词框里，所以它的左外边距就是歌词框的左边界，右外边距是布局的内边距。
        // 歌词框的宽度会随曲目信息块变化，这里每帧重算，按钮就永远待在歌词框正中间。
        // The button cluster is centred inside the lyrics box, so its left margin is that box's left edge and its right margin is
        // the layout's own padding. The box changes width with the track-info block, so this is recomputed every frame and the
        // buttons stay dead centre in it.
        var paneLeftDip = ResolveLyricsPaneLeftDip(settings);
        if (Math.Abs(paneLeftDip - _appliedTransportLeftDip) > 0.5)
        {
            _appliedTransportLeftDip = paneLeftDip;
            TransportPanel.Margin = new Thickness(paneLeftDip, 0, LayoutPaddingInlineDip, 0);
        }

        UpdateInfoHintLayout(settings);

        var scale = Math.Max(1, _target.DpiScale);
        var widthPx = (int)Math.Round(widthDip * scale);
        var heightPx = (int)Math.Round(heightDip * scale);
        var leftPx = ResolveBarLeftPx(settings, widthPx, scale);

        // 位置变化才记日志：任务栏宽度、歌词长度都会让位置每帧微调，不设阈值会把日志刷满。
        if (_appliedLeftPx is not int applied || Math.Abs(leftPx - applied) >= 4)
        {
            _appliedLeftPx = leftPx;
            Log.Info("Taskbar", $"歌词栏位置: {settings.LyricsBarAlignment}，左边界 {leftPx}px，宽度 {widthPx}px（任务栏宽 {_target.Width}px）");
        }

        _dock.SetPosition(_hwnd, leftPx, 0, widthPx, heightPx);
    }

    /// <summary>
    /// 按停靠位置算出歌词栏左边界的物理像素。左/右对齐时偏移量是"边距"，居中对齐时是"微调量"；
    /// 结果一律夹在任务栏范围内，避免用户把偏移量调大后歌词栏整个跑到屏幕外看不见。
    /// Resolves the bar's left edge in physical pixels from the docking position. For left/right the offset acts as a margin;
    /// for centre it acts as a nudge. The result is always clamped inside the taskbar so a large offset cannot push the bar
    /// off screen where it would silently vanish.
    /// </summary>
    private int ResolveBarLeftPx(AppSettings settings, int widthPx, double scale)
    {
        var offsetPx = (int)Math.Round(settings.TaskbarOffsetDip * scale);
        var available = Math.Max(0, _target.Width - widthPx);

        var left = settings.LyricsBarAlignment switch
        {
            LyricsBarAlignment.Center => (available / 2) + offsetPx,
            LyricsBarAlignment.Right => available - offsetPx,
            _ => offsetPx
        };

        return Math.Clamp(left, 0, available);
    }

    private double MeasureBarWidthDip(LyricsPresentationFrame frame, AppSettings settings)
    {
        if (settings.LyricsFixedWidthEnabled)
            return settings.LyricsFixedWidthDip;

        var primary = MeasureTextWidthDip(frame.Current, settings);
        var secondaryText = string.IsNullOrEmpty(frame.Next) ? frame.CurrentTranslation : frame.Next;
        var secondary = MeasureTextWidthDip(secondaryText, settings);

        // 封面占据固定的横向空间，自动宽度必须把它算进去，否则开了封面之后歌词会被右端省略号截掉。
        // The artwork takes a fixed slice of horizontal space, so automatic width has to include it; otherwise turning the
        // cover on makes the lyrics get truncated by the trailing ellipsis.
        var coverFootprint = settings.LyricsCoverArtEnabled
            ? ResolveCoverSizeDip() + CoverGapDip
            : 0;

        // 切歌按钮不再预留宽度：它已经从"右侧独立一列"改成覆盖在歌词之上（见 XAML），
        // 占位就只剩浮层那点视觉宽度，而浮层的意义正是"浮在歌词上"——再留一条空带就把歌词白白挤窄了。
        // The skip buttons no longer reserve width: they changed from a dedicated right-hand column to an overlay above the
        // lyrics (see the XAML), so all they occupy is the floating plate's own visual width, and reserving a lane for them
        // would only squeeze the lyrics for nothing — the point of the plate is to float over them.

        // 曲目信息块同理：它也是网格里独立的一列，不预留宽度就会和歌词抢同一块横向空间。
        // 固定栏宽下同样不追加，与封面的处理保持一致。
        // The track-info block is the same: it too is its own grid column, and without reserving width it would compete with the
        // lyrics for the same horizontal space. Under a fixed width nothing is added, matching the cover.
        var (trackInfoTitle, trackInfoArtist) = LyricsPresentationProjector.ResolveTrackInfoLines(_coordinator.Latest);
        var trackInfoWidthDip = ResolveTrackInfoWidthDip(trackInfoTitle, trackInfoArtist, settings);
        var trackInfoFootprint = trackInfoWidthDip > 0 ? trackInfoWidthDip + TrackInfoGapDip : 0;

        return Math.Clamp(
            Math.Max(primary, secondary) + WebLyricsHorizontalInsetDip + coverFootprint + trackInfoFootprint,
            MinimumBarWidthDip,
            settings.LyricsMaxWidthDip);
    }

    /// <summary>歌词栏高度（DIP），由任务栏的物理高度换算而来。/ Bar height in DIP, derived from the taskbar's physical height.</summary>
    private double ResolveBarHeightDip() => Math.Max(16, _target.Height / Math.Max(1, _target.DpiScale));

    /// <summary>
    /// 封面边长（DIP）。跟着歌词栏高度走而不是写死：任务栏高度从小图标到大任务栏能差一倍，
    /// 写死的封面在小任务栏上会溢出、在大任务栏上又显得局促。
    /// Cover edge length in DIP. It follows the bar height instead of being hard-coded: taskbar heights vary by a factor of two
    /// between small icons and large taskbars, so a fixed size overflows on the small one and looks cramped on the large one.
    /// </summary>
    private double ResolveCoverSizeDip() => Math.Clamp(ResolveBarHeightDip() - CoverVerticalInsetDip, 16, 48);

    /// <summary>
    /// 歌词框左边界（DIP）：布局水平内边距 + 封面列 + 曲目信息块列，与 style.css 里 .shell 的列定义一一对应。
    /// 按钮要居中在歌词框里，就得先知道歌词框从哪儿开始；封面或曲目信息块收起时它俩的列宽为 0，所以一并跳过。
    /// Left edge of the lyrics box in DIP: the layout's horizontal padding plus the cover column and the track-info column,
    /// mirroring .shell's column list in style.css. Centring the buttons inside the lyrics box requires knowing where the box
    /// begins; a folded cover or track-info block contributes nothing because its column collapses to zero.
    /// </summary>
    private double ResolveLyricsPaneLeftDip(AppSettings settings)
    {
        var left = LayoutPaddingInlineDip;

        if (settings.LyricsCoverArtEnabled)
            left += ResolveCoverSizeDip() + CoverGapDip;

        if (_appliedTrackInfoWidthDip > 0)
            left += _appliedTrackInfoWidthDip + TrackInfoGapDip;

        return left;
    }

    private double MeasureTextWidthDip(string text, AppSettings settings)
        => MeasureTextWidthDip(text, settings, settings.LyricsFontSize);

    /// <summary>
    /// 按指定字号量文字宽度。曲目信息块的两行用的是两个不同字号（曲名跟随主字号，歌手跟随 Web 端的
    /// <c>--next-size</c>），所以字号必须是参数而不是永远取 <see cref="AppSettings.LyricsFontSize"/>。
    /// Measures text width at an explicit font size. The two track-info rows use two different sizes (the title follows the main
    /// size, the artist follows the web side's <c>--next-size</c>), so the size has to be a parameter rather than always
    /// <see cref="AppSettings.LyricsFontSize"/>.
    /// </summary>
    private double MeasureTextWidthDip(string text, AppSettings settings, double fontSize)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        // 每帧都会量一遍主行与第二行，而相邻两帧量的几乎总是同一批字符串：20fps x 2 个
        // FormattedText 的分配纯属白烧（测量用的字体/画刷都是可复用的）。带界缓存挡掉重复测量，
        // 条目涨到容量上限就整体清空——一首歌的稳定状态也就几行歌词，命中率接近 100%。
        // Both rows are remeasured every frame while consecutive frames almost always measure the same strings: 20fps times
        // two FormattedText allocations is pure waste. A bounded cache absorbs the repeats and clears wholesale at its
        // capacity ceiling, so it never grows unbounded — one song settles on a handful of lines, keeping the hit rate near 100%.
        var key = new MeasureKey(text, ResolveFontFamily(settings), fontSize);
        if (_measureCache.TryGetValue(key, out var cached))
            return cached;

        var typeface = new Typeface(
            new FontFamily(key.FontFamily),
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        var width = formatted.WidthIncludingTrailingWhitespace;

        // Web 歌词用 CSS letter-spacing 拉开字距，每个字符都会多出"字号 × 百分比"的推进量；
        // 自动宽度必须补上同样的量，否则拉大字距后整行会被右端省略号截掉。
        var percent = LyricsCharacterSpacing.Normalize(settings.LyricsCharacterSpacingPercent);
        if (percent > 0)
            width += text.Length * fontSize * percent / 100.0;

        if (_measureCache.Count >= MeasureCacheCapacity)
            _measureCache.Clear();
        _measureCache[key] = width;
        return width;
    }

    /// <summary>文字宽度缓存的键：文本 + 字体 + 字号，三者缺一宽度就可能不同。/ The width cache key: text, family, and size — changing any one can change the width.</summary>
    private readonly record struct MeasureKey(string Text, string FontFamily, double FontSize);

    /// <summary>宽度缓存容量。一首歌的歌词行远小于此，命中率接近 100%。/ The width cache capacity; one song's lines are far fewer than this, so the hit rate approaches 100%.</summary>
    private const int MeasureCacheCapacity = 128;

    private readonly Dictionary<MeasureKey, double> _measureCache = new();

    private void UpdateProgress()
    {
        var settings = SettingsManager.Current;
        var snapshot = _coordinator.Latest;

        // 刻度用 LyricsDurationPolicy 而不是 snapshot.Duration：网易云音乐的时长恒为 0，而歌词末行结束时间人人都有，
        // 只认上报时长会让进度条在网易云上永远不出现，用户看到的就是"开了进度条却没效果"。
        // The scale comes from LyricsDurationPolicy rather than snapshot.Duration: NetEase Cloud Music's duration is a permanent
        // zero while the last lyric line's end is available everywhere, and trusting only the reported duration would keep the
        // bar hidden forever there — which reads as "the progress-bar switch does nothing".
        var duration = LyricsDurationPolicy.Resolve(snapshot);
        var visible = settings.LyricsProgressBarEnabled && duration > 0;

        ProgressTrack.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
            return;

        var position = TaskbarExperiencePolicy.GetPosition(snapshot, DateTimeOffset.UtcNow);
        var ratio = Math.Clamp(position / duration, 0, 1);
        ProgressFill.Width = Math.Max(0, ActualWidth * ratio);
    }

    private void ApplyClickThrough()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        // 纯显示时让鼠标事件落到任务栏本身：否则歌词栏会挡住它覆盖的那一段任务栏（包括那里的图标点击）。
        // 歌词栏不可见时一律穿透，不管用户有没有勾选"鼠标操作"——看不见的窗口没有理由拦截点击。
        // 反过来，开了鼠标操作且歌词栏可见时必须取消穿透，否则滚轮与按钮永远收不到事件。
        // In display-only mode mouse events fall through to the taskbar itself, otherwise the bar blocks the taskbar segment it
        // covers (including icon clicks there). Whenever the bar is invisible it is click-through regardless of the mouse-control
        // setting: an invisible window has no business intercepting clicks. Conversely, with mouse control on and the bar
        // visible, click-through has to be cleared or the wheel and buttons would never receive anything.
        var interactive = SettingsManager.Current.LyricsMouseControlEnabled && _barVisible;
        if (_appliedInteractive == interactive)
            return;

        _appliedInteractive = interactive;
        var extended = GetWindowLong(_hwnd, GWL_EXSTYLE);
        extended = interactive
            ? extended & ~WS_EX_TRANSPARENT | WS_EX_NOACTIVATE
            : extended | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
        SetWindowLong(_hwnd, GWL_EXSTYLE, extended);

        // 改成点击穿透后窗口再也收不到鼠标消息，钩子里的"指针在窗口内"会停在最后一个已知状态。
        // 不主动清零的话，用户把鼠标操作重新打开时按钮会凭一个陈旧状态弹出来。
        // Once click-through is on the window stops receiving mouse messages and the hook's "pointer inside" freezes at its last
        // known value; without clearing it the buttons would pop up on a stale state the moment mouse control is re-enabled.
        if (!interactive && _pointerInsideWindow)
        {
            _pointerInsideWindow = false;
            UpdateTransportTag();
        }
    }

    /// <summary>
    /// 刷新切歌按钮的状态。**显隐交给 XAML 的悬停触发器**，这里只改两件事：告诉触发器"现在允不允许显示"
    /// （TransportPanel.Tag），以及每个按钮的可用性与图标。
    /// Refreshes the skip buttons' state. Visibility is left to the XAML hover trigger; this only updates whether showing is
    /// currently allowed (the panel's Tag) plus each button's enabled state and glyph.
    /// </summary>
    private void UpdateTransport()
    {
        UpdateTransportTag();

        var snapshot = _coordinator.Latest;

        // 播放/暂停图标的语义要跟着播放状态走：正在播放时给"暂停"符号，暂停时给"播放"符号。
        // 图标在同一帧里可能反复横跳，只在真的变化时赋值，避免每 50ms 触发一次重排。
        // The play/pause glyph follows the playback state: a pause symbol while playing, a play symbol while paused. It can
        // flip back and forth across frames, so it is only assigned on a real change to avoid a re-layout every 50 ms.
        var glyph = snapshot.IsPlaying ? "\uE769" : "\uE768";
        if (!string.Equals((string?)PlayPauseButton.Content, glyph, StringComparison.Ordinal))
        {
            PlayPauseButton.Content = glyph;
            PlayPauseButton.ToolTip = snapshot.IsPlaying ? "暂停" : "继续播放";
        }

        // 播放器自己声明能不能操作。置灰而不是隐藏：位置保持稳定，用户也能看出"是这个播放器不支持"而不是"按钮不见了"。
        // The player declares what it can do. Greyed out rather than hidden: the layout stays put, and the user can tell
        // "this player does not support it" apart from "the button disappeared".
        PreviousButton.IsEnabled = snapshot.CanSkipPrevious;
        NextButton.IsEnabled = snapshot.CanSkipNext;
        PlayPauseButton.IsEnabled = snapshot.CanPlayPause;
    }

    /// <summary>
    /// 单独刷新按钮区的"允不允许显示"（TransportPanel.Tag）。悬停状态、鼠标操作开关、有没有内容可呈现、
    /// 有没有传输服务，四个条件全部满足才允许展开；任何一条掉了立刻收起。
    /// 每帧的 <see cref="UpdateTransport"/> 与钩子的 <see cref="UpdatePointerInside"/> 都走这里，
    /// 因此"鼠标划入"能立刻展开而不必等下一帧。
    /// Refreshes the button cluster's "showing allowed" flag (TransportPanel.Tag) on its own. Hover state, the mouse-control
    /// setting, whether there is anything to present, and whether a transport service exists must all hold before the cluster
    /// may unfold; losing any one of them folds it back immediately. Both the per-frame <see cref="UpdateTransport"/> and the
    /// hook's <see cref="UpdatePointerInside"/> route through here, so a pointer entering unfolds the buttons at once rather
    /// than on the next frame.
    /// </summary>
    private void UpdateTransportTag()
    {
        var shown = _pointerInsideWindow &&
                    _transport is not null &&
                    SettingsManager.Current.LyricsMouseControlEnabled &&
                    _appliedVisible;

        // Tag 是显隐触发器的唯一条件，用字符串而不是布尔：DataTrigger 的比较走 ToString，
        // 布尔会在"Cached"这类值上出歧义，字符串更直白。
        // The Tag is the visibility trigger's only condition; a string rather than a bool because DataTrigger compares via
        // ToString, where booleans invite ambiguity, while a string is explicit.
        var targetTag = shown ? "Shown" : "Hidden";
        if (!string.Equals((string?)TransportPanel.Tag, targetTag, StringComparison.Ordinal))
            TransportPanel.Tag = targetTag;

        // 指针停在封面与曲目信息那一块上时给一句"滚轮上下曲"的提示：那一块本身没有可点的东西，
        // 而滚轮恰好是这一块上的操作，提示把两者对上。按钮区的开关与它无关，两者按各自的区域出现。
        // A "scroll wheel skips tracks" hint appears while the pointer rests on the artwork-and-track-info part: that part holds
        // nothing clickable, and the wheel is exactly what acts on it, so the hint pairs the two. It is independent of the button
        // cluster's switch — each appears according to its own region.
        var region = ResolveOverlayRegion();
        _overlayRegion = region;

        var hintTag = shown && region == LyricsOverlayRegion.Info ? "Shown" : "Hidden";
        if (!string.Equals((string?)InfoHintPanel.Tag, hintTag, StringComparison.Ordinal))
            InfoHintPanel.Tag = hintTag;

        ApplyOverlayPlate(region);
    }

    /// <summary>
    /// 把"指针在哪一块上"推给渲染器。真正的模糊在渲染器那一侧完成：原生渲染器糊的是自己那几个元素，
    /// WebView2 则由页面自己糊（WPF 的 Effect 作用不到合成控件）。
    /// 指针不在窗口内时推 <see cref="LyricsOverlayRegion.None"/>，两块都恢复原样。
    /// Pushes "which part the pointer is over" to the renderer. The actual blur happens on the renderer's side: the native
    /// renderer blurs its own elements, while WebView2 has the page do it (WPF effects cannot reach a composition control).
    /// Outside the window <see cref="LyricsOverlayRegion.None"/> goes out and both parts return to normal.
    /// </summary>
    /// <param name="region">指针所在的块。/ The part under the pointer.</param>
    private void ApplyOverlayPlate(LyricsOverlayRegion region)
    {
        if (_renderer is not { IsReady: true } || _appliedOverlayRegion == region)
            return;

        _appliedOverlayRegion = region;
        _renderer.SetOverlay(region);
    }

    /// <summary>
    /// 按设置创建歌词渲染器。默认是原生自绘（<see cref="LyricsRendererEngine.Native"/>）：宿主进程自己画，
    /// 不起任何浏览器子进程，因此任务管理器里只有一个 LyricsBar.exe。设置里也可以切回 WebView2 承载的网页版，
    /// 那条路保留着降级重建与超时看门狗，用来逐项对拍观感或在原生实现出问题时兜底。
    /// Builds the lyric renderer according to the settings. The default is native drawing, where the host process draws the
    /// lyrics itself and starts no browser subprocess, leaving a single LyricsBar.exe in Task Manager. The settings can switch
    /// back to the WebView2-hosted page, which keeps its fallback rebuilds and readiness watchdog as an A/B reference and a
    /// safety net if the native path misbehaves.
    /// </summary>
    private async void EnsureRendererAsync()
    {
        if (_closing || _renderer is not null || _rendererFailed)
            return;

        if (SettingsManager.Current.LyricsRendererEngine == LyricsRendererEngine.WebView2)
        {
            await EnsureWebViewRendererAsync();
            return;
        }

        EnsureNativeRenderer();
    }

    /// <summary>
    /// 创建原生渲染器。它是同步的：没有文档要加载、没有引擎要等，构造完就能收消息，
    /// 因此这里不需要就绪看门狗，也不需要"窗口必须保持不透明直到渲染器就绪"那条约束（那条是 WebView2 合成控件的毛病）。
    /// Builds the native renderer. It is synchronous — no document to load, no engine to wait for — so it can receive
    /// messages the moment it is constructed. That means no readiness watchdog and none of the "the window must stay opaque
    /// until the renderer is ready" constraint, which is a WebView2 composition-control quirk.
    /// </summary>
    private void EnsureNativeRenderer()
    {
        var generation = ++_rendererGeneration;
        try
        {
            var renderer = new LyricsNativeRenderer();
            _renderer = renderer;
            renderer.Ready += OnRendererReady;
            renderer.Failed += OnRendererFailed;
            LyricsHost.Children.Add(renderer);
            ApplyRendererStyle();
            _ = renderer.InitializeAsync();
            Log.Info("Lyrics", "已启用原生歌词渲染器（不再起 msedgewebview2 子进程）。");
        }
        catch (Exception ex)
        {
            _rendererFailed = true;
            Log.Error("Lyrics", "创建原生歌词渲染器失败。", ex);
            if (generation == _rendererGeneration)
                ReleaseRenderer();
            SetBarVisible(false);
            _appliedVisible = false;
        }
    }

    private async Task EnsureWebViewRendererAsync()
    {
        var generation = ++_rendererGeneration;
        WebView2CompositionControl? webView = null;
        try
        {
            webView = new WebView2CompositionControl
            {
                MinWidth = 1,
                MinHeight = 1,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false
            };
            var renderer = new LyricsWebViewRenderer(webView, ResolveBrowserArguments());
            _webView = webView;
            _renderer = renderer;
            renderer.Ready += OnRendererReady;
            renderer.Failed += OnRendererFailed;
            LyricsHost.Children.Add(webView);
            ApplyRendererStyle();

            var initialized = await renderer.InitializeAsync();
            if (_closing || generation != _rendererGeneration || !ReferenceEquals(renderer, _renderer))
                return;

            if (!initialized)
            {
                // 初始化失败也走降级：环境创建失败、核心对象为空，都可能只是当前这套启动参数在这台机器上跑不通，
                // 换一级往往就好了。交给 OnRendererFailed 统一判断"还能不能再降"。
                // An initialization failure escalates too: a failed environment creation or a null core can equally mean the
                // current browser arguments do not work on this machine, and the next tier often does. OnRendererFailed owns
                // the "is there another tier left" decision.
                Log.Warn("Lyrics", "WebView2 歌词视图初始化失败，尝试降级重建。");
                OnRendererFailed(renderer, EventArgs.Empty);
                return;
            }

            _ = WatchRendererReadyAsync(renderer, generation);
        }
        catch (Exception ex)
        {
            _rendererFailed = true;
            Log.Error("Lyrics", "创建 WebView2 歌词宿主失败。", ex);
            if (generation == _rendererGeneration)
                webView?.Dispose();
        }
    }

    /// <summary>
    /// 等一小会儿，如果歌词文档还没就绪就当作一次失败，走和崩溃相同的降级路径。
    /// 只覆盖"既没成功也没报错"的第三种状态，见 <see cref="RendererReadyTimeout"/>。
    /// Waits a little and treats a document that still is not ready as a failure, taking the same fallback path as a crash.
    /// This only covers the third state, neither succeeded nor reported an error; see <see cref="RendererReadyTimeout"/>.
    /// </summary>
    private async Task WatchRendererReadyAsync(LyricsWebViewRenderer renderer, int generation)
    {
        try
        {
            await Task.Delay(RendererReadyTimeout);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        // 期间可能已经换过渲染器（降级重建）、关过窗口，或者文档已经加载完成，这些都不需要再插手。
        // In the meantime the renderer may have been replaced by a fallback rebuild, the window may have closed, or the
        // document may have finished loading; none of those need intervention.
        if (_closing || generation != _rendererGeneration || !ReferenceEquals(renderer, _renderer) || renderer.IsReady)
            return;

        // 先按同一级别重试一次，再考虑降级。实测（2026-10-14）最常见的"加载不出来"并不是参数不对，
        // 而是上一个实例的 WebView2 进程还没退干净、仍然占着用户数据目录——此时换级别没用（下一级用的是同一个目录），
        // 等一会儿用同样的参数重来反而能好。重试过一次仍不行，才认为这个级别真的不适用。
        // Retry the same tier once before falling back. Measured 2026-10-14: the usual cause of "never loads" is not wrong
        // arguments but a previous instance's WebView2 processes still holding the user data folder. Escalating does not help
        // there (the next tier uses the same folder), while waiting and retrying with identical arguments does. Only after one
        // retry is the tier treated as genuinely unusable.
        if (!_rendererRetriedCurrentTier)
        {
            _rendererRetriedCurrentTier = true;
            Log.Warn("Lyrics", $"WebView2 歌词文档 {RendererReadyTimeout.TotalSeconds:0} 秒内未加载完成，先按第 {_rendererFallbackTier} 级重建一次。");
            RebuildRenderer();
            return;
        }

        Log.Warn("Lyrics", $"WebView2 歌词文档在第 {_rendererFallbackTier} 级重试后仍未加载完成，按失败处理并降级。");
        OnRendererFailed(renderer, EventArgs.Empty);
    }

    private void OnRendererReady(object? sender, EventArgs e)
    {
        if (_closing || !ReferenceEquals(sender, _renderer))
            return;

        RememberRendererTier();
        _appliedOverlayRegion = null;
        ApplyRendererStyle();
        UpdateFrame();
    }

    /// <summary>
    /// 歌词文档真的加载出来了，说明当前降级级别在这台机器上可用，把它记进设置，下次启动直接从这个级别开始。
    /// 只在级别变化时落盘：这个方法每次重建渲染器都会走一遍，无脑写盘会平白增加磁盘写入。
    /// 原生渲染器没有"降级级别"这回事，所以只在 WebView2 引擎下才记录。
    /// The lyric document actually loaded, so the current tier works on this machine. Persist it so the next start begins at
    /// that tier. Only writes when the tier changed: this runs on every renderer rebuild, and writing unconditionally would add
    /// pointless disk traffic. The native renderer has no fallback tiers, so this only records for the WebView2 engine.
    /// </summary>
    private void RememberRendererTier()
    {
        if (SettingsManager.Current.LyricsRendererEngine != LyricsRendererEngine.WebView2)
            return;

        if (_rendererFallbackTier <= 0 || _rendererFallbackTier == SettingsManager.Current.WebView2RendererTier)
            return;

        SettingsManager.UpdateSilently(s => s.WebView2RendererTier = _rendererFallbackTier);
        Log.Info("Lyrics", $"已记住可用的 WebView2 降级级别 {_rendererFallbackTier}，下次启动不再重复降级过程。");
    }

    private void OnRendererFailed(object? sender, EventArgs e)
    {
        if (_closing || !ReferenceEquals(sender, _renderer))
            return;

        // 原生渲染器没有浏览器可以降级重建，也没有"沙箱起不来"这类环境问题：失败就是失败，
        // 直接停用歌词显示，免得在一条没有出路的路上反复重建。
        // The native renderer has no browser to rebuild with heavier arguments and no "sandbox will not initialise" class of
        // environment failure: a failure is final, so lyrics are disabled outright instead of rebuilding on a path that leads
        // nowhere.
        if (SettingsManager.Current.LyricsRendererEngine != LyricsRendererEngine.WebView2)
        {
            _rendererFailed = true;
            ReleaseRenderer();
            SetBarVisible(false);
            _appliedVisible = false;
            Log.Error("Lyrics", "原生歌词渲染器失败，本次会话停用歌词显示。");
            return;
        }

        // 逐级降级重建。WebView2 子进程崩溃在虚拟机、远程会话与受限企业终端上很常见，且原因分两类：
        // GPU 进程起不来（关 GPU 即可），以及 Chromium 沙箱无法初始化（必须连沙箱一起关）。
        // 两类都实测过：只关 GPU 只能治前者，所以这里逐级加重，而不是一次就放弃歌词。
        // Rebuilds with progressively heavier fallbacks. WebView2 subprocess crashes are common on virtual machines, remote
        // sessions, and locked-down endpoints, and there are two distinct causes: the GPU process cannot start (fixed by
        // disabling the GPU) and the Chromium sandbox cannot initialize (needs the sandbox disabled too). Both were measured;
        // disabling the GPU alone only fixes the first, so the fallback escalates instead of giving up on lyrics outright.
        var ceiling = SettingsManager.Current.WebView2CompatibilityMode ? MaxRendererFallbackTier : _rendererFallbackTier;
        var next = ceiling + 1;
        if (next == 1 && SettingsManager.Current.DisableWebView2Gpu)
            next = 2;

        if (next <= MaxRendererFallbackTier)
        {
            _rendererFallbackTier = next;
            _rendererRetriedCurrentTier = false;
            Log.Warn("Lyrics", $"WebView2 子进程反复崩溃，按第 {_rendererFallbackTier} 级降级重建歌词视图（{ResolveBrowserArguments()}）。");
            RebuildRenderer();
            return;
        }

        _rendererFailed = true;
        ReleaseRenderer();
        SetBarVisible(false);
        _appliedVisible = false;
        Log.Error("Lyrics", "WebView2 歌词视图在所有降级模式下均失败，本次会话停用歌词显示。");
    }

    /// <summary>
    /// 拆掉当前渲染器并按当前级别重建一个。崩溃降级与超时重试都走这里，保证两者行为完全一致。
    /// Tears the current renderer down and builds a fresh one at the current tier. Both crash fallback and timeout retry go
    /// through here so the two behave identically.
    /// </summary>
    private void RebuildRenderer()
    {
        ReleaseRenderer();
        _rendererFailed = false;
        SetBarVisible(false);
        _appliedVisible = false;
        EnsureRendererAsync();
    }

    /// <summary>
    /// 决定传给 WebView2 的额外启动参数。用户显式开启"强制软件渲染"时直接跳到第 1 级；
    /// 否则按本会话已经失败过几次逐级加重。
    /// Decides the extra browser arguments handed to WebView2. Turning on forced software rendering starts at tier 1;
    /// otherwise the tier escalates with the number of failures seen in this session.
    /// </summary>
    private string? ResolveBrowserArguments()
    {
        if (SettingsManager.Current.WebView2CompatibilityMode || _rendererFallbackTier >= MaxRendererFallbackTier)
            return DisableGpuAndNoSandboxArguments;

        return _rendererFallbackTier >= 1 || SettingsManager.Current.DisableWebView2Gpu
            ? DisableGpuArguments
            : null;
    }

    private void ApplyRendererStyle()
    {
        if (_renderer is null)
            return;

        var settings = SettingsManager.Current;
        var style = BuildStyle(settings);
        if (Equals(_appliedStyle, style))
            return;

        _appliedStyle = style;
        _renderer.ApplyStyle(style);

        // 用户改了字体却在任务栏上看不出变化时，必须先能确认样式到底有没有下发、下发的是什么字体名。
        // 这条日志只在样式真的变化时打印（上面有 Equals 短路），不会刷屏。
        // When a user changes the font but sees no change on the taskbar, the first thing to confirm is whether the style was
        // delivered at all and which family name went out. It only prints on a real style change (short-circuited above).
        Log.Info("Lyrics", $"已下发外观: 字体「{style.FontFamily}」，字号 {style.FontSize}，封面 {(style.ShowCover ? "开" : "关")}，对齐 {style.TextAlignment}。");
    }

    /// <summary>
    /// 把当前曲目的封面转给 Web 端。只在封面真的变了的时候发——每帧都发会让 Web 端反复做 base64 解码与
    /// 交叉淡入，白白占着渲染线程。
    /// Forwards the current track's artwork to the web side. It is only sent when the artwork actually changed; sending it every
    /// frame would make the web side re-decode base64 and re-run the crossfade, needlessly occupying the render thread.
    /// </summary>
    private void ApplyCover(MediaSnapshot snapshot)
    {
        if (_renderer is null)
            return;

        var dataUri = snapshot.CoverArt ?? string.Empty;
        var trackId = _frame.TrackId;
        if (string.Equals(dataUri, _appliedCoverUri, StringComparison.Ordinal) &&
            string.Equals(trackId, _appliedCoverTrackId, StringComparison.Ordinal))
            return;

        _appliedCoverUri = dataUri;
        _appliedCoverTrackId = trackId;
        _renderer.SetCover(dataUri, snapshot.Title, CoverFallbackColor(snapshot.Title), trackId);
    }

    /// <summary>
    /// 把曲名与歌手推给 Web 端封面右侧那块独立区域。
    ///
    /// 只在文本或宽度真的变了才发：宽度是在这里量出来的，每帧都发不只是浪费，还会让 Web 端每帧重排一次版面。
    /// 设置关掉这块、或曲名与歌手都为空时宽度为 0，Web 端据此把整块收起来（宽度与间距一起归零），
    /// 而不是留一个空白占位把歌词推远。
    /// Pushes the title and artist to the standalone region right of the artwork on the web side.
    ///
    /// It is sent only when the text or the width actually changed: the width is measured here, so sending it every frame would
    /// not only be wasted work but would make the web side re-lay-out once per frame. The width is zero when the setting turns
    /// the block off or when both title and artist are empty, and the web side collapses the whole block on that (zeroing width
    /// and gap together) rather than holding blank space that pushes the lyrics away.
    /// </summary>
    private void ApplyTrackInfo(AppSettings settings)
    {
        if (_renderer is null)
            return;

        var (title, artist) = LyricsPresentationProjector.ResolveTrackInfoLines(_coordinator.Latest);
        var widthDip = ResolveTrackInfoWidthDip(title, artist, settings);

        if (string.Equals(title, _appliedTrackInfoTitle, StringComparison.Ordinal) &&
            string.Equals(artist, _appliedTrackInfoArtist, StringComparison.Ordinal) &&
            Math.Abs(widthDip - _appliedTrackInfoWidthDip) <= 0.5)
        {
            return;
        }

        _appliedTrackInfoTitle = title;
        _appliedTrackInfoArtist = artist;
        _appliedTrackInfoWidthDip = widthDip;
        _renderer.SetTrackInfo(title, artist, widthDip);
    }

    /// <summary>
    /// 曲目信息块的宽度（DIP）：曲名与歌手两行里更长的那条。用与歌词自动宽度同一套量法量出来，
    /// Web 端只是照用，于是两边不会各自量一遍、量出两个宽度。
    /// 歌手那一行在 Web 端用的是 <c>--next-size = max(9, 主字号 × 0.92)</c>，这里必须用同一个字号去量，
    /// 否则量出来的宽度会比实际排版宽一截。
    /// Width of the track-info block in DIP: whichever of the two rows is longer, measured with the very same routine used for
    /// the lyrics' automatic width, so the web side can simply use it instead of measuring a second, divergent width of its own.
    /// The artist row is rendered on the web side at <c>--next-size = max(9, main size × 0.92)</c>, so it has to be measured at
    /// that same size here; otherwise the reserved width comes out wider than the actual layout.
    /// </summary>
    private double ResolveTrackInfoWidthDip(string title, string artist, AppSettings settings)
    {
        if (!settings.LyricsTrackInfoBlockEnabled)
            return 0;

        if (title.Length == 0 && artist.Length == 0)
            return 0;

        // 用户定过宽度就照用：定宽的目的正是把栏宽钉死，不再随曲名长短伸缩，所以这里不看文字量出来的宽度。
        // 文字排不下时由渲染端按省略号截断（原生端与网页端各自的裁剪都在块内做）。
        // A user-set width wins outright: the point of setting one is to pin the bar's width down instead of letting it stretch
        // with the title's length, so the measured text width is not consulted here. Text that does not fit is trimmed with an
        // ellipsis at the rendering end, inside the block, on both the native and the web side.
        var manualWidthDip = LyricsTrackInfoWidthPolicy.NormalizeManual(settings.LyricsTrackInfoWidthDip);
        if (manualWidthDip > 0)
            return manualWidthDip;

        var artistFontSize = Math.Max(9, settings.LyricsFontSize * 0.92);
        var titleWidth = MeasureTextWidthDip(title, settings, settings.LyricsFontSize);
        var artistWidth = MeasureTextWidthDip(artist, settings, artistFontSize);

        return Math.Min(Math.Max(titleWidth, artistWidth), LyricsTrackInfoWidthPolicy.AutoMaximumDip);
    }

    /// <summary>
    /// 没有封面时占位色块的底色：按曲名算一个固定的色相，让同一首歌每次回来都是同一个颜色。
    /// Placeholder tile colour used when there is no artwork: a stable hue derived from the title, so the same track always
    /// comes back in the same colour.
    /// </summary>
    private static string CoverFallbackColor(string title)
    {
        var hash = 17;
        foreach (var ch in title)
            hash = unchecked(hash * 31 + ch);

        return $"hsl({(uint)hash % 360}, 42%, 36%)";
    }

    private LyricsWebStyle BuildStyle(AppSettings settings)
    {
        var primary = settings.LyricsTextColor;
        var secondaryOpacity = 0.68;
        var unsungOpacity = LyricsUnsungOpacity.Normalize(settings.LyricsUnsungOpacityPercent) / 100d;

        var secondary = WithOpacity(primary, secondaryOpacity);
        var unsung = WithOpacity(primary, unsungOpacity);
        var fontFamily = ResolveFontFamily(settings);

        return new LyricsWebStyle(
            fontFamily,
            fontFamily,
            fontFamily,
            settings.LyricsFontSize,
            // 加粗走字重而不是另加一个字重字段：700 是 OpenType 的 Bold，渲染器与页面都按 >=600 当粗体。
            // Bold rides the weight rather than a separate bold flag: 700 is OpenType's Bold, and both the renderer and the page
            // treat anything >= 600 as bold.
            settings.LyricsFontBold ? 700 : 400,
            LyricsCharacterSpacing.Normalize(settings.LyricsCharacterSpacingPercent),
            LyricsLineGap.Normalize(settings.LyricsLineGapPercent),
            settings.LyricsTextAlignment switch
            {
                LyricsTextAlignment.Left => "Left",
                LyricsTextAlignment.Right => "Right",
                LyricsTextAlignment.Split => "Split",
                _ => "Center"
            },
            primary,
            secondary,
            secondary,
            unsung,
            primary,
            settings.LyricsTextShadowEnabled ? "0 0 3px rgba(0, 0, 0, 0.85)" : "none",
            settings.LyricsCoverArtEnabled,
            ResolveCoverSizeDip(),
            settings.LyricsFontItalic);
    }

    private static string ResolveFontFamily(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.FontFamily)
            ? SystemFonts.MessageFontFamily.Source
            : settings.FontFamily!;

    /// <summary>
    /// 给一个 <c>#AARRGGBB</c> 或 <c>#RRGGBB</c> 颜色叠加不透明度，产出 CSS 的 <c>rgba(...)</c>。
    /// 无法解析时退回白色，绝不返回空串——空颜色会让整行歌词不可见。
    /// Multiplies an <c>#AARRGGBB</c> or <c>#RRGGBB</c> colour by an opacity and produces a CSS <c>rgba(...)</c>. An unparsable
    /// value falls back to white and never to an empty string, because an empty colour makes the whole line invisible.
    /// </summary>
    private static string WithOpacity(string hex, double opacity)
    {
        var text = (hex ?? string.Empty).Trim().TrimStart('#');
        if (text.Length == 6)
            text = "FF" + text;
        if (text.Length != 8 || !uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
            return $"rgba(255, 255, 255, {opacity:0.###})";

        var a = (byte)((value >> 24) & 0xFF);
        var r = (byte)((value >> 16) & 0xFF);
        var g = (byte)((value >> 8) & 0xFF);
        var b = (byte)(value & 0xFF);
        var alpha = Math.Clamp(a / 255d * opacity, 0, 1);
        return $"rgba({r}, {g}, {b}, {alpha:0.###})";
    }

    private void ReleaseRenderer()
    {
        _rendererGeneration++;
        var renderer = _renderer;
        _renderer = null;
        if (renderer is not null)
        {
            renderer.Ready -= OnRendererReady;
            renderer.Failed -= OnRendererFailed;
        }

        // 清掉"已应用"的记录：换渲染器或降级重建会得到一个全新的空视图，
        // 若留着旧记录，外观与封面会因为"值没变"而被跳过，重建后的歌词栏就既没有样式也没有封面。
        // Clears the applied-state records: swapping renderers or a fallback rebuild produces a brand-new blank view, and
        // keeping the old records would make appearance and artwork be skipped as "unchanged", leaving the rebuilt bar with
        // neither.
        _appliedStyle = null;
        _appliedCoverUri = string.Empty;
        _appliedCoverTrackId = string.Empty;
        _appliedTrackInfoTitle = string.Empty;
        _appliedTrackInfoArtist = string.Empty;
        _appliedTrackInfoWidthDip = -1;
        _appliedOverlayRegion = null;
        _appliedInfoHintWidthDip = -1;
        // 按钮组的边距依赖曲目信息块宽度，宽度记录一清就得跟着重算，否则重建后会停在旧位置的居中点上。
        _appliedTransportLeftDip = -1;

        LyricsHost.Children.Clear();
        _webView = null;
        try { renderer?.Dispose(); }
        catch (Exception ex)
        {
            Log.Warn("Lyrics", $"释放歌词渲染器时出错: {ex.Message}");
        }
    }

    /// <summary>窗口内容准备好之后创建 WebView2。放在这里而不是构造里，是因为合成控件必须先有可视树。</summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        EnsureRendererAsync();
    }
}
