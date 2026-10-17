using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LyricsBar.Classes.Services.Lyrics;

/// <summary>
/// WebView2 歌词适配器。它只保留每种消息的最新值，因此初始化或脚本执行较慢时不会回放过期帧。
/// WebView2 lyrics adapter. It retains only the latest value of each message kind, so stale frames are never replayed after
/// initialization or slow script execution.
/// </summary>
/// <param name="webView">承载歌词页的合成控件。/ The composition control hosting the lyric page.</param>
/// <param name="additionalBrowserArguments">
/// 传给 WebView2 浏览器的额外启动参数。留空即用运行时默认值；某些环境（虚拟机、远程会话、
/// 受限的企业终端）里 GPU 进程无法启动并反复崩溃，此时用 <c>--disable-gpu</c> 重建即可恢复正常。
/// Extra browser launch arguments handed to WebView2. Empty means the runtime defaults; in some environments (virtual
/// machines, remote sessions, locked-down corporate endpoints) the GPU process cannot start and crashes repeatedly, and
/// rebuilding with <c>--disable-gpu</c> restores normal operation.
/// </param>
public sealed class LyricsWebViewRenderer(WebView2CompositionControl webView, string? additionalBrowserArguments = null)
    : ILyricsRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly WebView2CompositionControl _webView = webView ?? throw new ArgumentNullException(nameof(webView));
    private readonly string? _additionalBrowserArguments = additionalBrowserArguments;
    private LyricsWebStyle? _lastStyle;
    private string? _pendingStyle;
    private string? _pendingCover;
    private string? _pendingTrackInfo;
    private string? _pendingOverlay;
    private string? _pendingLyrics;
    private bool _initializationStarted;
    private bool _documentReady;
    private bool _draining;
    private bool _deliveryPaused;
    private bool _disposed;
    private bool _failed;
    private int _unresponsiveNotifications;
    private int _auxiliaryFailureNotifications;
    private CoreWebView2? _core;
    private string _runtimeVersion = "unknown";

    /// <summary>Web 文档已完成导航且可以接收消息时触发。/ Raised when the web document has navigated and can receive messages.</summary>
    public event EventHandler? Ready;

    /// <summary>导航或脚本执行失败时触发，由控件所有者决定是否重建。/ Raised when navigation or script execution fails; the control owner decides whether to rebuild.</summary>
    public event EventHandler? Failed;

    /// <summary>Web 文档是否已可呈现。/ Whether the web document is ready to present.</summary>
    internal string RuntimeVersion => _runtimeVersion;
    internal bool CanInjectDeveloperFailure(CoreWebView2ProcessFailedKind kind) => !_disposed && !_failed &&
        (kind != CoreWebView2ProcessFailedKind.RenderProcessUnresponsive || !_deliveryPaused);

    public bool IsReady => _documentReady && !_disposed && !_failed;

    public async Task<bool> InitializeAsync()
    {
        if (_disposed || _initializationStarted)
            return false;

        _initializationStarted = true;
        try
        {
            _webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            // WebView2目录统一指到本应用其余本地数据（日志、缓存）所在的 %LOCALAPPDATA%\LyricsBar 下。
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LyricsBar");

            // 只有需要传额外启动参数时才自建环境：自建环境后 CreationProperties 不再生效，
            // 因此把用户数据目录一并显式传进去，避免多出一份默认目录。
            CoreWebView2Environment? environment = null;
            if (!string.IsNullOrWhiteSpace(_additionalBrowserArguments))
            {
                var options = new CoreWebView2EnvironmentOptions(_additionalBrowserArguments);
                environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
            }
            else if (_webView.CreationProperties is null)
            {
                _webView.CreationProperties = new CoreWebView2CreationProperties
                {
                    UserDataFolder = userDataFolder
                };
            }

            await _webView.EnsureCoreWebView2Async(environment);
            if (_disposed || _failed || _webView.CoreWebView2 is null)
                return false;

            _core = _webView.CoreWebView2;
            _core.ProcessFailed += OnProcessFailed;
            _runtimeVersion = _core.Environment.BrowserVersionString;
            var settings = _core.Settings;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsBuiltInErrorPageEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsZoomControlEnabled = false;
            _webView.NavigationCompleted += OnNavigationCompleted;
            _webView.NavigateToString(BuildDocument());
            return true;
        }
        catch (Exception ex)
        {
            AppLogService.Current?.Error("Lyrics", "Failed to initialize the web lyrics renderer.", ex);
            ReportFailure();
            return false;
        }
    }

    public void ApplyStyle(LyricsWebStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (_disposed || _failed || Equals(_lastStyle, style))
            return;

        // Snapshot updates can reapply appearance without changing it; skip the WebView2
        // round trip and CSS/layout work until an actual style value changes.
        _lastStyle = style;
        _pendingStyle = SerializeMessage("style", new
        {
            style.FontFamily,
            style.LatinFontFamily,
            style.CjkFontFamily,
            style.FontSize,
            style.FontWeight,
            // 斜体必须序列化成真正的 true/false：页面只在严格等于 true 时才写 italic，字符串 "true" 会被当成不斜。
            // The italic flag has to serialize as a real true/false: the page writes italic only when it is strictly true, and
            // the string "true" would be read as upright.
            style.FontItalic,
            style.CharacterSpacingPercent,
            style.LineGapPercent,
            style.TextAlignment,
            style.ShowCover,
            coverSize = style.CoverSizeDip,
            coverGap = 8,
            coverCornerRadius = 6,
            layoutScalePercent = 100,
            style.PrimaryColor,
            style.SecondaryColor,
            style.TranslationColor,
            style.WordScanBaseColor,
            style.WordScanOverlayColor,
            surfaceColor = "transparent",
            surfaceShadow = "none",
            style.TextShadow,

            // 曲目信息块自己的字体与颜色。五个字段都是可选的：空串 / 0 表示"跟随歌词"，
            // 页面据此把对应的 CSS 变量摘掉，var() 的兜底值于是回到歌词那一套——旧宿主不发这些字段时画面不变。
            // The track-info block's own font and colour. All five are optional: an empty string or a zero means "follow the
            // lyrics", on which the page removes the matching CSS variable so the var() fallback returns to the lyric values.
            // A host that does not send them renders exactly as before.
            trackInfoFontFamily = style.TrackInfoFontFamily ?? string.Empty,
            trackInfoFontWeight = style.TrackInfoFontWeight,
            trackInfoFontItalic = style.TrackInfoFontItalic,
            trackInfoFontScalePercent = style.TrackInfoFontScalePercent,
            trackInfoColor = style.TrackInfoColor ?? string.Empty
        });
        StartDrain();
    }

    /// <summary>
    /// 推送当前曲目的封面。<paramref name="dataUri"/> 为空串表示没有封面，Web 端会退回"曲名首字母"色块。
    /// Pushes the current track's artwork. An empty <paramref name="dataUri"/> means no artwork and the web side falls back to
    /// a coloured tile with the title's first letter.
    /// </summary>
    /// <param name="dataUri">封面 data URI，或空串。/ Artwork data URI, or an empty string.</param>
    /// <param name="fallbackText">无封面时显示的文本，Web 端只取首字符。/ Text shown when there is no artwork; the web side keeps only the first character.</param>
    /// <param name="fallbackColor">无封面时的占位底色。/ Placeholder colour used when there is no artwork.</param>
    /// <param name="trackId">用于 Web 端做换歌竞态判断的曲目标识。/ Track identity the web side uses to discard stale artwork.</param>
    public void SetCover(string dataUri, string fallbackText, string fallbackColor, string trackId)
    {
        if (_disposed || _failed) return;
        _pendingCover = SerializeMessage("cover", new
        {
            dataUri = dataUri ?? string.Empty,
            fallbackText = fallbackText ?? string.Empty,
            fallbackColor = fallbackColor ?? string.Empty,
            trackId = trackId ?? string.Empty
        });
        StartDrain();
    }

    /// <summary>
    /// 推送封面右侧那块独立的曲目信息（曲名 / 歌手）。
    ///
    /// 它刻意不走歌词帧：曲目信息不参与歌词那两行的排版，也不该和换行、淡入淡出、逐字擦亮那套状态机纠缠。
    /// 两条文本都为空即收起整块（宽度归零、不留空隙），这也是设置关掉它时的下发方式。
    /// <paramref name="widthDip"/> 是宿主量出来的区块宽度：由宿主决定宽度，Web 端照用，
    /// 两边就不会各自量一遍、对不齐。
    /// Pushes the standalone track-info block (title / artist) shown right of the artwork.
    ///
    /// It deliberately does not ride the lyric frame: track info takes part in neither lyric row and has no business entangled
    /// with the line-transition, crossfade, and syllable-scan state machine. Two empty strings collapse the whole block
    /// (zero width, no gap), which is also how the setting turns it off. <paramref name="widthDip"/> is the block width measured
    /// by the host: the host owns the width and the web side simply uses it, so the two never measure it apart and drift.
    /// </summary>
    /// <param name="title">曲名，可为空。/ Track title, may be empty.</param>
    /// <param name="artist">歌手，可为空。/ Artist, may be empty.</param>
    /// <param name="widthDip">区块宽度（DIP）。/ Block width in DIP.</param>
    public void SetTrackInfo(string title, string artist, double widthDip)
    {
        if (_disposed || _failed) return;
        _pendingTrackInfo = SerializeMessage("trackInfo", new
        {
            title = title ?? string.Empty,
            artist = artist ?? string.Empty,
            width = double.IsFinite(widthDip) && widthDip > 0 ? widthDip : 0
        });
        StartDrain();
    }

    /// <summary>
    /// 推送悬停磨砂作用在哪一块上。
    ///
    /// 覆盖效果只能由页面自己做：WPF 的 Effect 作用不到 WebView2 这种合成控件。因此宿主只负责判定指针落在哪一块上，
    /// 页面自己把那一块糊掉（内容加 filter:blur），宽度跟着窗口走，不需要宿主下发。
    /// Pushes which part the hover frost applies to.
    ///
    /// That effect can only be done by the page: WPF effects cannot reach a composition control such as WebView2. The host
    /// therefore owns only the decision about which part the pointer is over, and the page blurs that part itself (a filter:blur
    /// on the content), tracking the window width on its own.
    /// </summary>
    /// <param name="region">指针所在的块。/ The part under the pointer.</param>
    public void SetOverlay(LyricsOverlayRegion region)
    {
        if (_disposed || _failed) return;
        _pendingOverlay = SerializeMessage("overlay", new { region = ToWireRegion(region) });
        StartDrain();
    }

    /// <summary>
    /// 区域在报文里的写法：小写字符串，None 用 "none"。页面按这三个值判断，收到未知值就什么都不糊。
    /// How a region is spelled on the wire: lowercase, with None as "none". The page branches on these three values and blurs
    /// nothing when it sees an unknown one.
    /// </summary>
    private static string ToWireRegion(LyricsOverlayRegion region) => region switch
    {
        LyricsOverlayRegion.Info => "info",
        LyricsOverlayRegion.Lyrics => "lyrics",
        _ => "none"
    };

    /// <summary>
    /// 推送一帧歌词。不可见帧推的是空文本，让 Web 端把两行清空，而不是留着上一首的残影。
    /// <paramref name="animateTransition"/> 为 false 时 Web 端直接落位、不做换行与淡入淡出，
    /// 用于"歌词栏重新出现"这类不该有过渡的场景。
    /// Pushes one lyric frame. An invisible frame pushes empty text so the web side clears both rows instead of leaving the
    /// previous track's remains behind. With <paramref name="animateTransition"/> false the web side lands the frame without a
    /// line transition or crossfade, for cases like "the bar just reappeared" where a transition would be wrong.
    /// </summary>
    /// <param name="frame">要呈现的帧。/ The frame to present.</param>
    /// <param name="animateTransition">是否让 Web 端播出换行动效。/ Whether the web side should animate the line transition.</param>
    public void Present(LyricsPresentationFrame frame, bool animateTransition)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_disposed || _failed) return;
        _pendingLyrics = SerializeMessage("lyrics", new
        {
            current = frame.IsVisible ? frame.Current : string.Empty,
            next = frame.IsVisible ? frame.Next : string.Empty,
            progress = frame.IsVisible ? frame.LineProgress : 0,
            currentLineIndex = frame.IsVisible ? frame.CurrentLineIndex : -1,
            trackId = frame.IsVisible ? frame.TrackId : string.Empty,
            isPureMusic = false,
            isPlaying = frame.IsVisible && frame.IsPlaying,
            wordScanProgress = frame.IsVisible ? frame.WordScanProgress : null,
            currentTranslation = frame.IsVisible ? frame.CurrentTranslation : string.Empty,
            nextTranslation = frame.IsVisible ? frame.NextTranslation : string.Empty,
            translationMode = frame.IsVisible && frame.TranslationMode,
            animateTransition = frame.IsVisible && animateTransition,
            scene = "lyrics"
        });
        StartDrain();
    }

    /// <summary>Hold only the newest pending messages while the taskbar cannot be seen. / 任务栏不可见时只保留最新待发送消息。</summary>
    public void PauseDelivery()
    {
        _deliveryPaused = true;
        _unresponsiveNotifications = 0;
    }

    /// <summary>Resume delivery, sending style before the latest lyrics frame. / 恢复消息投递，先发送样式再发送最新歌词帧。</summary>
    public void ResumeDelivery()
    {
        if (_disposed || _failed)
            return;
        _deliveryPaused = false;
        StartDrain();
    }

    /// <summary>Best-effort renderer suspension after WPF has made the WebView invisible. / WPF 已隐藏视图后的尽力挂起。</summary>
    public async Task<bool> TrySuspendAsync()
    {
        if (!IsReady || _webView.IsVisible || _core is null)
            return false;

        try
        {
            var suspended = await _core.TrySuspendAsync();
            return !_disposed && !_failed && suspended;
        }
        catch (Exception ex)
        {
            AppLogService.Current?.Warn("Lyrics", $"Web lyrics suspension failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Resume a suspended renderer before making its WPF control visible. / 显示 WPF 控件前恢复挂起的渲染器。</summary>
    public void Resume()
    {
        if (_disposed || _failed || _core is null)
            return;

        try
        {
            _core.Resume();
        }
        catch (Exception ex)
        {
            AppLogService.Current?.Warn("Lyrics", $"Web lyrics resume failed: {ex.Message}");
            ReportFailure();
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (_disposed || _failed) return;
        try { HandleProcessFailure(e.ProcessFailedKind, e.Reason, e.ExitCode); }
        catch (Exception ex)
        {
            AppLogService.Current?.Error("Lyrics", "读取 WebView2 进程故障信息失败", ex);
            ReportFailure();
        }
    }

    internal void HandleProcessFailure(CoreWebView2ProcessFailedKind kind, CoreWebView2ProcessFailedReason reason, int exitCode, bool developerInjected = false)
    {
        if (_disposed || _failed) return;
        AppLogService.Current?.Warn("Lyrics", $"WebView2 process failure: origin={(developerInjected ? "DeveloperInjected" : "Runtime")}, kind={kind}, reason={reason}, exitCode=0x{exitCode:X8}, runtime={_runtimeVersion}");
        if (kind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            if (_deliveryPaused || ++_unresponsiveNotifications < 2) return;
        }
        else if (kind is CoreWebView2ProcessFailedKind.GpuProcessExited or CoreWebView2ProcessFailedKind.UtilityProcessExited)
        {
            // GPU 与工具进程崩溃在受限环境里会成串出现。单次崩溃不影响渲染（运行时会重启它们），
            // 但持续崩溃说明该环境跑不了默认渲染路径，累积到阈值后上报失败，由宿主改用软件渲染重建。
            // GPU and utility process crashes arrive in bursts in restricted environments. A single crash does not break
            // rendering because the runtime restarts them, but persistent crashes mean the environment cannot run the default
            // rendering path, so a failure is reported past the threshold and the host rebuilds with software rendering.
            if (++_auxiliaryFailureNotifications < 3) return;
        }
        else if (kind is not (CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited))
            return;
        ReportFailure();
    }

    private void ReportFailure()
    {
        if (_disposed || _failed) return;
        _failed = true;
        _documentReady = false;
        _pendingStyle = null;
        _pendingCover = null;
        _pendingTrackInfo = null;
        _pendingOverlay = null;
        _pendingLyrics = null;
        Failed?.Invoke(this, EventArgs.Empty);
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_disposed || _failed)
            return;

        try
        {
            if (!e.IsSuccess)
            {
                AppLogService.Current?.Error("Lyrics", $"Web lyrics navigation failed: {e.WebErrorStatus}.");
                ReportFailure();
                return;
            }

            _documentReady = true;
            AppLogService.Current?.Info("Lyrics", $"Web 歌词文档已就绪（运行时 {_runtimeVersion}）。");
            await DrainAsync();
            if (IsReady)
                Ready?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLogService.Current?.Error("Lyrics", "Web lyrics navigation callback failed.", ex);
            ReportFailure();
        }
    }

    private void StartDrain()
    {
        if (IsReady && !_draining && !_deliveryPaused)
            _ = DrainAsync();
    }

    private async Task DrainAsync()
    {
        if (!IsReady || _draining || _deliveryPaused || _core is null)
            return;

        _draining = true;
        try
        {
            while (IsReady && !_deliveryPaused)
            {
                // 顺序固定为 外观 → 封面 → 曲目信息 → 按钮浮层 → 歌词：封面与曲目信息在歌词之前落地，
                // 避免换歌瞬间先出现新歌词、再补上新封面与新曲目信息那一下视觉跳动。
                // The order is fixed as appearance, artwork, track info, button plate, then lyrics: landing artwork and track
                // info before lyrics avoids the visual jump of new lyrics appearing first and the new cover and track info
                // catching up a moment later.
                var script = _pendingStyle;
                if (script is not null)
                {
                    _pendingStyle = null;
                }
                else
                {
                    script = _pendingCover;
                    if (script is not null)
                    {
                        _pendingCover = null;
                    }
                    else
                    {
                        script = _pendingTrackInfo;
                        if (script is not null)
                        {
                            _pendingTrackInfo = null;
                        }
                        else
                        {
                            script = _pendingOverlay;
                            if (script is not null)
                            {
                                _pendingOverlay = null;
                            }
                            else
                            {
                                script = _pendingLyrics;
                                _pendingLyrics = null;
                            }
                        }
                    }
                }

                if (script is null)
                    break;

                await _core.ExecuteScriptAsync(script);
                if (IsReady) _unresponsiveNotifications = 0;
            }
        }
        catch (Exception ex)
        {
            AppLogService.Current?.Error("Lyrics", "Failed to update the web lyrics renderer.", ex);
            ReportFailure();
        }
        finally
        {
            _draining = false;
            if ((_pendingStyle is not null || _pendingCover is not null ||
                 _pendingTrackInfo is not null || _pendingOverlay is not null ||
                 _pendingLyrics is not null) &&
                !_deliveryPaused && IsReady)
                StartDrain();
        }
    }

    private static string SerializeMessage(string type, object payload)
    {
        var json = JsonSerializer.Serialize(new { version = 1, type, payload }, JsonOptions);
        return $"window.taskbarLyrics?.receive({json});";
    }

    private static string BuildDocument()
    {
        var template = ReadResource("Web.Lyrics.index.html");
        var style = ReadResource("Web.Lyrics.style.css").Replace("</style", "<\\/style", StringComparison.OrdinalIgnoreCase);
        var scripts = string.Join("\n", new[]
        {
            ReadResource("Web.Lyrics.state.js"),
            ReadResource("Web.Lyrics.spacing.js"),
            ReadResource("Web.Lyrics.presentation.js"),
            ReadResource("Web.Lyrics.app.js")
        }).Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);
        return template.Replace("{{STYLE_CSS}}", style, StringComparison.Ordinal)
            .Replace("{{APP_JS}}", scripts, StringComparison.Ordinal);
    }

    private static string ReadResource(string suffix)
    {
        var assembly = typeof(LyricsWebViewRenderer).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Embedded lyrics resource '{suffix}' was not found.");
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded lyrics resource '{resourceName}' could not be opened.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _documentReady = false;
        _pendingStyle = null;
        _pendingCover = null;
        _pendingTrackInfo = null;
        _pendingOverlay = null;
        _pendingLyrics = null;
        Ready = null;
        Failed = null;
        try
        {
            _webView.NavigationCompleted -= OnNavigationCompleted;
            if (_core is not null) _core.ProcessFailed -= OnProcessFailed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException)
        {
            AppLogService.Current?.Warn("Lyrics", $"WebView2 退订失败: {ex.Message}");
        }
        finally
        {
            _core = null;
            try { _webView.Dispose(); }
            catch (Exception ex) when (ex is InvalidOperationException or COMException || WebView2GraphicsFaultPolicy.IsGraphicsFault(ex))
            {
                AppLogService.Current?.Warn("Lyrics", $"WebView2 释放失败: {ex.Message}");
            }
        }
    }
}
