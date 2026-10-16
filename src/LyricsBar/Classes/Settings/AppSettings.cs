using LyricsBar.Classes.Services.Lyrics.Presentation;

namespace LyricsBar.Classes.Settings;

/// <summary>第二行歌词的内容来源。/ Source of the second lyric row.</summary>
public enum LyricsSecondaryLineMode
{
    /// <summary>显示下一句歌词。/ Show the next lyric line.</summary>
    NextLine = 0,

    /// <summary>显示当前句翻译。/ Show the translation of the active line.</summary>
    Translation = 1,

    /// <summary>显示当前句音译（如粤拼、罗马字）。成员值参与序列化，因此只能追加。
    /// Show the romanization of the active line (Cantonese jyutping, romanized Japanese, and so on). Member values take part in
    /// serialization, so this may only be appended.</summary>
    Romanization = 2
}

/// <summary>歌词中文转换方向。/ Chinese conversion direction for lyric text.</summary>
public enum LyricsChineseConversionMode
{
    /// <summary>不做转换，歌词按来源原样显示。/ No conversion; lyrics show exactly as their source wrote them.</summary>
    None = 0,

    /// <summary>简体转繁体。/ Convert Simplified Chinese to Traditional Chinese.</summary>
    SimplifiedToTraditional = 1,

    /// <summary>繁体转简体。/ Convert Traditional Chinese to Simplified Chinese.</summary>
    TraditionalToSimplified = 2
}

/// <summary>歌词文本的水平对齐方式。/ Horizontal alignment of lyric text.</summary>
public enum LyricsTextAlignment
{
    Left = 0,
    Center = 1,
    Right = 2,

    /// <summary>
    /// 左右分离：主行靠左、第二行靠右，两行分列两端，读起来像一句一答。
    /// Left/right separation: the main row hugs the left edge and the second row the right, so the two read
    /// as a call and an answer at opposite ends.
    /// </summary>
    Split = 3
}

/// <summary>
/// 歌词栏在任务栏上的水平停靠位置。
/// Horizontal docking position of the lyric bar on the taskbar.
/// </summary>
public enum LyricsBarAlignment
{
    /// <summary>靠左：距任务栏左边缘 <see cref="AppSettings.TaskbarOffsetDip"/> 像素。
    /// Left: <see cref="AppSettings.TaskbarOffsetDip"/> from the taskbar's left edge.</summary>
    Left = 0,

    /// <summary>居中：水平居中后再按偏移量微调。/ Centred, with the offset applied as a nudge.</summary>
    Center = 1,

    /// <summary>靠右：距任务栏右边缘 <see cref="AppSettings.TaskbarOffsetDip"/> 像素。
    /// Right: <see cref="AppSettings.TaskbarOffsetDip"/> from the taskbar's right edge.</summary>
    Right = 2
}

/// <summary>歌词匹配严格度：曲名/歌手与候选曲目的相似度要到多少分才采纳。/ How similar a candidate's title and artist must be before its lyrics are accepted.</summary>
public enum LyricsMatchStrictness
{
    /// <summary>
    /// 宽松：门槛最低，宁可显示一个可能有出入的匹配，也不让歌词栏空着。
    /// 适合曲库冷门、翻唱与现场版很多的场景。
    /// Lenient: the lowest bar. A possibly-off match beats an empty bar, which suits obscure catalogues and libraries full of
    /// covers and live versions.
    /// </summary>
    Lenient = 0,

    /// <summary>
    /// 均衡（默认）：与升级前逐位一致的门槛，既有足够的兜底能力，也不会把明显不同的曲目当成同一首。
    /// Balanced (the default): bit-for-bit the thresholds used before this setting existed — enough fallback to find lyrics,
    /// but not so loose that a clearly different track passes as the same one.
    /// </summary>
    Balanced = 1,

