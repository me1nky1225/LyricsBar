namespace LyricsBar.Classes.Services.Lyrics;

/// <summary>歌词视图的外观输入。与具体渲染实现无关。/ Appearance input for the lyrics view, independent of the renderer.</summary>
public sealed record LyricsWebStyle(
    string FontFamily,
    string LatinFontFamily,
    string CjkFontFamily,
    double FontSize,
    int FontWeight,
    double CharacterSpacingPercent,
    double LineGapPercent,
    string TextAlignment,
    string PrimaryColor,
    string SecondaryColor,
    string TranslationColor,
    string WordScanBaseColor,
    string WordScanOverlayColor,
    string TextShadow,
    bool ShowCover = false,
    double CoverSizeDip = 34);

/// <summary>
/// 歌词渲染器的公共面。宿主（<c>TaskbarLyricsWindow</c>）只认这一层，
/// 因此"原生自绘"与"WebView2 承载网页"两种实现可以并存、互换，也便于逐项对拍观感。
/// The common surface of a lyrics renderer. The host (<c>TaskbarLyricsWindow</c>) only knows this layer, so the native
/// self-drawn implementation and the WebView2-hosted page can coexist and be swapped, which also makes it practical to
/// compare them item by item.
///
/// 协议与原来的 WebView2 适配器完全一致（见 <c>RENDERER-REWRITE.md</c> §3.2）：
/// 宿主只喂三样东西——一帧歌词、一份外观，外加封面与曲目信息两条独立消息；渲染器不回传任何东西。
/// The contract is exactly the one the WebView2 adapter had (see RENDERER-REWRITE.md §3.2): the host feeds a lyric frame, an
/// appearance, and two standalone messages for artwork and track info; nothing travels back.
/// </summary>
public interface ILyricsRenderer : IDisposable
{
    /// <summary>渲染器已就绪、可以接收消息时触发。/ Raised once the renderer is ready to receive messages.</summary>
    event EventHandler? Ready;

    /// <summary>渲染失败、由宿主决定是否重建时触发。/ Raised when rendering fails and the host should decide whether to rebuild.</summary>
    event EventHandler? Failed;

    /// <summary>是否已就绪。/ Whether the renderer is ready.</summary>
    bool IsReady { get; }

    /// <summary>初始化。原生实现是同步的，WebView2 实现要等文档加载完成。/ Initializes; the native one is synchronous while the WebView2 one waits for the document.</summary>
    Task<bool> InitializeAsync();

    /// <summary>下发外观（字体、字号、颜色、对齐、封面开关等）。相同外观应被短路。/ Applies the appearance; an identical style should be short-circuited.</summary>
    void ApplyStyle(LyricsWebStyle style);

    /// <summary>下发封面。空 <paramref name="dataUri"/> 表示没有封面，退回首字母色块。/ Pushes the artwork; an empty data URI falls back to the initial tile.</summary>
    void SetCover(string dataUri, string fallbackText, string fallbackColor, string trackId);

    /// <summary>下发封面右侧那块独立的曲目信息（曲名 / 歌手）；两条都为空即收起。/ Pushes the standalone track-info block; two empty strings collapse it.</summary>
    void SetTrackInfo(string title, string artist, double widthDip);

    /// <summary>悬停浮层的显隐。/ Visibility of the hover overlay.</summary>
    void SetOverlay(bool visible);

    /// <summary>呈现一帧歌词。/ Presents one lyric frame.</summary>
    void Present(LyricsPresentationFrame frame, bool animateTransition);

    /// <summary>任务栏不可见时暂停投递，只保留最新一帧。/ Pauses delivery while the taskbar is invisible, keeping only the newest frame.</summary>
    void PauseDelivery();

    /// <summary>恢复投递。/ Resumes delivery.</summary>
    void ResumeDelivery();

    /// <summary>尽力挂起以省内存（只有 WebView2 实现真的能挂起）。/ Best-effort suspension; only the WebView2 one can actually suspend.</summary>
    Task<bool> TrySuspendAsync();

    /// <summary>从挂起中恢复。/ Resumes from suspension.</summary>
    void Resume();
}
