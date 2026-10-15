using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LyricsBar.Classes.Utils;

namespace LyricsBar.Classes.Services.Lyrics;

/// <summary>
/// 原生 WPF 歌词渲染器。它替代 WebView2 承载的那一整套 HTML/CSS/JS，把内存从"宿主 + 六个 Chromium 子进程"
/// 压到"只有宿主"，观感与功能逐项对齐 <c>Web/Lyrics/app.js</c> 与 <c>Web/Lyrics/style.css</c>。
/// The native WPF lyrics renderer. It replaces the whole HTML/CSS/JS stack WebView2 hosted and cuts memory from
/// "host plus six Chromium subprocesses" down to the host alone, matching Web/Lyrics/app.js and Web/Lyrics/style.css item
/// by item.
///
/// 实现要点：
/// * 歌词文字由 <see cref="LyricsCanvas"/> 一次自绘（<c>OnRender</c>）。逐字擦亮的渐变擦除、超长行的水平滚动、
///   换行上滚的整体位移因此都在同一个坐标系里算，也不必为每一行搭一棵可视树。
/// * 动画（上滚、逐字平滑）由 <c>CompositionTarget.Rendering</c> 驱动，没有动画时立刻退订；闲置时 CPU 占用为 0。
/// * 封面与曲目信息块是歌词区之外的独立元素，所以悬停磨砂（<see cref="BlurEffect"/>）只糊歌词那两块，
///   封面与曲目信息保持清晰——网页版要靠 <c>filter: blur()</c> 才能做到的事，换成原生反而更直接。
///
/// Key points:
/// * Lyric text is drawn in one pass by <see cref="LyricsCanvas"/> (OnRender), so the syllable scan's gradient wipe, the
///   horizontal scroll of overlong lines, and the whole-track offset of a line roll share one coordinate space, with no
///   per-row visual tree.
/// * Animation (the roll, syllable smoothing) rides <c>CompositionTarget.Rendering</c> and the subscription is dropped the
///   moment nothing animates, so an idle bar costs no CPU.
/// * The artwork and the track-info block live outside the self-drawn lyrics, so the hover frost (a
///   <see cref="BlurEffect"/>) blurs exactly the two lyric rows while artwork and track info stay sharp — what the web
///   version needed <c>filter: blur()</c> for, only more directly.
///
/// 明确不做的一件事：网页版里还有一套"频谱场景"（无歌词时 21 根柱子律动）。宿主的
/// <c>LyricsWebViewRenderer.Present</c> 恒发 <c>scene = "lyrics"</c>、<c>isPureMusic = false</c>，
/// 那套代码在这个精简版里从来没有被走到过，所以原生实现也不带它。
/// One thing deliberately left out: the web version still carries a spectrum scene (21 bars pulsing when there are no
/// lyrics). The host's LyricsWebViewRenderer.Present always sends scene = "lyrics" and isPureMusic = false, so that code
/// has never been reachable in this trimmed build and the native renderer does not carry it either.
/// </summary>
public sealed class LyricsNativeRenderer : Canvas, ILyricsRenderer
{
    // ============================================================================================
    // 常量：逐条对应 Web/Lyrics/app.js 与 style.css。改这里之前先去看那边同名的那一行。
    // Constants, one for one with app.js and style.css. Check the matching line there before editing.
    // ============================================================================================

    /// <summary><c>--layout-padding-inline</c>：整条栏左右各留的内边距（DIP）。</summary>
    private const double LayoutPaddingInlineDip = 4;

    /// <summary><c>--lyrics-pane-padding-*</c>：歌词框自己的内边距（DIP）。</summary>
    private const double PanePaddingTopDip = 3;
    private const double PanePaddingRightDip = 4;
    private const double PanePaddingLeftDip = 2;

    /// <summary><c>--cover-gap</c>：封面与右边内容之间的固定间距（DIP）。</summary>
    private const double CoverGapDip = 8;

    /// <summary>与宿主 <c>TrackInfoGapDip</c> 一致的曲目信息块间距（DIP）。两边不同，栏宽就会差一个间距。</summary>
    private const double TrackInfoGapDip = 8;

    /// <summary><c>--primary-offset-y</c> / <c>--secondary-offset-y</c>：两行各自向下微移（DIP）。</summary>
    private const double PrimaryOffsetYDip = 1;
    private const double SecondaryOffsetYDip = 2;

    /// <summary><c>viewportDescenderBufferPx</c>：给下行字母留的余量（DIP）。</summary>
    private const double DescenderBufferDip = 2;

    /// <summary><c>--line-text-padding-bottom</c>：行文本自身底部留白（DIP）。</summary>
    private const double LineTextPaddingBottomDip = 2;

    /// <summary><c>currentLineRestOpacity</c> / <c>leavingLineOpacity</c>。</summary>
    private const double CurrentLineRestOpacity = 0.98;
    private const double LeavingLineOpacity = 0.16;

    /// <summary><c>secondaryOpacity</c> 的静止基准与随行内进度的微调区间（<c>updateSecondaryOpacity</c>）。</summary>
    private const double SecondaryBaseOpacity = 0.72;
    private const double SecondaryOpacityFloor = 0.58;
    private const double SecondaryOpacitySwing = 0.16;
    private const double SecondaryOpacitySmoothing = 0.28;

    /// <summary>离开行收尾时的缩放（<c>.current-line.leaving { transform: scale(0.972) }</c>）。</summary>
    private const double LeavingLineScale = 0.972;

    /// <summary><c>--cover-radius</c> / <c>--cover-fallback-font-size</c>。</summary>
    private const double CoverCornerRadiusDip = 6;
    private const double CoverFallbackFontSizeDip = 13;

    /// <summary><c>--word-scan-feather-width: 0.12em</c>：擦亮头部的软边宽度（em）。</summary>
    private const double WordScanFeatherEm = 0.12;

    /// <summary><c>horizontalScrollAnchorRatio</c>：水平滚动时扫描头停在可视宽度的 65% 处。</summary>
    private const double HorizontalScrollAnchorRatio = 0.65;

    /// <summary>字号夹取的下限与第二行字号比例（<c>updateMetrics</c>）。</summary>
    private const double MinimumFontSizeDip = 11.2;
    private const double NextFontSizeRatio = 0.92;
    private const double MinimumNextFontSizeDip = 9;

    /// <summary>封面色块的底色（<c>.cover { background: #1f2937 }</c>）。</summary>
    private static readonly Color CoverBaseColor = Color.FromRgb(0x1F, 0x29, 0x37);