    /// <summary>
    /// 严格：门槛明显抬高，宁可不显示歌词，也不显示别的那首（现场版、翻唱、同名曲）的歌词。
    /// 代价是冷门曲目会更容易"取不到词"。
    /// Strict: a noticeably higher bar. Not showing lyrics beats showing another track's — a live take, a cover, a namesake.
    /// The cost is that obscure tracks miss more often.
    /// </summary>
    Strict = 2
}

/// <summary>歌词渲染引擎。/ Which engine draws the lyrics.</summary>
public enum LyricsRendererEngine
{
    /// <summary>
    /// 原生 WPF 自绘（默认）。歌词由宿主进程自己绘制，不起任何浏览器子进程，
    /// 内存占用只有宿主本身（实测从约 865MB 降到 200MB 量级）。
    /// Native WPF drawing (the default). The host process draws the lyrics itself with no browser subprocesses, so memory
    /// is the host alone — measured down from roughly 865 MB to the 200 MB range.
    /// </summary>
    Native = 0,

    /// <summary>
    /// WebView2 承载内嵌网页（旧实现）。保留它是为了逐项对拍观感与出问题时兜底：
    /// 它会额外拉起一整套 msedgewebview2 子进程，内存高得多。
    /// WebView2 hosting an embedded page (the previous implementation). It is kept so the two can be compared item by item
    /// and as a fallback if something goes wrong; it spins up a whole msedgewebview2 process tree and costs far more memory.
    /// </summary>
    WebView2 = 1
}

/// <summary>
/// 设置重置的范围。原项目有九个分组（外观、布局、显示模式……）；精简版只剩歌词，于是收敛为两项。
/// 这个枚举被移植过来的 <c>LyricsCacheInvalidationPolicy</c> 使用：重置歌词设置或重置全部都要清空取词缓存。
/// The scope of a settings reset. The original had nine groups (appearance, layout, display modes, and so on); the trimmed
/// build only has lyrics left, so it collapses to two. The ported <c>LyricsCacheInvalidationPolicy</c> uses it: resetting the
/// lyrics settings or everything both clear the retrieval cache.
/// </summary>
public enum SettingsResetScope
{
    /// <summary>只重置歌词设置。/ Reset only the lyrics settings.</summary>
    Lyrics = 0,

    /// <summary>重置全部设置。/ Reset everything.</summary>
    All = 1
}

/// <summary>
/// 精简版的全部用户设置。只保留"任务栏歌词"需要的字段，其余功能（音频控制、外观、更新、频谱等）一律没有对应设置。
/// The complete user settings of the trimmed build. Only the fields the taskbar-lyrics feature needs survive; everything else
/// (audio control, appearance, updates, spectrum, and so on) has no setting at all.
/// </summary>
public sealed class AppSettings
{
    /// <summary>是否显示歌词。关闭后任务栏隐藏，不再取词。/ Whether lyrics are shown; turning this off hides the bar and stops retrieval.</summary>
    public bool LyricsEnabled { get; set; } = true;

    /// <summary>是否显示第二行。/ Whether the second row is shown.</summary>
    public bool TwoLineLyricsEnabled { get; set; } = true;

    /// <summary>
    /// 是否也接受浏览器与视频播放器的歌词。原项目用它在"只给音乐播放器取词"与"任何媒体都取词"之间切换；
    /// 精简版保留该字段，因为它参与取词缓存失效判断。
    /// Whether browser and video-player lyrics are also accepted. The original used it to switch between "music players only" and
    /// "any media"; the trimmed build keeps the field because it takes part in lyric-cache invalidation.
    /// </summary>
    public bool AllowBrowserAndVideoLyrics { get; set; } = true;

    /// <summary>第二行来源顺序；null 表示未配置，按默认顺序。/ Second-row source order; null means unconfigured and follows the default order.</summary>
    public LyricsSecondaryLineSettings LyricsSecondaryLine { get; set; } = LyricsSecondaryLineSettings.Default;

    /// <summary>
    /// 是否启用逐字擦亮。有真实逐字时间轴时按字擦亮；只有行级歌词时按整行时长平均推算（v1.4.10 起），
    /// 让每一句都有扫过去的过程，而不是"有的歌擦亮、有的歌整行一次性点亮"。
    /// Whether syllable-level highlighting is enabled. A real word timeline highlights character by character; line-level
    /// lyrics get a wipe spread evenly across the line window (since v1.4.10) so every line sweeps instead of some songs
    /// lighting up all at once.
    /// </summary>
    public bool LyricsSyllableHighlightEnabled { get; set; } = true;

    /// <summary>
    /// 是否在歌词左侧显示歌曲封面。封面来自系统媒体会话（SMTC），不是所有播放器都会提供；
    /// 拿不到封面时 Web 端会退回到"曲名首字母"的色块占位。
    /// Whether to show the track artwork to the left of the lyrics. Artwork comes from the system media session (SMTC) and not
    /// every player provides it; without it the web side falls back to a coloured tile with the title's first letter.
    /// </summary>
    public bool LyricsCoverArtEnabled { get; set; } = true;

    /// <summary>歌词栏在任务栏上的水平停靠位置。/ Horizontal docking position of the lyric bar on the taskbar.</summary>
    public LyricsBarAlignment LyricsBarAlignment { get; set; } = LyricsBarAlignment.Center;

    /// <summary>歌词文本水平对齐。/ Horizontal alignment of lyric text.</summary>
    public LyricsTextAlignment LyricsTextAlignment { get; set; } = LyricsTextAlignment.Center;

    /// <summary>未唱部分的不透明度（百分比）。/ Opacity of the unsung portion, in percent.</summary>
    public int LyricsUnsungOpacityPercent { get; set; } = LyricsUnsungOpacity.DefaultPercent;

    /// <summary>字距（相对字号的百分比）。/ Character spacing as a percent of the font size.</summary>
    public int LyricsCharacterSpacingPercent { get; set; } = LyricsCharacterSpacing.DefaultPercent;

    /// <summary>行距（相对字号的百分比）。/ Line gap as a percent of the font size.</summary>
    public int LyricsLineGapPercent { get; set; } = LyricsLineGap.DefaultPercent;

    /// <summary>是否使用固定栏宽；关闭时栏宽跟随歌词内容。/ Whether the bar uses a fixed width; when off the width follows the lyric content.</summary>
    public bool LyricsFixedWidthEnabled { get; set; }

    /// <summary>固定栏宽（DIP）。/ Fixed bar width in DIP.</summary>
    public int LyricsFixedWidthDip { get; set; } = LyricsFixedWidth.DefaultDip;

    /// <summary>是否丢弃作词、作曲等信息行。/ Whether credit lines such as writer and composer are dropped.</summary>
    public bool LyricsInfoLineFilterEnabled { get; set; } = true;

    /// <summary>启用的歌词来源；null 表示全部启用。/ Enabled lyric sources; null means all of them.</summary>
    public LyricsSourceSettings LyricsSource { get; set; } = LyricsSourceSettings.Default;

    /// <summary>艺术家分隔符，每行一项。/ Artist separators, one per line.</summary>
    public string LyricsArtistSeparators { get; set; } = "/";

    /// <summary>歌词中文转换方向。/ Chinese conversion direction for lyric text.</summary>
    public LyricsChineseConversionMode LyricsChineseConversion { get; set; } = LyricsChineseConversionMode.None;

    /// <summary>
    /// 歌词匹配严格度。它只影响"候选曲目要多像才被采纳"，不影响用哪些来源。
    /// 默认均衡 = 升级前的固定门槛，因此默认行为不变。
    /// Lyric match strictness. It only decides how alike a candidate must be to be accepted; it does not change which sources
    /// are used. The default, balanced, is the fixed threshold used before this setting existed, so the default is unchanged.
    /// </summary>
    public LyricsMatchStrictness LyricsMatchStrictness { get; set; } = LyricsMatchStrictness.Balanced;

    /// <summary>歌词字号（DIP）。/ Lyric font size in DIP.</summary>
    public double LyricsFontSize { get; set; } = 14;

    /// <summary>任务栏歌词栏高度（DIP）。/ Height of the taskbar lyrics bar in DIP.</summary>
    public double LyricsBarHeight { get; set; } = 40;