    /// <summary>悬停白膜的底色（<c>.overlay-backdrop { background: rgba(255,255,255,0.12) }</c>）。</summary>
    private static readonly Color OverlayFilmColor = Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF);

    /// <summary>悬停时歌词内容的模糊半径（<c>filter: blur(7px)</c>）。</summary>
    private const double OverlayBlurRadius = 7;

    /// <summary>译文行的默认不透明度（<c>.translation-line { opacity: 1 }</c>）。</summary>
    private const double TranslationLineOpacity = 1.0;

    private static readonly TimeSpan RollDuration = TimeSpan.FromMilliseconds(560);
    private static readonly TimeSpan PairRollDuration = TimeSpan.FromMilliseconds(760);
    private static readonly TimeSpan WordScanSmoothingDuration = TimeSpan.FromMilliseconds(90);
    private static readonly TimeSpan CoverFadeDuration = TimeSpan.FromMilliseconds(440);
    private static readonly TimeSpan OverlayFadeDuration = TimeSpan.FromMilliseconds(140);

    /// <summary>逐字擦亮的平滑只在"向前小步推进"时生效；倒退或大跳（拖进度条、换行）直接落位。</summary>
    private const double ScanSmoothingMaxJump = 0.35;
    private const double ScanSmoothingBackwardTolerance = 0.02;

    // ============================================================================================
    // 可视树
    // ============================================================================================

    private readonly Grid _coverHost = new() { ClipToBounds = true };
    private readonly Border _coverBase = new() { Background = new SolidColorBrush(CoverBaseColor) };
    private readonly Border _coverFallback = new();
    private readonly TextBlock _coverFallbackText = new()
    {
        FontSize = CoverFallbackFontSizeDip,
        FontWeight = FontWeights.Bold,
        Foreground = new SolidColorBrush(Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF)),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly Image _coverImageA = new() { Stretch = Stretch.UniformToFill, Opacity = 0 };
    private readonly Image _coverImageB = new() { Stretch = Stretch.UniformToFill, Opacity = 0 };

    private readonly Grid _trackInfoPanel = new();
    private readonly StackPanel _trackInfoStack = new()
    {
        Orientation = Orientation.Vertical,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly TextBlock _trackInfoTitle = new() { TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _trackInfoArtist = new() { TextTrimming = TextTrimming.CharacterEllipsis };

    private readonly Grid _lyricsPane = new() { ClipToBounds = true };
    private readonly Grid _blurHost = new();
    private readonly LyricsCanvas _lyricsCanvas;
    private readonly Border _overlayFilm = new()
    {
        Background = new SolidColorBrush(OverlayFilmColor),
        Opacity = 0
    };

    private readonly BlurEffect _overlayBlur = new()
    {
        Radius = 0,
        KernelType = KernelType.Gaussian,
        RenderingBias = RenderingBias.Performance
    };

    private readonly DropShadowEffect _textShadow = new()
    {
        Color = Colors.Black,
        BlurRadius = 3,
        ShadowDepth = 0,
        Direction = 0,
        Opacity = 0.85,
        RenderingBias = RenderingBias.Performance
    };

    // ============================================================================================
    // 状态
    // ============================================================================================

    private readonly Row[] _rows = [new Row(), new Row(), new Row(), new Row()];
    private readonly List<Tween> _tweens = [];
    private readonly DispatcherTimer _animationWatchdog;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    private LyricsWebStyle? _style;
    private string _textAlignment = "Center";
    private double _fontSize = 14;
    private double _characterSpacingPercent;
    private double _lineGapPercent;
    private Typeface _typeface = new("Segoe UI");

    private Brush _primaryBrush = Brushes.White;
    private Brush _secondaryBrush = Brushes.White;
    private Brush _translationBrush = Brushes.White;
    private Brush _wordScanBaseBrush = Brushes.White;
    private Brush _wordScanOverlayBrush = Brushes.White;

    private string _trackInfoTitleText = string.Empty;
    private string _trackInfoArtistText = string.Empty;
    private double _trackInfoWidthDip;

    private string _coverDataUri = string.Empty;
    private bool _coverImageOnA = true;
    private double _coverOpacityA;
    private double _coverOpacityB;
    private double _coverFallbackOpacity = 1;
    private double _coverSizeDip = 34;

    private double _overlayFilmOpacity;
    private double _overlayBlurRadius;

    // 布局度量（尺寸或外观变化时重算）
    private double _textAreaWidth;
    private double _textAreaHeight;
    private double _rowHeight;
    private double _rowGap;
    private double _linePitch;
    private double _currentFontSize = 13;
    private double _nextFontSize = 12;

    // 呈现状态
    private double _trackOffset;
    private double _secondaryOpacity = SecondaryBaseOpacity;
    private LyricsPresentationFrame _lastFrame = LyricsPresentationFrame.Hidden;
    private LyricsPresentationFrame? _pendingFrame;
    private bool _hasContent;
    private bool _deliveryPaused;
    private bool _disposed;
    private bool _renderLoopHooked;
    private bool _layoutDirty = true;
    private bool _failureReported;
    private double _lastTickMs;
    private double _lastRenderingMs;
    private Roll? _roll;

    /// <summary>创建原生歌词渲染器。/ Creates the native lyrics renderer.</summary>
    public LyricsNativeRenderer()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent;

        _lyricsCanvas = new LyricsCanvas(this);

        _coverFallback.Child = _coverFallbackText;
        _coverHost.Children.Add(_coverBase);
        _coverHost.Children.Add(_coverFallback);
        _coverHost.Children.Add(_coverImageA);
        _coverHost.Children.Add(_coverImageB);

        _trackInfoStack.Children.Add(_trackInfoTitle);
        _trackInfoStack.Children.Add(_trackInfoArtist);
        _trackInfoPanel.Children.Add(_trackInfoStack);

        _blurHost.Children.Add(_lyricsCanvas);
        _lyricsPane.Children.Add(_blurHost);
        _lyricsPane.Children.Add(_overlayFilm);

        Children.Add(_coverHost);
        Children.Add(_trackInfoPanel);
        Children.Add(_lyricsPane);

        // 外观到达之前先把封面与曲目信息藏起来：它们的尺寸来自外观（封面边长）与宿主量出的宽度，
        // 还没拿到值时若按内容自适应，左上角会先冒出一小块色块。
        // Artwork and track info start hidden until the appearance arrives: their sizes come from the appearance (cover edge)
        // and from the width the host measured, and sizing to content before that would flash a small tile in the corner.
        _coverHost.Visibility = Visibility.Collapsed;
        _trackInfoPanel.Visibility = Visibility.Collapsed;

        _lyricsCanvas.Effect = _textShadow;

        SizeChanged += (_, _) => MarkLayoutDirty();
        Loaded += (_, _) =>
        {
            MarkLayoutDirty();
            Ready?.Invoke(this, EventArgs.Empty);
        };

        // 窗口被隐藏时 CompositionTarget.Rendering 会停发，上滚就会卡在半途。这条定时器只在
        // "确实有动画在跑、而渲染回调已经 50ms 没有来" 的时候兜底，平时一次都不做。
        // CompositionTarget.Rendering stops firing while the window is hidden, which would freeze a roll halfway. This timer
        // only steps in when something is genuinely animating and the render callback has been silent for 50 ms; otherwise it
        // does nothing at all.
        _animationWatchdog = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _animationWatchdog.Tick += (_, _) => StepFromWatchdog();
    }

    // ============================================================================================
    // ILyricsRenderer
    // ============================================================================================

    /// <inheritdoc />
    public event EventHandler? Ready;

    /// <inheritdoc />
    public event EventHandler? Failed;

    /// <inheritdoc />
    public bool IsReady => !_disposed;

    /// <inheritdoc />
    public Task<bool> InitializeAsync() => Task.FromResult(!_disposed);

    /// <inheritdoc />
    public void ApplyStyle(LyricsWebStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (_disposed || Equals(_style, style))
            return;

        _style = style;
        _fontSize = style.FontSize;
        _characterSpacingPercent = style.CharacterSpacingPercent;
        _lineGapPercent = style.LineGapPercent;
        _textAlignment = string.IsNullOrWhiteSpace(style.TextAlignment) ? "Center" : style.TextAlignment;
        _coverSizeDip = style.CoverSizeDip > 0 ? style.CoverSizeDip : 34;
        _typeface = ResolveTypeface(style.FontFamily);

        _primaryBrush = CreateBrush(style.PrimaryColor, Colors.White);
        _secondaryBrush = CreateBrush(style.SecondaryColor, Colors.White);
        _translationBrush = CreateBrush(style.TranslationColor, Colors.White);
        _wordScanBaseBrush = CreateBrush(style.WordScanBaseColor, Colors.White);
        _wordScanOverlayBrush = CreateBrush(style.WordScanOverlayColor, Colors.White);

        ApplyTextShadow(style.TextShadow);

        // 外观里也带着封面开关：收起时要把封面从布局里摘掉，否则左边会空出一条。
        // The appearance also carries the artwork switch: when it is off the block has to leave the layout, otherwise an
        // empty lane stays on the left.
        _coverHost.Visibility = style.ShowCover ? Visibility.Visible : Visibility.Collapsed;

        foreach (var row in _rows)
            row.InvalidateRuns();

        MarkLayoutDirty();
    }

    /// <inheritdoc />
    public void SetCover(string dataUri, string fallbackText, string fallbackColor, string trackId)
    {
        if (_disposed)
            return;

        var uri = dataUri ?? string.Empty;
        var text = fallbackText ?? string.Empty;

        _coverFallbackText.Text = text.Length > 0 ? text[..1] : string.Empty;
        _coverFallback.Background = CreateBrush(fallbackColor, Colors.Transparent);

        if (string.Equals(uri, _coverDataUri, StringComparison.Ordinal))
            return;

        _coverDataUri = uri;
        var bitmap = uri.Length > 0 ? TryDecodeDataUri(uri) : null;
        FadeCoverImages(bitmap);
        AnimateOpacity(_coverFallback, ref _coverFallbackOpacity, bitmap is null ? 1 : 0, CoverFadeDuration);
    }

    /// <inheritdoc />
    public void SetTrackInfo(string title, string artist, double widthDip)
    {
        if (_disposed)
            return;

        var normalizedTitle = title ?? string.Empty;
        var normalizedArtist = artist ?? string.Empty;
        var width = double.IsFinite(widthDip) && widthDip > 0 ? widthDip : 0;

        if (string.Equals(normalizedTitle, _trackInfoTitleText, StringComparison.Ordinal) &&
            string.Equals(normalizedArtist, _trackInfoArtistText, StringComparison.Ordinal) &&
            Math.Abs(width - _trackInfoWidthDip) <= 0.5)
        {
            return;
        }

        _trackInfoTitleText = normalizedTitle;
        _trackInfoArtistText = normalizedArtist;
        _trackInfoWidthDip = width;

        _trackInfoTitle.Text = normalizedTitle;
        _trackInfoArtist.Text = normalizedArtist;
        // 歌手为空时整行不占高度，曲名才能像单行歌词那样垂直居中（对应 .track-info-artist:empty）。
        // An empty artist takes no height so a title-only block centres vertically, matching .track-info-artist:empty.
        _trackInfoArtist.Visibility = normalizedArtist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        MarkLayoutDirty();
    }

    /// <inheritdoc />
    public void SetOverlay(bool visible)
    {
        if (_disposed)
            return;

        AnimateOpacity(_overlayFilm, ref _overlayFilmOpacity, visible ? 1 : 0, OverlayFadeDuration);
        AnimateBlurRadius(visible ? OverlayBlurRadius : 0, OverlayFadeDuration);

        if (visible)
        {
            _blurHost.Effect = _overlayBlur;
            return;
        }

        // 模糊半径回到 0 之后把效果摘掉：一直挂着它会让歌词内容每帧都过一遍着色器，文字也会因为多一次栅格化而发糊。
        // The effect is detached once the radius reaches zero: leaving it attached would push the lyric content through a
        // shader every frame and soften the text with an extra rasterisation pass.
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = OverlayFadeDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_overlayBlurRadius <= 0.01)
                _blurHost.Effect = null;
        };
        timer.Start();
    }

    /// <inheritdoc />
    public void Present(LyricsPresentationFrame frame, bool animateTransition)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_disposed)
            return;

        if (_deliveryPaused)
        {
            _pendingFrame = frame;
            return;
        }

        PresentCore(frame, animateTransition);
    }

    /// <inheritdoc />
    public void PauseDelivery() => _deliveryPaused = true;

    /// <inheritdoc />
    public void ResumeDelivery()
    {
        if (_disposed)
            return;

        _deliveryPaused = false;
        var pending = _pendingFrame;
        _pendingFrame = null;
        if (pending is { } frame)
            PresentCore(frame, animateTransition: false);
    }

    /// <summary>原生渲染没有可挂起的引擎，直接返回 false。/ Native rendering has no engine to suspend; always false.</summary>
    public Task<bool> TrySuspendAsync() => Task.FromResult(false);

    /// <summary>原生渲染没有挂起状态，无需恢复。/ Native rendering has no suspended state to resume.</summary>
    public void Resume()
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        ReleaseRenderLoop();
        _animationWatchdog.Stop();
        _tweens.Clear();
        _pendingFrame = null;
        Ready = null;
        Failed = null;
        Children.Clear();
    }

    // ============================================================================================
    // 帧处理
    // ============================================================================================

    private void PresentCore(LyricsPresentationFrame frame, bool animateTransition)
    {
        if (!frame.IsVisible)
        {
            // 不可见帧把两行清空，不留上一首的残影（与 WebView2 那边"不可见帧推空文本"的约定一致）。
            // An invisible frame clears both rows instead of leaving the previous track's remains, matching the "invisible
            // frames push empty text" convention of the WebView2 path.
            _tweens.Clear();
            ReleaseRenderLoop();
            _roll = null;
            _trackOffset = 0;
            _secondaryOpacity = SecondaryBaseOpacity;
            _pendingFrame = null;
            _hasContent = false;
            _lastFrame = frame;
            foreach (var row in _rows)
                row.Reset();
            _lyricsCanvas.InvalidateVisual();
            return;
        }

        if (_roll is not null)
        {
            // 上滚途中来的新帧只更新"升格行"的逐字进度，其余排队到收尾后再落地——
            // 与网页版 presentationCoordinator.queueLatest 的行为一致。
            // A frame arriving mid-roll only refreshes the promoted row's syllable progress; the rest is queued until the
            // roll finishes, matching presentationCoordinator.queueLatest on the web side.
            if (frame.CurrentLineIndex == _roll.PromotedLineIndex)
            {
                _rows[_roll.PromotedRowIndex].SetScanTarget(frame.WordScanProgress);
                EnsureRenderLoop();
            }

            _pendingFrame = frame;
            return;
        }

        var sameLine = _hasContent &&
            _lastFrame.CurrentLineIndex == frame.CurrentLineIndex &&
            string.Equals(_lastFrame.TrackId, frame.TrackId, StringComparison.Ordinal);

        if (!_hasContent || !animateTransition || sameLine)
            ApplyInPlace(frame);
        else
            StartRoll(frame);

        _hasContent = true;
        _lastFrame = frame;
    }

    /// <summary>
    /// 不带动效地把一帧落到画面上：同曲同行只换文本、或歌词栏重新出现时走这里。
    /// Lands a frame without animation: a text swap on the same line of the same track, or the bar reappearing.
    /// </summary>
    private void ApplyInPlace(LyricsPresentationFrame frame)
    {
        _trackOffset = 0;
        _secondaryOpacity = SecondaryBaseOpacity;

        var primaryAlign = PrimaryAlignment();
        var secondaryAlign = SecondaryAlignment();

        _rows[0].Configure(
            frame.Current,
            _currentFontSize,
            _primaryBrush,
            CurrentLineRestOpacity,
            primaryAlign,
            PrimaryOffsetYDip);
        _rows[0].SetScanTarget(frame.WordScanProgress);

        if (frame.TranslationMode)
        {
            _rows[1].Configure(
                frame.CurrentTranslation,
                _nextFontSize,
                _translationBrush,
                TranslationLineOpacity,
                secondaryAlign,
                SecondaryOffsetYDip);
        }
        else
        {
            _rows[1].Configure(
                frame.Next,
                _nextFontSize,
                _secondaryBrush,
                SecondaryOpacityFor(frame.LineProgress),
                secondaryAlign,
                SecondaryOffsetYDip);
        }

        _rows[1].SetScanTarget(null);
        _rows[2].Reset();
        _rows[3].Reset();

        if (frame.WordScanProgress is not null)
            EnsureRenderLoop();

        _lyricsCanvas.InvalidateVisual();
    }

    /// <summary>
    /// 起一次换行上滚：单行模式滚一行（560ms），译文对模式滚两行（760ms）——数值与网页版一致。
    /// Starts a line roll: one row in single-line mode (560 ms) and two rows in translation-pair mode (760 ms), the numbers
    /// the web side uses.
    /// </summary>
    private void StartRoll(LyricsPresentationFrame frame)
    {
        var pair = frame.TranslationMode;
        var pitch = pair ? _linePitch * 2 : _linePitch;
        var duration = pair ? PairRollDuration : RollDuration;

        var primaryAlign = PrimaryAlignment();
        var secondaryAlign = SecondaryAlignment();

        if (pair)
        {
            // 译文对：新的一组（主行 + 译文）从下面滚进来，旧的一组整体滚出上方。
            // 网页版这一路只动整轨位移、不做透明度动画，这里照做。
            // Translation pair: the incoming group (line plus translation) rolls in from below while the outgoing group rolls
            // out through the top. The web version animates only the track offset here, and so does this.
            _rows[2].Configure(
                frame.Current,
                _currentFontSize,
                _primaryBrush,
                CurrentLineRestOpacity,
                primaryAlign,
                PrimaryOffsetYDip);
            _rows[2].SetScanTarget(frame.WordScanProgress);
            _rows[3].Configure(
                frame.CurrentTranslation,
                _nextFontSize,
                _translationBrush,
                TranslationLineOpacity,
                secondaryAlign,
                SecondaryOffsetYDip);
            _rows[3].SetScanTarget(null);
        }
        else
        {
            // 单行：升格行占第二行的位置（它就是即将成为主行的那一句），新的一句接在第三行。
            // 升格行从第二行的字号放大到主行字号，同时淡入到当前行的不透明度。
            // Single line: the promoted line takes the second row's slot — it is the one about to become the main row — and the
            // upcoming line takes the third. The promoted line scales up from the second row's font size and fades in to the
            // main row's opacity.
            _rows[1].Configure(
                frame.Current,
                _currentFontSize,
                _primaryBrush,
                _secondaryOpacity,
                primaryAlign,
                PrimaryOffsetYDip);
            _rows[1].Scale = _currentFontSize > 0 ? _nextFontSize / _currentFontSize : 1;
            _rows[1].SetScanTarget(frame.WordScanProgress);

            _rows[2].Configure(
                frame.Next,
                _nextFontSize,
                _secondaryBrush,
                SecondaryBaseOpacity,
                secondaryAlign,
                SecondaryOffsetYDip);
            _rows[2].SetScanTarget(null);
        }

        _roll = new Roll
        {
            PromotedLineIndex = frame.CurrentLineIndex,
            PromotedRowIndex = pair ? 2 : 1
        };

        AddTween(
            _trackOffset,
            -pitch,
            duration,
            value =>
            {
                _trackOffset = SnapToPhysicalPixel(value);
                _lyricsCanvas.InvalidateVisual();
            },
            () =>
            {
                _roll = null;
                _trackOffset = 0;
                ApplyInPlace(frame);

                var queued = _pendingFrame;
                _pendingFrame = null;
                if (queued is { } next && !ReferenceEquals(next, frame))
                    Dispatcher.BeginInvoke(new Action(() => PresentCore(next, animateTransition: true)));
            });

        if (!pair)
        {
            // 离开行淡到 0.16 并轻微缩小；升格行淡入到当前行的不透明度、放大到 1。
            // The leaving row fades to 0.16 and shrinks slightly; the promoted row fades up to the main row's opacity and
            // scales to 1.
            AddTween(_rows[0].Opacity, LeavingLineOpacity, duration,
                value => { _rows[0].Opacity = value; _lyricsCanvas.InvalidateVisual(); });
            AddTween(_rows[0].Scale, LeavingLineScale, duration,
                value => { _rows[0].Scale = value; _lyricsCanvas.InvalidateVisual(); });
            AddTween(_rows[1].Opacity, CurrentLineRestOpacity, duration,
                value => { _rows[1].Opacity = value; _lyricsCanvas.InvalidateVisual(); });
            AddTween(_rows[1].Scale, 1, duration,
                value => { _rows[1].Scale = value; _lyricsCanvas.InvalidateVisual(); });
        }

        EnsureRenderLoop();
    }

    /// <summary>次要行不透明度随行内进度微调（<c>updateSecondaryOpacity</c>）。/ Nudges the secondary row's opacity with the line progress.</summary>
    private double SecondaryOpacityFor(double progress)
    {
        var p = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        var target = SecondaryOpacityFloor + ((1 - p) * SecondaryOpacitySwing);
        _secondaryOpacity += (target - _secondaryOpacity) * SecondaryOpacitySmoothing;
        return _secondaryOpacity;
    }

    private HorizontalAlign PrimaryAlignment() => _textAlignment switch
    {
        "Right" => HorizontalAlign.Right,
        "Center" => HorizontalAlign.Center,
        _ => HorizontalAlign.Left
    };

    /// <summary>
    /// 次要行的对齐。左右分离时主行靠左、第二行靠右（<c>.layout[data-text-alignment="Split"] .next-line</c>）。
    /// Alignment of the secondary row. In split mode the main row hugs the left and the second row the right.
    /// </summary>
    private HorizontalAlign SecondaryAlignment() => _textAlignment switch
    {
        "Right" => HorizontalAlign.Right,
        "Center" => HorizontalAlign.Center,
        "Split" => HorizontalAlign.Right,
        _ => HorizontalAlign.Left
    };

    // ============================================================================================
    // 布局
    // ============================================================================================

    private void MarkLayoutDirty()
    {
        _layoutDirty = true;
        RecomputeLayout();
    }

    /// <summary>
    /// 重算行高、字号与栏内各块的落点。公式与 <c>spacing.js: resolveRowMetrics</c> 及 <c>app.js: updateMetrics</c>
    /// 逐位对应，连"零行距时取整余量"这种细节都照抄——不照抄的话默认观感会差一两个像素。
    /// Recomputes row heights, font sizes, and where each block inside the bar lands. The formulas mirror
    /// spacing.js:resolveRowMetrics and app.js:updateMetrics bit for bit, including details such as the rounding remainder at
    /// zero line gap; skipping them would shift the default look by a pixel or two.
    /// </summary>
    private void RecomputeLayout()
    {
        if (_disposed)
            return;

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        var showCover = _style?.ShowCover == true;
        var coverFootprint = showCover ? _coverSizeDip + CoverGapDip : 0;
        var trackInfoFootprint = _trackInfoWidthDip > 0 ? _trackInfoWidthDip + TrackInfoGapDip : 0;

        var paneLeft = LayoutPaddingInlineDip + coverFootprint + trackInfoFootprint;
        var paneWidth = Math.Max(0, width - paneLeft - LayoutPaddingInlineDip);
        var paneHeight = Math.Max(0, height);

        _textAreaWidth = Math.Max(0, paneWidth - PanePaddingLeftDip - PanePaddingRightDip);
        _textAreaHeight = Math.Max(0, paneHeight - PanePaddingTopDip);

        // 封面：贴左、垂直居中；圆角靠裁剪（WPF 的 Border 圆角只作用于自身背景，会把图切掉）。
        // Artwork: left-aligned and vertically centred, with the corner radius done by clipping because a WPF Border radius
        // only rounds its own background and would cut the image off.
        if (showCover)
        {
            SetLeft(_coverHost, LayoutPaddingInlineDip);
            SetTop(_coverHost, Math.Max(0, (height - _coverSizeDip) / 2));
            _coverHost.Width = _coverSizeDip;
            _coverHost.Height = _coverSizeDip;
            _coverHost.Clip = new RectangleGeometry(
                new Rect(0, 0, _coverSizeDip, _coverSizeDip),
                CoverCornerRadiusDip,
                CoverCornerRadiusDip);
        }

        // 曲目信息块：紧接封面右侧，宽度由宿主体量好下发。
        // Track-info block: immediately right of the artwork, at the width the host measured and handed down.
        if (_trackInfoWidthDip > 0)
        {
            _trackInfoPanel.Visibility = Visibility.Visible;
            SetLeft(_trackInfoPanel, LayoutPaddingInlineDip + coverFootprint);
            SetTop(_trackInfoPanel, 0);
            _trackInfoPanel.Width = _trackInfoWidthDip;
            _trackInfoPanel.Height = height;
            _trackInfoTitle.MaxWidth = _trackInfoWidthDip;
            _trackInfoArtist.MaxWidth = _trackInfoWidthDip;
        }
        else
        {
            _trackInfoPanel.Visibility = Visibility.Collapsed;
        }

        SetLeft(_lyricsPane, paneLeft);
        SetTop(_lyricsPane, 0);
        _lyricsPane.Width = paneWidth;
        _lyricsPane.Height = paneHeight;

        _blurHost.Margin = new Thickness(PanePaddingLeftDip, PanePaddingTopDip, PanePaddingRightDip, 0);

        ComputeRowMetrics();
        ApplyTrackInfoFonts();

        foreach (var row in _rows)
            row.AvailableWidth = _textAreaWidth;

        _layoutDirty = false;

        // 行高与字号刚被重算，行的字号也必须跟着换，否则改字号或调整窗口大小之后文本会停在旧字号上。
        // 上滚途中不重新落帧，那会把整条轨道的位移清掉、动画当场断掉。
        // The row heights and font sizes were just recomputed, so the rows have to pick up the new sizes too; otherwise the
        // text keeps the old size after a font-size change or a resize. A roll in flight is left alone, because re-landing the
        // frame would zero the track offset and cut the animation short.
        if (_hasContent && _roll is null)
            ApplyInPlace(_lastFrame);

        _lyricsCanvas.InvalidateVisual();
    }

    /// <summary>
    /// 行高 / 行距 / 字号，与 <c>spacing.js</c> 的 <c>resolveRowMetrics</c> 及 <c>app.js</c> 的 <c>updateMetrics</c> 同源。
    /// Row height, line gap, and font sizes, ported from spacing.js:resolveRowMetrics and app.js:updateMetrics.
    /// </summary>
    private void ComputeRowMetrics()
    {
        var host = Math.Max(2, _textAreaHeight - DescenderBufferDip);

        // 先按"没有额外行距"的基准算出字号与字号夹取下限，再让 resolveRowMetrics 求行高/间隙，最后才夹字号。
        // The base font size and the font-clamp floor come first, from the no-extra-gap layout; resolveRowMetrics then derives
        // row heights and the gap, and only afterwards is the font size clamped.
        var baseRowHeight = Math.Max(1, Math.Floor(host / 2));
        var baseSizeMax = Math.Max(MinimumFontSizeDip, baseRowHeight * NextFontSizeRatio);
        var baseSize = Math.Min(_fontSize, baseSizeMax);
        var minimumRowHeight = baseSize / NextFontSizeRatio;

        var requestedGap = _lineGapPercent > 0 ? (_lineGapPercent / 100.0) * baseSize : 0;

        var baseExtra = Math.Max(0, host - (2 * Math.Floor(host / 2)));
        var maxExtra = Math.Max(baseExtra, host - (2 * minimumRowHeight));
        var extra = requestedGap <= 0
            ? baseExtra
            : Math.Min(baseExtra + (2 * requestedGap), maxExtra);

        _rowHeight = Math.Max(1, (host - extra) / 2);
        _rowGap = extra;
        _linePitch = _rowHeight + _rowGap;

        var currentSizeMax = Math.Max(MinimumFontSizeDip, _rowHeight * NextFontSizeRatio);
        _currentFontSize = Math.Min(_fontSize, currentSizeMax);
        _nextFontSize = Math.Max(MinimumNextFontSizeDip, _currentFontSize * NextFontSizeRatio);

        _trackInfoArtist.Margin = new Thickness(0, Math.Max(0, _rowGap), 0, 0);

        // 文本宽度参与行内滚动与省略号的判断，宽度一变缓存就得作废。
        // Text width drives the in-row scrolling and the ellipsis, so the caches go with it.
        foreach (var row in _rows)
            row.InvalidateRuns();
    }

    private void ApplyTrackInfoFonts()
    {
        _trackInfoTitle.FontFamily = _typeface.FontFamily;
        _trackInfoArtist.FontFamily = _typeface.FontFamily;
        _trackInfoTitle.FontSize = _currentFontSize;
        _trackInfoArtist.FontSize = _nextFontSize;
        _trackInfoTitle.Foreground = _primaryBrush;
        _trackInfoArtist.Foreground = _secondaryBrush;
        _trackInfoTitle.Opacity = 0.98;
        _trackInfoArtist.Opacity = 0.72;

        // 曲目信息的对齐跟随歌词排版设置（`.layout[data-text-alignment] .track-info`）。
        // Track-info alignment follows the lyric alignment setting.
        _trackInfoStack.HorizontalAlignment = _textAlignment switch
        {
            "Center" => HorizontalAlignment.Center,
            "Right" => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left
        };
    }

    private static Typeface ResolveTypeface(string? fontFamily)
        => string.IsNullOrWhiteSpace(fontFamily)
            ? new Typeface("Segoe UI")
            : new Typeface(new FontFamily(fontFamily), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private void ApplyTextShadow(string? shadow)
    {
        if (string.IsNullOrWhiteSpace(shadow) ||
            string.Equals(shadow, "none", StringComparison.OrdinalIgnoreCase))
        {
            _lyricsCanvas.Effect = null;
            return;
        }

        // 网页版是 `0 0 3px rgba(0,0,0,0.85)` 这种 `0 0 <blur> <color>` 形式，只取模糊半径与颜色。
        // The web value looks like `0 0 3px rgba(0,0,0,0.85)`; only the blur radius and the colour matter here.
        var blur = 3.0;
        var parts = shadow!.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (!parts[i].EndsWith("px", StringComparison.OrdinalIgnoreCase))
                continue;

            if (double.TryParse(parts[i][..^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
                blur = px;

            if (i + 1 < parts.Length)
            {
                var color = ParseCssColor(parts[i + 1], Color.FromArgb(0xD9, 0, 0, 0));
                _textShadow.Color = Color.FromRgb(color.R, color.G, color.B);
                _textShadow.Opacity = color.A / 255.0;
            }

            break;
        }

        _textShadow.BlurRadius = Math.Max(0, blur);
        _textShadow.ShadowDepth = 0;
        _lyricsCanvas.Effect = _textShadow;
    }

    // ============================================================================================
    // 绘制
    // ============================================================================================

    private void RenderLyrics(DrawingContext dc)
    {
        try
        {
            RenderLyricsCore(dc);
        }
        catch (Exception ex)
        {
            // 绘制里抛异常会顺着 WPF 的渲染路径炸到应用级异常处理上，而那一刻宿主还在等这一帧。
            // 报一次失败、交回宿主决定，比让异常一路冒上去安全。
            // An exception here would travel WPF's render path up to the app-level handler while the host is still waiting on
            // this frame; reporting a failure once and handing control back is safer than letting it bubble.
            if (_failureReported)
                return;

            _failureReported = true;
            Log.Error("Lyrics", "原生歌词渲染失败。", ex);
            Dispatcher.BeginInvoke(new Action(() => Failed?.Invoke(this, EventArgs.Empty)));
        }
    }

    private void RenderLyricsCore(DrawingContext dc)
    {
        if (_layoutDirty)
            RecomputeLayout();

        var width = _textAreaWidth;
        var height = _textAreaHeight;
        if (width <= 0 || height <= 0)
            return;

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
        for (var i = 0; i < _rows.Length; i++)
        {
            var row = _rows[i];
            if (row.IsEmpty)
                continue;

            var top = (i * _linePitch) + _trackOffset + row.OffsetY;
            if (top > height || top + _rowHeight < 0)
                continue;

            DrawRow(dc, row, top);
        }

        dc.Pop();
    }

    private void DrawRow(DrawingContext dc, Row row, double top)
    {
        var measured = EnsureRuns(row);
        var runs = measured.Runs;
        if (runs is null || runs.Runs.Count == 0)
            return;

        var textWidth = runs.Width;
        var available = Math.Max(0, _textAreaWidth);

        // 水平滚动的位移在绘制这一帧时现算：它只依赖扫描头的当前位置与这一行的自然宽度，
        // 放在这里算就不会因为"渲染循环没跑"（窗口被隐藏、离屏渲染）而停在 0。
        // The horizontal scroll offset is resolved while drawing this frame: it depends only on the scan head's current
        // position and the row's natural width, and computing it here keeps it from sticking at zero whenever the render loop
        // is not running (hidden window, offscreen render).
        if (measured.Scrolling)
            row.ScrollOffset = SnapToPhysicalPixel(row.ResolveScrollOffset(available));

        double originX;
        if (measured.Scrolling)
        {
            // 滚动时文本按自然宽度排布、靠左起排，位移由扫描头决定。
            // While scrolling the text lays out at its natural width, starting at the left, with the offset driven by the
            // scan head.
            originX = -row.ScrollOffset;
        }
        else
        {
            originX = row.Align switch
            {
                HorizontalAlign.Right => Math.Max(0, available - textWidth),
                HorizontalAlign.Center => Math.Max(0, (available - textWidth) / 2),
                _ => 0
            };
        }

        var originY = top + Math.Max(0, (_rowHeight - (runs.Height + LineTextPaddingBottomDip)) / 2);

        var pushedTransform = false;
        if (Math.Abs(row.Scale - 1) > 0.0001)
        {
            // 缩放原点按对齐走：靠左的行绕左边缩放，居中的绕中心，靠右的绕右边。
            // 升格行在左右分离下必须绕左边，否则它会一边放大一边往左飘。
            // The scale origin follows the alignment: a left-aligned row scales about its left edge, a centred one about its
            // centre, and a right-aligned one about its right edge. A promoted row in split mode must scale about the left,
            // otherwise it drifts leftwards while growing.
            var originXForScale = row.Align switch
            {
                HorizontalAlign.Right => available,
                HorizontalAlign.Center => available / 2,
                _ => 0
            };
            dc.PushTransform(new ScaleTransform(row.Scale, row.Scale, originXForScale, top + (_rowHeight / 2)));
            pushedTransform = true;
        }

        dc.PushOpacity(Math.Clamp(row.Opacity, 0, 1));

        if (row.ScanTarget is not null)
        {
            // 逐字擦亮：整行先按"未唱"的颜色铺一遍，再按"已唱"的颜色叠一遍，并用一条从左到右的透明渐变把叠加层
            // 裁到扫描头处，软边 0.12em——与 CSS 的 mask-image 是同一件事。
            // Syllable scan: lay the whole row down in the unsung colour, then again in the sung colour, masking the second
            // pass with a left-to-right alpha gradient that ends at the scan head with a 0.12em soft edge — the same thing
            // CSS does with mask-image.
            var display = Math.Clamp(row.ScanDisplay, 0, 1);
            var featherPx = WordScanFeatherEm * row.FontSize;
            // 掩膜的坐标必须钉在"这一行文本"上，所以用 Absolute 映射（相对边界框会落到整个歌词区，扫描头位置就全错了）。
            // 渐变停靠点始终是 [0,1] 的分数，跨度由 StartPoint/EndPoint 决定。
            // The mask has to be anchored to this row's text, so it uses absolute mapping; relative-to-bounding-box would map
            // to the whole lyric area and put the scan head in the wrong place. Gradient stop offsets stay fractions in
            // [0,1] and the span comes from StartPoint/EndPoint.
            var span = Math.Max(1, textWidth);
            var mask = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(originX, originY),
                EndPoint = new Point(originX + span, originY)
            };
            var feather = Math.Min(featherPx / span, Math.Min(display, 1 - display));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, Math.Clamp(display - feather, 0, 1)));
            mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), Math.Clamp(display + feather, 0, 1)));
            mask.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));

            DrawRuns(dc, runs, originX, originY, _wordScanBaseBrush);
            dc.PushOpacityMask(mask);
            DrawRuns(dc, runs, originX, originY, _wordScanOverlayBrush);
            dc.Pop();
        }
        else
        {
            DrawRuns(dc, runs, originX, originY, row.Brush);
        }

        dc.Pop();
        if (pushedTransform)
            dc.Pop();
    }

    private static void DrawRuns(DrawingContext dc, TextRunSet runs, double originX, double originY, Brush brush)
    {
        foreach (var run in runs.Runs)
        {
            run.Text.SetForegroundBrush(brush);
            dc.DrawText(run.Text, new Point(originX + run.X, originY));
        }
    }

    // ============================================================================================
    // 文本度量与缓存
    // ============================================================================================

    private double PixelsPerDip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private double SnapToPhysicalPixel(double value)
    {
        var dpr = Math.Max(1, VisualTreeHelper.GetDpi(this).DpiScaleX);
        return Math.Round(value * dpr) / dpr;
    }

    /// <summary>一行的文本段缓存（内容、字体、字号、可用宽度任一变化都要重建）。/ Run cache for one row; any change of text, font, size, or width rebuilds it.</summary>
    private sealed class TextRunSet
    {
        public List<TextRun> Runs { get; } = [];
        public double Width { get; set; }
        public double Height { get; set; }
    }

    private sealed class TextRun(FormattedText text, double x)
    {
        public FormattedText Text { get; } = text;
        public double X { get; } = x;
    }

    private readonly record struct MeasuredRow(TextRunSet? Runs, bool Scrolling);

    /// <summary>
    /// 取这一行要画的文本段。两种形态按需构建并各自缓存：整串不裁的"自然"版（超长行滚动时用），
    /// 以及放不下时截断加省略号的"裁剪"版。要不要滚动得先知道自然宽度，所以两版必须分开缓存——
    /// 只用一版会陷入"要判断宽度得先构建、要构建得先知道宽度"的死循环。
    /// Returns the runs to draw for this row. Two forms are built and cached on demand: the natural one with no truncation
    /// (used while an overlong line scrolls) and the truncated one with an ellipsis. Whether to scroll depends on the natural
    /// width, so the two caches have to be separate — a single cache would be circular, since deciding the width needs the
    /// build and the build needs the width.
    /// </summary>
    private MeasuredRow EnsureRuns(Row row)
    {
        if (row.Text.Length == 0)
            return new MeasuredRow(null, false);

        var cached = row.TryGetRuns(_typeface, _characterSpacingPercent, _textAreaWidth);
        if (cached is { } value)
            return value;

        var natural = BuildRuns(row.Text, _typeface, row.FontSize, _characterSpacingPercent, _textAreaWidth, trim: false);
        var scrolling = row.ScanTarget is not null &&
                        _textAreaWidth > 0 &&
                        natural.Width > _textAreaWidth + 0.5;

        TextRunSet trimmed;
        if (scrolling || _textAreaWidth <= 0 || natural.Width <= _textAreaWidth)
            trimmed = natural;
        else
            trimmed = BuildRuns(row.Text, _typeface, row.FontSize, _characterSpacingPercent, _textAreaWidth, trim: true);

        var measured = new MeasuredRow(scrolling ? natural : trimmed, scrolling);
        row.StoreRuns(measured, natural.Width, _typeface, _characterSpacingPercent, _textAreaWidth);
        return measured;
    }

    /// <summary>
    /// 构建一行的文本段。字距为 0 时整串是一个 <see cref="FormattedText"/>（保留连字与 ClearType），
    /// 字距大于 0 时退化成"每字一段 + 手工推进"——这是 WPF 里唯一能做出 letter-spacing 的办法
    /// （TextBlock 与 FormattedText 都没有字距属性）。
    ///
    /// 省略号是自己截的，没有用 <c>FormattedText.Trimming</c>：那个属性在 <c>MaxTextWidth</c> 不够时
    /// 表现为"换行"而不是"截断加省略号"（实测：一行超长歌词被排成两行，第二行还压在下面一行上），
    /// 所以宽度一律不设上限，放不下就自己找出能放下的最长前缀再接一个省略号。
    /// Builds one row's runs. At zero character spacing the whole string is a single FormattedText (keeping ligatures and
    /// ClearType); above zero it degrades to one run per character with a manual advance, which is the only way to get
    /// letter-spacing out of WPF — neither TextBlock nor FormattedText has a spacing property.
    ///
    /// The ellipsis is produced by hand rather than with FormattedText.Trimming: that property wraps instead of trimming when
    /// MaxTextWidth is too small (measured: an overlong line was laid out on two rows, the second overlapping the row below),
    /// so the width is left unbounded and the longest fitting prefix is found and given an ellipsis.
    /// </summary>
    private TextRunSet BuildRuns(string text, Typeface typeface, double fontSize, double spacingPercent, double availableWidth, bool trim)
    {
        var result = new TextRunSet();
        if (text.Length == 0)
            return result;

        var spacingPx = Math.Max(0, (spacingPercent / 100.0) * fontSize);

        // 逐字路径先量一遍每个字的宽度，后面找最长前缀就是纯加法。
        // The per-character path measures every glyph once up front, which turns the longest-prefix search into additions.
        double[]? charWidths = null;
        if (spacingPx > 0.01)
        {
            charWidths = new double[text.Length];
            for (var i = 0; i < text.Length; i++)
                charWidths[i] = MeasureWidth(text[i].ToString(), typeface, fontSize);
        }

        var shown = text;
        if (trim && availableWidth > 0)
        {
            var ellipsisWidth = MeasureWidth("…", typeface, fontSize);
            var budget = Math.Max(0, availableWidth - ellipsisWidth);
            var keep = LongestPrefixThatFits(text, charWidths, spacingPx, budget, typeface, fontSize);
            if (keep < text.Length)
                shown = text[..keep];
        }

        if (spacingPx <= 0.01)
        {
            var formatted = CreateFormattedText(shown, typeface, fontSize);
            result.Runs.Add(new TextRun(formatted, 0));
            result.Width = formatted.WidthIncludingTrailingWhitespace;
            result.Height = formatted.Height;
        }
        else
        {
            var x = 0.0;
            var height = 0.0;
            for (var i = 0; i < shown.Length; i++)
            {
                var run = CreateFormattedText(shown[i].ToString(), typeface, fontSize);
                result.Runs.Add(new TextRun(run, x));
                height = Math.Max(height, run.Height);
                x += charWidths![i] + spacingPx;
            }

            result.Width = Math.Max(0, x - spacingPx);
            result.Height = height > 0 ? height : fontSize;
        }

        if (trim && shown.Length < text.Length)
        {
            var ellipsis = CreateFormattedText("…", typeface, fontSize);
            result.Runs.Add(new TextRun(ellipsis, result.Width));
            result.Width += ellipsis.WidthIncludingTrailingWhitespace;
            result.Height = Math.Max(result.Height, ellipsis.Height);
        }

        return result;
    }

    /// <summary>能塞进给定宽度的最长前缀长度。/ Length of the longest prefix that fits the given width.</summary>
    private int LongestPrefixThatFits(
        string text,
        double[]? charWidths,
        double spacingPx,
        double budget,
        Typeface typeface,
        double fontSize)
    {
        if (budget <= 0)
            return 0;

        if (charWidths is not null && spacingPx > 0.01)
        {
            var right = 0.0;
            for (var i = 0; i < text.Length; i++)
            {
                right += (i > 0 ? spacingPx : 0) + charWidths[i];
                if (right > budget)
                    return i;
            }

            return text.Length;
        }

        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (MeasureWidth(text[..mid], typeface, fontSize) <= budget)
                low = mid;
            else
                high = mid - 1;
        }

        return low;
    }

    private FormattedText CreateFormattedText(string text, Typeface typeface, double fontSize)
        => new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            Brushes.White,
            PixelsPerDip);

    private double MeasureWidth(string text, Typeface typeface, double fontSize)
    {
        if (text.Length == 0)
            return 0;

        return CreateFormattedText(text, typeface, fontSize).WidthIncludingTrailingWhitespace;
    }

    // ============================================================================================
    // 行
    // ============================================================================================

    private enum HorizontalAlign
    {
        Left,
        Center,
        Right
    }

    /// <summary>一行歌词（或一行译文）的全部绘制状态。/ Everything needed to draw one lyric row (or one translation row).</summary>
    private sealed class Row
    {
        private string _text = string.Empty;
        private double _fontSize;
        private double _spacingPercent = -1;
        private Typeface? _typeface;
        private double _runsWidth = -1;
        private MeasuredRow? _runs;

        public Brush Brush { get; set; } = Brushes.White;
        public double Opacity { get; set; }
        public double Scale { get; set; } = 1;
        public HorizontalAlign Align { get; set; } = HorizontalAlign.Center;
        public double OffsetY { get; set; }
        public double AvailableWidth { get; set; }

        /// <summary>逐字擦亮的权威进度（0~1）；null 表示这一行不做擦亮。</summary>
        public double? ScanTarget { get; private set; }

        /// <summary>平滑后的显示进度；对应网页版 90ms 的 <c>--word-scan-progress</c> 过渡。</summary>
        public double ScanDisplay { get; set; }

        /// <summary>水平滚动的位移（DIP，正数表示内容向左移）。</summary>
        public double ScrollOffset { get; set; }

        /// <summary>这一行的自然宽度（不受省略号限制）。/ Natural width of the row, without trimming.</summary>
        public double NaturalWidth { get; private set; }

        public double FontSize => _fontSize;

        public string Text => _text;

        public bool IsEmpty => _text.Length == 0 || Opacity <= 0.001;

        public void Configure(
            string text,
            double fontSize,
            Brush brush,
            double opacity,
            HorizontalAlign align,
            double offsetY)
        {
            var value = text ?? string.Empty;
            if (!string.Equals(_text, value, StringComparison.Ordinal) ||
                Math.Abs(_fontSize - fontSize) > 0.001)
            {
                InvalidateRuns();
            }

            _text = value;
            _fontSize = fontSize;
            Brush = brush;
            Opacity = opacity;
            Align = align;
            OffsetY = offsetY;
            Scale = 1;
        }

        public void Reset()
        {
            _text = string.Empty;
            Opacity = 0;
            Scale = 1;
            ScanTarget = null;
            ScanDisplay = 0;
            ScrollOffset = 0;
            NaturalWidth = 0;
            InvalidateRuns();
        }

        public void SetScanTarget(double? progress)
        {
            if (progress is not double value)
            {
                ScanTarget = null;
                ScanDisplay = 0;
                ScrollOffset = 0;
                return;
            }

            var clamped = Math.Clamp(value, 0, 1);
            var previous = ScanDisplay;

            // 只有"向前小步推进"才平滑；倒退或大跳（拖进度条、换行）直接落位，否则会看到扫描头倒着爬。
            // Only small forward steps are smoothed; a backward step or a jump (seek, line change) lands immediately,
            // otherwise the scan head would visibly crawl backwards.
            if (ScanTarget is null ||
                clamped < previous - ScanSmoothingBackwardTolerance ||
                clamped - previous > ScanSmoothingMaxJump)
            {
                ScanDisplay = clamped;
            }

            ScanTarget = clamped;
        }

        public MeasuredRow? TryGetRuns(Typeface typeface, double spacingPercent, double availableWidth)
        {
            if (_text.Length == 0)
                return null;

            if (_runs is { } cached &&
                ReferenceEquals(_typeface, typeface) &&
                Math.Abs(_spacingPercent - spacingPercent) < 0.001 &&
                Math.Abs(_runsWidth - availableWidth) < 0.5)
            {
                return cached;
            }

            return null;
        }

        public void StoreRuns(
            MeasuredRow measured,
            double naturalWidth,
            Typeface typeface,
            double spacingPercent,
            double availableWidth)
        {
            _runs = measured;
            _typeface = typeface;
            _spacingPercent = spacingPercent;
            _runsWidth = availableWidth;
            NaturalWidth = naturalWidth;
        }

        public void InvalidateRuns()
        {
            _runs = null;
            _runsWidth = -1;
        }

        /// <summary>
        /// 水平滚动的目标位移：扫描头推到可视宽度 65% 处才开始滚（<c>updateLineHorizontalScroll</c>）。
        /// Target scroll offset: scrolling only starts once the scan head reaches 65% of the visible width.
        /// </summary>
        public double ResolveScrollOffset(double viewportWidth)
        {
            var overflow = Math.Max(0, NaturalWidth - viewportWidth);
            if (ScanTarget is null || viewportWidth <= 0 || overflow < 0.5)
                return 0;

            var scanHead = ScanDisplay * NaturalWidth;
            var anchor = viewportWidth * HorizontalScrollAnchorRatio;
            return Math.Clamp(scanHead - anchor, 0, overflow);
        }
    }

    // ============================================================================================
    // 动画
    // ============================================================================================

    private sealed class Tween
    {
        public double From { get; init; }
        public double To { get; init; }
        public double DurationMs { get; init; }
        public double ElapsedMs { get; set; }
        public Func<double, double> Ease { get; init; } = static t => t;
        public Action<double> Apply { get; init; } = static _ => { };
        public Action? Completed { get; init; }
    }

    private sealed class Roll
    {
        public int PromotedLineIndex { get; init; }
        public int PromotedRowIndex { get; init; }
    }

    private void AddTween(double from, double to, TimeSpan duration, Action<double> apply, Action? completed = null)
    {
        if (Math.Abs(to - from) < 0.0001)
        {
            apply(to);
            completed?.Invoke();
            return;
        }

        _tweens.Add(new Tween
        {
            From = from,
            To = to,
            DurationMs = Math.Max(1, duration.TotalMilliseconds),
            Apply = apply,
            Completed = completed,
            // 与 CSS 的 cubic-bezier(0.22, 0.72, 0.24, 1) 观感接近的减速曲线。
            // A decelerating curve close to the CSS cubic-bezier(0.22, 0.72, 0.24, 1).
            Ease = static t => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3)
        });
    }

    private void EnsureRenderLoop()
    {
        if (_renderLoopHooked || _disposed)
            return;

        _renderLoopHooked = true;
        _lastTickMs = _clock.Elapsed.TotalMilliseconds;
        _lastRenderingMs = _lastTickMs;
        CompositionTarget.Rendering += OnRendering;
        _animationWatchdog.Start();
    }

    private void ReleaseRenderLoop()
    {
        if (!_renderLoopHooked)
            return;

        _renderLoopHooked = false;
        CompositionTarget.Rendering -= OnRendering;
        _animationWatchdog.Stop();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalMilliseconds;
        _lastRenderingMs = now;
        var dt = now - _lastTickMs;
        _lastTickMs = now;
        StepAnimations(dt);
    }

    /// <summary>
    /// 只有"确实有动画在跑、而渲染回调已经 50ms 没来"（窗口被隐藏时会发生）才兜底推进一步。
    /// Only steps in when something is genuinely animating and the render callback has been silent for 50 ms, which happens
    /// while the window is hidden.
    /// </summary>
    private void StepFromWatchdog()
    {
        if (!_renderLoopHooked || _tweens.Count == 0)
            return;

        var now = _clock.Elapsed.TotalMilliseconds;
        if (now - _lastRenderingMs < 50)
            return;

        var dt = now - _lastTickMs;
        _lastTickMs = now;
        StepAnimations(dt);
    }

    private void StepAnimations(double dtMs)
    {
        var dt = Math.Clamp(dtMs, 0, 120);
        var active = false;

        for (var i = _tweens.Count - 1; i >= 0; i--)
        {
            var tween = _tweens[i];
            active = true;
            tween.ElapsedMs += dt;
            var progress = Math.Clamp(tween.ElapsedMs / tween.DurationMs, 0, 1);
            tween.Apply(tween.From + ((tween.To - tween.From) * tween.Ease(progress)));
            if (progress >= 1)
            {
                _tweens.RemoveAt(i);
                tween.Completed?.Invoke();
            }
        }

        foreach (var row in _rows)
        {
            if (row.ScanTarget is not double target)
                continue;

            var diff = target - row.ScanDisplay;
            if (Math.Abs(diff) < 0.0015)
            {
                row.ScanDisplay = target;
                continue;
            }

            row.ScanDisplay += diff * Math.Min(1, dt / WordScanSmoothingDuration.TotalMilliseconds);
            active = true;
        }

        // 滚动位移在 DrawRow 里按当帧的扫描头位置现算（见那里的注释），这里不重复。
        // The scroll offset is resolved per frame inside DrawRow (see the note there) and is not duplicated here.

        if (active)
            _lyricsCanvas.InvalidateVisual();

        if (_tweens.Count == 0 && !AnyScanSmoothing())
        {
            ReleaseRenderLoop();
            _lyricsCanvas.InvalidateVisual();

            var queued = _pendingFrame;
            _pendingFrame = null;
            if (queued is { } frame && !ReferenceEquals(frame, _lastFrame))
                Dispatcher.BeginInvoke(new Action(() => PresentCore(frame, animateTransition: true)));
        }
    }

    private bool AnyScanSmoothing()
    {
        foreach (var row in _rows)
        {
            if (row.ScanTarget is double target && Math.Abs(target - row.ScanDisplay) > 0.0015)
                return true;
        }

        return false;
    }

    // ============================================================================================
    // 封面与浮层动画
    // ============================================================================================

    private void FadeCoverImages(BitmapImage? bitmap)
    {
        // 双图交叉淡入淡出：新图放到"待用"那张上淡入，同时把"在用"那张淡出，然后互换角色，
        // 这样换歌时不会先闪一下底色。没有封面时两张一起淡出，露出底下的首字母色块。
        // Two-image crossfade: the new bitmap fades in on the standby image while the active one fades out, then the roles
        // swap, so a track change never flashes the base colour first. With no artwork both fade out and the initial tile
        // underneath shows through.
        var incoming = _coverImageOnA ? _coverImageB : _coverImageA;
        var outgoing = _coverImageOnA ? _coverImageA : _coverImageB;

        if (bitmap is null)
        {
            AnimateOpacity(_coverImageA, ref _coverOpacityA, 0, CoverFadeDuration);
            AnimateOpacity(_coverImageB, ref _coverOpacityB, 0, CoverFadeDuration);
            return;
        }

        incoming.Source = bitmap;
        if (_coverImageOnA)
            AnimateOpacity(incoming, ref _coverOpacityB, 1, CoverFadeDuration);
        else
            AnimateOpacity(incoming, ref _coverOpacityA, 1, CoverFadeDuration);

        if (_coverImageOnA)
            AnimateOpacity(outgoing, ref _coverOpacityA, 0, CoverFadeDuration);
        else
            AnimateOpacity(outgoing, ref _coverOpacityB, 0, CoverFadeDuration);

        _coverImageOnA = !_coverImageOnA;
    }

    private void AnimateBlurRadius(double to, TimeSpan duration)
    {
        _overlayBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        _overlayBlur.Radius = _overlayBlurRadius;
        _overlayBlur.BeginAnimation(BlurEffect.RadiusProperty, new DoubleAnimation(_overlayBlurRadius, to, duration));
        _overlayBlurRadius = to;
    }

    private static void AnimateOpacity(UIElement element, ref double current, double to, TimeSpan duration)
    {
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = current;
        if (Math.Abs(to - current) > 0.001)
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(current, to, duration));

        current = to;
    }

    private static BitmapImage? TryDecodeDataUri(string dataUri)
    {
        var comma = dataUri.IndexOf(',');
        if (comma < 0 || comma == dataUri.Length - 1)
            return null;

        try
        {
            var bytes = Convert.FromBase64String(dataUri[(comma + 1)..]);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = new System.IO.MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or System.IO.IOException)
        {
            Log.Warn("Lyrics", $"原生渲染器解码封面失败: {ex.Message}");
            return null;
        }
    }

    // ============================================================================================
    // 颜色
    // ============================================================================================

    private static Brush CreateBrush(string? cssColor, Color fallback)
        => new SolidColorBrush(ParseCssColor(cssColor, fallback));

    /// <summary>
    /// 解析宿主下发的 CSS 颜色。宿主实际只会给 <c>#AARRGGBB</c> 与 <c>rgba(r, g, b, a)</c> 两种，
    /// 外加封面占位色的 <c>hsl(h, s%, l%)</c>，其余一律退回给定的兜底色。
    /// Parses the CSS colours the host sends. In practice it only emits <c>#AARRGGBB</c> and <c>rgba(r, g, b, a)</c>, plus the
    /// artwork placeholder's <c>hsl(h, s%, l%)</c>; anything else falls back to the supplied colour.
    /// </summary>
    private static Color ParseCssColor(string? value, Color fallback)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return fallback;

        if (text.StartsWith('#'))
        {
            var hex = text[1..];
            if (hex.Length == 3)
                hex = $"{hex[0]}{hex[0]}{hex[1]}{hex[1]}{hex[2]}{hex[2]}";
            if (hex.Length == 6)
                hex = "FF" + hex;

            if (hex.Length == 8 &&
                uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
            {
                return Color.FromArgb(
                    (byte)((packed >> 24) & 0xFF),
                    (byte)((packed >> 16) & 0xFF),
                    (byte)((packed >> 8) & 0xFF),
                    (byte)(packed & 0xFF));
            }

            return fallback;
        }

        if (text.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitArguments(text);
            if (parts.Length < 3)
                return fallback;

            var r = (byte)Math.Clamp(ParseNumber(parts[0]), 0, 255);
            var g = (byte)Math.Clamp(ParseNumber(parts[1]), 0, 255);
            var b = (byte)Math.Clamp(ParseNumber(parts[2]), 0, 255);
            var a = parts.Length > 3 ? (byte)Math.Clamp(ParseAlpha(parts[3]), 0, 255) : (byte)255;
            return Color.FromArgb(a, r, g, b);
        }

        if (text.StartsWith("hsl(", StringComparison.OrdinalIgnoreCase))
        {
            var parts = SplitArguments(text);
            if (parts.Length < 3)
                return fallback;

            return FromHsl(ParseNumber(parts[0]) / 360.0, ParsePercent(parts[1]), ParsePercent(parts[2]));
        }

        return fallback;
    }

    private static string[] SplitArguments(string text)
    {
        var open = text.IndexOf('(');
        var close = text.IndexOf(')');
        if (open < 0 || close <= open)
            return [];

        return text[(open + 1)..close].Split(',', StringSplitOptions.TrimEntries);
    }

    private static double ParseNumber(string value)
        => double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

    private static double ParsePercent(string value)
    {
        var text = value.Trim().TrimEnd('%');
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed / 100.0, 0, 1)
            : 0;
    }

    private static int ParseAlpha(string value)
    {
        var text = value.Trim();
        if (text.EndsWith('%'))
            return (int)Math.Round(Math.Clamp(ParseNumber(text.TrimEnd('%')) / 100.0, 0, 1) * 255);

        var parsed = ParseNumber(text);
        return parsed <= 1 ? (int)Math.Round(parsed * 255) : (int)Math.Round(parsed);
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        var h = (((hue % 1) + 1) % 1) * 6;
        var c = (1 - Math.Abs((2 * lightness) - 1)) * saturation;
        var x = c * (1 - Math.Abs((h % 2) - 1));
        var m = lightness - (c / 2);

        var (r, g, b) = (int)Math.Floor(h) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };

        return Color.FromRgb(
            (byte)Math.Round(Math.Clamp(r + m, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(g + m, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(b + m, 0, 1) * 255));
    }

    // ============================================================================================
    // 自绘元素
    // ============================================================================================

    /// <summary>
    /// 歌词自绘面板。整个歌词区只有这一个元素，所以上滚位移、逐字擦亮、水平滚动、省略号全在同一个坐标系里算。
    /// The lyric drawing surface. The whole lyric area is this one element, so the roll offset, the syllable scan, the
    /// horizontal scroll, and the ellipsis are all resolved in a single coordinate space.
    /// </summary>
    private sealed class LyricsCanvas(LyricsNativeRenderer owner) : FrameworkElement
    {
        protected override void OnRender(DrawingContext drawingContext) => owner.RenderLyrics(drawingContext);
    }
}