    /// <summary>自动栏宽的上限（DIP）。/ Upper bound of the automatic bar width in DIP.</summary>
    public double LyricsMaxWidthDip { get; set; } = 640;

    /// <summary>歌词字体族名；null 表示跟随系统消息字体。/ Lyric font family name; null follows the system message font.</summary>
    public string? FontFamily { get; set; }

    /// <summary>
    /// 歌词是否加粗。加粗作用在两行歌词、以及曲目信息块的曲名/歌手那一行上，与网页版的 <c>--font-weight</c> 是同一件事。
    /// Whether the lyrics are bold. It applies to both lyric rows and to the track-info title/artist, and it is the same thing
    /// as the web side's <c>--font-weight</c>.
    /// </summary>
    public bool LyricsFontBold { get; set; }

    /// <summary>
    /// 歌词是否使用斜体。斜体靠字体自身的 italic 外形实现，机器上没装斜体外形时系统会做合成倾斜，仍然可读。
    /// Whether the lyrics use italics. The slant comes from the family's italic face; when a machine has none installed the
    /// system synthesises an oblique, which stays readable.
    /// </summary>
    public bool LyricsFontItalic { get; set; }

    /// <summary>是否在任务栏歌词栏上显示播放进度条。/ Whether a playback progress bar is drawn inside the taskbar bar.</summary>
    public bool LyricsProgressBarEnabled { get; set; } = true;

    /// <summary>
    /// 歌词文字颜色（#RRGGBB 或 #AARRGGBB）。默认白色，因为任务栏在深色主题下是深底；
    /// 浅色主题的用户可以在设置里改成深色。原项目靠采样任务栏背景自动选前景色，精简版交给用户显式指定，行为更可预期。
    /// Lyric text colour (#RRGGBB or #AARRGGBB). White by default because the taskbar has a dark surface under a dark theme;
    /// light-theme users can change it in the settings. The original sampled the taskbar background to pick a foreground
    /// automatically; the trimmed build lets the user state it explicitly, which behaves more predictably.
    /// </summary>
    public string LyricsTextColor { get; set; } = "#FFFFFFFF";

    /// <summary>是否给文字加一圈对比阴影。深色壁纸配浅色任务栏、或壁纸很花时打开它更容易读。</summary>
    public bool LyricsTextShadowEnabled { get; set; } = true;

    /// <summary>
    /// 用哪个引擎画歌词。默认原生：不起浏览器子进程，任务管理器里只有一个进程。
    /// 改成 WebView2 会在下一次重建渲染器时生效（设置窗口保存后重新停靠一次即可）。
    /// Which engine draws the lyrics. Native by default: no browser subprocess, one process in Task Manager. Switching to
    /// WebView2 takes effect the next time the renderer is built, which re-docking from the settings window triggers.
    /// </summary>
    public LyricsRendererEngine LyricsRendererEngine { get; set; } = LyricsRendererEngine.Native;

    /// <summary>
    /// 是否强制让 WebView2 使用软件渲染（<c>--disable-gpu</c>）。只对 <see cref="LyricsRendererEngine.WebView2"/> 有意义。
    /// 程序在检测到 WebView2 子进程反复崩溃时会自动切到软件渲染重试一次；这个开关用于手动固定该模式，
    /// 避免每次启动都先崩一次。虚拟机、远程桌面与部分受限企业终端上通常需要它。
    /// Whether to force WebView2 onto software rendering (<c>--disable-gpu</c>). Only meaningful for
    /// <see cref="LyricsRendererEngine.WebView2"/>. The app automatically retries once in this mode after detecting repeated
    /// WebView2 subprocess crashes; this switch pins the mode so a start does not have to fail first. Virtual machines, remote
    /// desktops, and some locked-down corporate endpoints usually need it.
    /// </summary>
    public bool DisableWebView2Gpu { get; set; }

    /// <summary>
    /// 兼容模式：在软件渲染的基础上再关掉 Chromium 沙箱（<c>--disable-gpu --no-sandbox</c>）。
    /// 虚拟机、远程桌面与受限企业终端上沙箱常常无法初始化，只关 GPU 完全无效（实测 2026-10-14），
    /// 表现为歌词栏位置正确但一片空白。开启后不再走"先崩一次再降级"的过程，启动即可用。
    /// Compatibility mode: disables the Chromium sandbox on top of software rendering
    /// (<c>--disable-gpu --no-sandbox</c>). On virtual machines, remote desktops, and locked-down endpoints the sandbox often
    /// cannot initialize and disabling the GPU alone does nothing (measured 2026-10-14), which shows up as a correctly
    /// positioned but blank lyric bar. Turning this on skips the "crash first, then fall back" cycle so it works from launch.
    /// </summary>
    public bool WebView2CompatibilityMode { get; set; }

    /// <summary>
    /// 上一次启动时真正跑通的 WebView2 降级级别（0 默认 / 1 关 GPU / 2 关 GPU + 关沙箱）。
    /// 这台机器上沙箱起不来，每次启动都要先崩两轮、等约 8 秒才能出歌词；跑通一次就把级别记下来，
    /// 下次直接从该级别开始，不再重复"崩溃—重建"的过程。
    /// 值由程序自己维护，设置界面上的"兼容模式"开关在关闭时会把它清零，给用户一个反悔的入口。
    /// The WebView2 fallback tier that actually worked on the previous start (0 default / 1 no GPU / 2 no GPU + no sandbox).
    /// On this machine the sandbox cannot initialize, so every start used to burn two crash-and-rebuild cycles (~8s) before
    /// lyrics appeared. Remembering the tier that worked skips that entirely on subsequent starts. The value is maintained by
    /// the app itself; unchecking "compatibility mode" in the settings clears it, so the user always has a way back.
    /// </summary>
    public int WebView2RendererTier { get; set; }

    /// <summary>任务栏歌词栏距屏幕左边缘的偏移（DIP）。/ Offset of the taskbar bar from the left edge of the screen, in DIP.</summary>
    public double TaskbarOffsetDip { get; set; } = 0;

    /// <summary>
    /// 这一首确认没有歌词时，是否把曲名与歌手顶到歌词行上（配合封面）。纯音乐、伴奏、无人声曲目都走这条路。
    /// 关掉它只是不再占用歌词行——整条栏仍然留着，只剩封面（开了曲目信息块时还有那一块）；
    /// 无论如何都不会再把栏整个隐掉，否则封面与曲目信息块会被一起淡出，任务栏上什么都看不见。
    /// Whether to promote the title and artist onto a lyric row (alongside the artwork) when a track is confirmed to have no
    /// lyrics. Instrumentals, backing tracks, and vocal-free pieces all take this path. Turning it off only stops using the lyric
    /// row — the bar stays and shows just the artwork (plus the track-info block when enabled); the bar is never hidden here, which
    /// would fade the artwork and the track-info block out with it and leave nothing on the taskbar.
    /// </summary>
    public bool LyricsShowTrackInfoWhenNoLyrics { get; set; } = true;

    /// <summary>
    /// 歌词栏是否响应鼠标：滚轮上下切歌，点击栏上的按钮切歌。
    /// 开启后歌词栏会吃掉它覆盖的那一段任务栏上的鼠标事件（那里的图标点击、右键菜单都不再落到任务栏），
    /// 因此它是一个显式取舍；关掉就回到"点击穿透、纯显示"的行为。
    /// Whether the bar responds to the mouse: the wheel skips tracks, and the buttons on the bar skip tracks. Enabling it makes
    /// the bar swallow mouse events over the taskbar segment it covers (icon clicks and context menus there no longer reach the
    /// taskbar), so it is an explicit trade-off; turning it off restores click-through, display-only behaviour.
    /// </summary>
    public bool LyricsMouseControlEnabled { get; set; } = true;

    /// <summary>
    /// 是否始终显示**曲目信息块**：封面右侧、歌词左侧的一块竖排「曲名 / 歌手」。
    /// 它不占用歌词的两行——歌词始终是"主行当前句 + 第二行下一句/翻译/音译"，所以曲目与歌词可以同时看到。
    /// 关掉它整块不占宽度，歌词回到紧贴封面的默认排布。
    ///
    /// 早先的实现是把曲目信息顶到主行、把当前歌词挤到第二行。那样主行就不再是歌词：逐字擦亮没有时间轴可擦、
    /// 行下标必须钉死成常数、每首歌都白白少看一行歌词。用户要的是"封面右侧能看出在放什么"，
    /// 不是"用曲目信息替换掉歌词"，所以改成这块独立区域。
    /// Whether to always show the **track-info block**: a stacked "title / artist" area between the artwork and the lyrics.
    /// It does not consume either lyric row — the lyrics keep their normal "current line, then next line / translation /
    /// romanization" layout — so track info and lyrics are visible at the same time. Turning it off removes the block and
    /// leaves the lyrics immediately right of the artwork again.
    ///
    /// It used to be implemented by promoting the track info onto the main row and pushing the current lyric down to the
    /// second row. That stopped the main row from being a lyric at all: syllable highlighting had no timeline left to wipe,
    /// the line index had to be pinned to a constant, and every song silently lost one lyric row. The ask was "tell me what is
    /// playing, next to the artwork", not "replace the lyrics with the track info", so it is a dedicated area now.
    ///
    /// This key was called <c>LyricsTrackInfoAlwaysShown</c> while it meant "promote the track info onto the main row".
    /// It was renamed together with that rewrite on purpose: an old settings file carries <c>false</c> for the *old*
    /// question ("do not replace my lyric row"), and reusing the name would let that stale <c>false</c> silently
    /// suppress the new block for everyone who had ever turned the old behaviour off. The old key is simply ignored.
    /// </summary>
    public bool LyricsTrackInfoBlockEnabled { get; set; } = true;

    /// <summary>
    /// 曲目信息块的宽度（DIP）。0 表示自适应：按曲名与歌手两行里更长的那条量出来，最长不超过
    /// <see cref="LyricsTrackInfoWidthPolicy.AutoMaximumDip"/>。给一个正值就是用户手动定的宽度，文字排不下时按省略号截断。
    /// 手动宽度是有意留的：曲名长短差得很远时，自适应会让整条栏随歌曲忽宽忽窄，手动定宽可以把栏宽钉死。
    /// Width of the track-info block in DIP. Zero means automatic: measured from whichever of the title/artist rows is longer,
    /// capped at <see cref="TrackInfoWidthLimits.AutoMaximumDip"/>. A positive value is a width the user fixed, and text that
    /// does not fit is ellipsized. The manual width is deliberate: with wildly different title lengths the automatic width makes
    /// the whole bar jump between widths from song to song, while a fixed one pins it down.
    /// </summary>
    public double LyricsTrackInfoWidthDip { get; set; }

    /// <summary>
    /// 归一化：把越界或不合理的取值收敛回合法区间，保证渲染层拿到的一定是可用值。
    /// Normalizes out-of-range or nonsensical values so the rendering layer always receives usable ones.
    /// </summary>
    public AppSettings Normalize()
    {
        LyricsSecondaryLine = LyricsSecondaryLine.Normalize();
        LyricsSource = LyricsSource.Normalize();
        LyricsArtistSeparators ??= string.Empty;
        LyricsUnsungOpacityPercent = LyricsUnsungOpacity.Normalize(LyricsUnsungOpacityPercent);
        LyricsCharacterSpacingPercent = LyricsCharacterSpacing.Normalize(LyricsCharacterSpacingPercent);
        LyricsLineGapPercent = LyricsLineGap.Normalize(LyricsLineGapPercent);
        LyricsFixedWidthDip = LyricsFixedWidth.Normalize(LyricsFixedWidthDip);
        if (!Enum.IsDefined(LyricsTextAlignment)) LyricsTextAlignment = LyricsTextAlignment.Center;
        if (!Enum.IsDefined(LyricsChineseConversion)) LyricsChineseConversion = LyricsChineseConversionMode.None;
        if (!Enum.IsDefined(LyricsMatchStrictness)) LyricsMatchStrictness = LyricsMatchStrictness.Balanced;
        if (!Enum.IsDefined(LyricsRendererEngine)) LyricsRendererEngine = LyricsRendererEngine.Native;
        LyricsFontSize = double.IsFinite(LyricsFontSize) ? Math.Clamp(LyricsFontSize, 8, 48) : 14;
        LyricsTrackInfoWidthDip = LyricsTrackInfoWidthPolicy.NormalizeManual(LyricsTrackInfoWidthDip);
        LyricsBarHeight = double.IsFinite(LyricsBarHeight) ? Math.Clamp(LyricsBarHeight, 24, 96) : 40;
        LyricsMaxWidthDip = double.IsFinite(LyricsMaxWidthDip) ? Math.Clamp(LyricsMaxWidthDip, 120, 2000) : 640;
        TaskbarOffsetDip = double.IsFinite(TaskbarOffsetDip) ? Math.Clamp(TaskbarOffsetDip, 0, 2000) : 0;
        WebView2RendererTier = Math.Clamp(WebView2RendererTier, 0, 2);
        return this;
    }

    /// <summary>深拷贝，用于设置窗口的"取消"语义。/ Deep copy, so the settings window can offer a working "cancel".</summary>
    public AppSettings Clone() => new()
    {
        LyricsEnabled = LyricsEnabled,
        TwoLineLyricsEnabled = TwoLineLyricsEnabled,
        AllowBrowserAndVideoLyrics = AllowBrowserAndVideoLyrics,
        LyricsSecondaryLine = LyricsSecondaryLine,
        LyricsSyllableHighlightEnabled = LyricsSyllableHighlightEnabled,
        LyricsTextAlignment = LyricsTextAlignment,
        LyricsUnsungOpacityPercent = LyricsUnsungOpacityPercent,
        LyricsCharacterSpacingPercent = LyricsCharacterSpacingPercent,
        LyricsLineGapPercent = LyricsLineGapPercent,
        LyricsFixedWidthEnabled = LyricsFixedWidthEnabled,
        LyricsFixedWidthDip = LyricsFixedWidthDip,
        LyricsInfoLineFilterEnabled = LyricsInfoLineFilterEnabled,
        LyricsSource = LyricsSource,
        LyricsArtistSeparators = LyricsArtistSeparators,
        LyricsChineseConversion = LyricsChineseConversion,
        LyricsMatchStrictness = LyricsMatchStrictness,
        LyricsFontSize = LyricsFontSize,
        LyricsBarHeight = LyricsBarHeight,
        LyricsMaxWidthDip = LyricsMaxWidthDip,
        FontFamily = FontFamily,
        LyricsFontBold = LyricsFontBold,
        LyricsFontItalic = LyricsFontItalic,
        LyricsProgressBarEnabled = LyricsProgressBarEnabled,
        LyricsTextColor = LyricsTextColor,
        LyricsTextShadowEnabled = LyricsTextShadowEnabled,
        LyricsRendererEngine = LyricsRendererEngine,
        DisableWebView2Gpu = DisableWebView2Gpu,
        WebView2CompatibilityMode = WebView2CompatibilityMode,
        WebView2RendererTier = WebView2RendererTier,
        LyricsShowTrackInfoWhenNoLyrics = LyricsShowTrackInfoWhenNoLyrics,
        LyricsMouseControlEnabled = LyricsMouseControlEnabled,
        LyricsTrackInfoBlockEnabled = LyricsTrackInfoBlockEnabled,
        LyricsTrackInfoWidthDip = LyricsTrackInfoWidthDip,
        LyricsCoverArtEnabled = LyricsCoverArtEnabled,
        LyricsBarAlignment = LyricsBarAlignment,
        TaskbarOffsetDip = TaskbarOffsetDip
    };
}
