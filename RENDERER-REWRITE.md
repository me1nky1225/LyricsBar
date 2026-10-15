# 渲染层重写交接 / Rendering-layer rewrite handoff

> **✅ 已完成（v1.4.0，2026-10-15）。** 本文档描述的改造已经落地：新增 `ILyricsRenderer` 接口与原生渲染器
> `Classes/Services/Lyrics/Presentation/LyricsNativeRenderer.cs`，设置项 `LyricsRendererEngine` 默认走原生；
> 不再有任何本程序名下的 `msedgewebview2.exe`。新增 `--render-preview` 离屏自检用于逐项对拍观感
> （见 `SOFTWARE-STATUS.md` 3.5 节）。
> 与本文档原计划的两处偏差：**网页引擎没有删掉**（保留为设置里的可选项，用于对拍与兜底，因此 WebView2 包引用、
> `Web/Lyrics/**`、两个降级开关都还在）；**频谱场景没有实现**（宿主的 `Present` 恒发 `scene = "lyrics"`，那套代码
> 在这个精简版里从来没有被走到过）。下面保留原文，作为这次改造的动机与验收依据。
>
> **§1.2(a) 那个"打包方式值 ~100 MB"的猜测已被证实并落地（v1.4.1）。** 根因不是自包含，而是
> `EnableCompressionInSingleFile`：压缩包在启动时整包解压进私有内存。关掉之后任务管理器里的"内存"
> 从 155MB 降到 **57MB（播放中）/ 28MB（空闲）**，代价是 exe 从 75MB 变成 178MB。
> 四种打包方式的实测对比见 `SOFTWARE-STATUS.md` 3.6 节。§1.1 的 865MB 至此降到 57MB。
>
> **任务**：把歌词渲染层从 WebView2 换成原生实现，把后台内存从 ~865 MB 压到 ~200 MB 量级，观感与功能不许退化。
> **English summary**: replace the WebView2-based lyric rendering layer with a native one to cut the app's memory from ~865 MB (466 MB host + 399 MB WebView2 process tree) down to the ~200 MB range, without regressing anything the user can see. §1 has the measurements, §3 the host↔page contract, §4 the exact seam to swap, §6 the verification commands.
> **基线**：v1.3.3，提交 `b2c5253`，工作树干净。本文所有数字都是 2026-10-15 在作者机器（2560×72 任务栏，PerMonitorV2 屏）上实测的。

---

## 1. 为什么换：内存实测（任务的全部动机）

### 1.1 现在的占用（播放中，正在显示歌词）

| 进程 | 个数 | 工作集 | 归属判据 |
|---|---|---|---|
| `LyricsBar.exe`（宿主） | 1 | 466.0 MB（私有 295.3 MB） | — |
| `msedgewebview2.exe` | 6 | 399.1 MB | 命令行 `--user-data-dir=C:\Users\meinky\AppData\Local\LyricsBar\EBWebView` |
| **合计** | **7** | **≈ 865 MB** | |

> 口径说明：工作集是各进程相加，共享页（同一份 Chromium 二进制与字体映射）会被重复计入一点，
> 所以"合计 865 MB"略偏高；对"用户看到任务管理器里有一堆进程吃内存"这个感受来说，
> 这个口径才是对的。私有字节是更严格的指标，宿主 295.3 MB 是实测值，浏览器子进程的私有字节没有逐个记。
>
> 另有一组 6 个 `msedgewebview2.exe`（合计 293.7 MB）的 `--user-data-dir` 指向
> `...\Packages\MicrosoftWindows.Client.CBS_cw5n1h2txyewy\LocalState\EBWebView` —— 那是 **Windows 外壳自己的** WebView2，
> 不属于本程序。统计时务必按 user-data-dir 过滤，别把它算进来，更别去动它。

### 1.2 两个与渲染层无关、可以立刻拿到的便宜

**(a) 打包方式值 ~100 MB。** 同一天、同配置（Release / win-x64 / `--diagnose` 无窗口、不起浏览器）实测峰值工作集：

| 打包方式 | 峰值工作集 |
|---|---|
| framework-dependent，非单文件 | **166.2 MB** |
| self-contained 单文件 + `EnableCompressionInSingleFile`（现在的 `dist`） | **267.2 MB** |

也就是说 `LyricsBar.csproj` 里那组 `PublishSingleFile` / `IncludeNativeLibrariesForSelfExtract` /
`EnableCompressionInSingleFile` 本身就值约 100 MB 工作集（代价换来"只有一个 exe"）。命令见 §6.1。
注意：framework-dependent 要求目标机装了 .NET 10 桌面运行时（本机有）。

**(b) 闲置裁剪只裁到了宿主，没裁到浏览器。** `Classes/Services/WorkingSetTrimmer.cs` 已经在做：
闲置（无播放会话或已暂停）持续超过 `IdleThreshold = 1 分钟` 后调用一次
`EmptyWorkingSet(GetCurrentProcess())`（`:73-77`），且只裁一次、恢复播放后重新武装（`:70`）。
但它**只作用于本进程**：466 MB 里浏览器那 399 MB 一个字节都没还。若要在大改之前先压一压，
把同样的 `EmptyWorkingSet` 施于那 6 个 WebView2 子进程（按 `--user-data-dir` 认领）即可，
代价是重新播放时首帧会卡一下。

### 1.3 还没测的东西（接手后第一步建议先补）

本机没装 `dotnet-counters` / `dotnet-gcdump`，所以**托管堆与 native 的拆分是空白**：

```powershell
dotnet-counters monitor --process-id <pid> --counters System.Runtime[gc-heap-size,working-set]
```

或者在 `--diagnose` 路径上加一行 `GC.GetTotalMemory(true)` 的日志。理由：宿主那 466 MB 里，
WPF + WebView2 合成控件的 native 映射、内存扫描缓冲、托管堆各占多少，现在只能靠猜；
不知道这个数，就没法判断"只换渲染层"的收益天花板在哪里。

---

## 2. 这个程序是什么（现状快照）

- **一句话**：把当前播放歌曲的歌词显示在 Windows 任务栏上（一个悬浮于任务栏的无边框分层窗口），
  歌词来自网易云音乐 / QQ 音乐 / 酷狗 / 汽水 / LRCLIB。
- **目标框架** `net10.0-windows10.0.19041.0`；WPF（`UseWPF`）+ WinForms 托盘（`UseWindowsForms`，只为 `TrayIconService`）；
  `app.manifest` 声明 **PerMonitorV2** DPI —— 任务栏停靠按物理像素定位，必须保留。
- **依赖 4 个包**（`src/LyricsBar/LyricsBar.csproj`）：
  `Lyricify.Lyrics.Helper` 0.2.0（多源搜索 + LRC/YRC/QRC 解析）、
  `F23.StringSimilarity` 7.0.1（匹配打分）、
  **`Microsoft.Web.WebView2` 1.0.4191.47（本次要拿掉的）**、
  `OpenccNetLib` 1.7.0（繁简转换）。
- **源码地图**（`src/LyricsBar/`）：
  - `Views/TaskbarLyricsWindow.xaml{,.cs}` —— **任务栏窗口本体**（后置代码约 1400 行）：停靠、DPI、悬停命中、
    三个切歌按钮、进度条、歌词区，以及 §4 的全部接缝。
  - `Views/SettingsWindow.xaml{,.cs}` —— Win11 风格设置界面。
  - `Classes/Services/Lyrics/` —— `LyricsService`（取词总入口）、`Providers/`（各源）、`Parsing/`、`Cache/`、
    `Presentation/`（**渲染之前的纯逻辑层**，含 §3.4 的时长策略与 §4.3 的生命周期策略）。
  - `Classes/Services/` —— `SmtcMediaReader`（SMTC + 本地计时兜底）、`MediaTransportService`、`CoverArtLoader`、
    `TaskbarDockService`、`TrayIconService`、`WorkingSetTrimmer`、`AppLogService`、
    `NeteasePositionService` + `Classes/Interop/ProcessMemory.cs`（**网易云只读内存通道，刚做完，别动**）。
  - `Web/Lyrics/` —— **现在这一层**（见 §3.1）。
  - `Classes/Settings/AppSettings.cs` —— 全部设置项（渲染相关的见 §3.4）。
- **文档**：`README.md`（用户向，含"改了原项目哪些代码"）、`SOFTWARE-STATUS.md`（现状 / 限制 / **验证记录表**，
  每次改动都要往里加一行）、`THIRD-PARTY-NOTICES.md`（依赖声明，删 WebView2 时要同步）。
- **发布**：`dist/`（已被 gitignore，当前 `LyricsBar.exe` = 78,470,237 字节）；
  历史版本归档在 `releases/LyricsBar-v<版本>-<说明>.exe`（v1.0 / v1.1.0 / v1.3.0 / v1.3.1 各一份，v1.3.2、v1.3.3 尚未放入）。

### 2.1 数据是怎么流到渲染层的

```
SMTC / 网易云内存通道          →  MediaSnapshot（曲目、位置、时长、播放态）
  SmtcMediaReader / NeteasePositionService
        ↓
LyricsCoordinator              →  取词、换行、时间轴对齐、繁简转换
        ↓
Classes/Services/Lyrics/Presentation/**  →  纯逻辑：算出 LyricsPresentationFrame（当前行/下一行/行内进度/逐字进度/译文…）
        ↓
TaskbarLyricsWindow            →  把"一帧"和"外观"交给渲染器（本文档要换掉的那一层）
```

**这条链路与渲染层无关，重写不要碰。** 渲染器只吃三样东西：一帧歌词、一份外观（`LyricsWebStyle`）、
外加封面与曲目信息两条独立消息。

---

## 3. 现在的渲染层（WebView2）与它的契约

### 3.1 文件与装载方式

| 文件（`src/LyricsBar/Web/Lyrics/`） | 行数 | 角色 |
|---|---|---|
| `index.html` | 108 | DOM 骨架（骨架里每个 id 都在 `app.js:1-26` 被取用） |
| `style.css` | 623 | 全部观感：磨砂、字号、颜色、动画时长 |
| `state.js` | 18 | 共享状态槽 |
| `spacing.js` | 58 | 行距 / 字距换算 |
| `presentation.js` | 431 | `PresentationPlanner` / `PresentationCoordinator` / `TransitionDispatcher`（纯逻辑、无 DOM） |
| `app.js` | 2220 | DOM 与动画状态机：逐字擦亮、换行、水平滚动、封面淡入淡出、频谱 |

六个文件都以 `EmbeddedResource` 打进程序集（`LyricsBar.csproj` 里的 `<EmbeddedResource Include="Web\Lyrics\*.html|css|js" />`），
运行时由 `Presentation/LyricsWebViewRenderer.cs:485-510` 的 `BuildDocument()` 拼成一个**自包含 HTML**，
`NavigateToString` 装载：不落磁盘、不发网络请求。

### 3.2 宿主 → 页面协议（重写后必须等价地喂给新渲染器）

下发形式是注入脚本：`window.taskbarLyrics?.receive({version:1,type,payload})`
（`LyricsWebViewRenderer.cs:479-483`，JSON 用 camelCase）。五种消息：

| type | 生产者 | 载荷字段 | 语义要点 |
|---|---|---|---|
| `style` | `ApplyStyle`（`:146-169`） | `fontFamily` `latinFontFamily` `cjkFontFamily` `fontSize` `fontWeight` `characterSpacingPercent` `lineGapPercent` `textAlignment` `showCover` `coverSize` `coverGap`(8) `coverCornerRadius`(6) `layoutScalePercent`(100) `primaryColor` `secondaryColor` `translationColor` `wordScanBaseColor` `wordScanOverlayColor` `surfaceColor`(transparent) `surfaceShadow`(none) `textShadow` | 相同外观重复下发会被 `Equals(_lastStyle, style)` 短路掉（`:140`），重写要保留这个短路，否则每次媒体快照都会走一遍重排版 |
| `cover` | `SetCover`（`:185-191`） | `dataUri` `fallbackText` `fallbackColor` `trackId` | `dataUri` 为空 = 没有封面，退回"曲名首字母 + `fallbackColor`"色块；`trackId` 用于丢弃换歌竞态下的旧封面 |
| `trackInfo` | `SetTrackInfo`（`:215-220`） | `title` `artist` `width` | **宽度由宿主体量好下发**，页面照用（两边不各量一遍才不会对不齐）；两条文本都为空 = 收起整块且不留间距 |
| `overlay` | `SetOverlay`（`:239`） | `visible` | 悬停磨砂的显隐。磨砂**只能由页面自己做**（WPF 的 `Effect` 作用不到合成控件），页面用"满宽白膜 + 内容 `filter:blur`"，宽度自己跟窗口走 |
| `lyrics` | `Present`（`:257-272`） | `current` `next` `progress` `currentLineIndex` `trackId` `isPureMusic` `isPlaying` `wordScanProgress` `currentTranslation` `nextTranslation` `translationMode` `animateTransition` `scene` | 不可见帧推空文本（清空两行、不留上一首残影）；`animateTransition=false` 用于"歌词栏重新出现"这类不该有过渡的场景 |

**投递模型**（`StartDrain` / `DrainAsync`，`:400-477`）：每类**只保留最新一条**（渲染慢时不回放过期帧），
固定顺序 `style → cover → trackInfo → overlay → lyrics`（封面与曲目信息先于歌词落地，
避免换歌瞬间"新歌词先到、封面后到"那一跳）；任务栏不可见时 `PauseDelivery()`（`:277`）只留最新值，
恢复时 `ResumeDelivery()`（`:284`）先补样式再补歌词。

### 3.3 页面里发生了什么（重写必须实现的观感清单）

权威实现是 `app.js` + `presentation.js` + `style.css`；这里列出来是为了让重写者知道**哪些行为不能丢**，
括号里是常量名/函数名，照名字去源码里抄数值：

1. **两行歌词**：当前行 + 下一行。当前行 `currentLineRestOpacity = 0.98`；次要行 `secondaryOpacity = 0.72`，
   并随行内进度在 0.58~0.74 之间微调（`updateSecondaryOpacity`）。
2. **换行动画**：`transitionDurationMs = 560`（译文对 `translationPairTransitionDurationMs = 760`）；
   离开行淡到 `leavingLineOpacity = 0.16`；整行按 `linePitchPx` 上移（`setTrackOffset`）；
   新行与即将成为当前行的行做交叉淡入（`getFadeOutEase` / `getFadeInEase`）。
3. **逐字擦亮**：`wordScanProgress`（0~1）驱动当前行逐字点亮（`setLineWordScanProgress`）；
   暂停时冻结（`wordScanFreezeState`），恢复播放后用 180 ms 追上真实进度（`wordScanResumeCatchUpDurationMs`）；
   未唱部分 `wordScanBaseColor`、已唱部分 `wordScanOverlayColor`。
4. **超长行水平滚动**：行文本超出可视宽度时，扫描头推到 `horizontalScrollAnchorRatio = 0.65` 处开始滚动（`updateLineHorizontalScroll`）。
5. **物理像素对齐**：所有位移都过 `snapToPhysicalPixel` / `alignDownToPhysicalPixel`（用 `devicePixelRatio`），
   否则高分屏上字会发虚。
6. **封面**：双 `img` 交叉淡入淡出（`coverSwapDelayMs = 180`、`coverSwitchMinVisibleMs = 420`、
   `coverGeneration` 丢弃过期帧），无封面时退回首字母色块。
7. **曲目信息块**：封面右侧的曲名 / 歌手，宽度由宿主给（`trackInfoGapPx = 8`，`app.js:31`）。
8. **频谱模式**：无歌词 / 纯音乐时切 `scene = "spectrum"`，21 根（可 8~32）柱子按 `--i` 依次律动（`spectrumTuning`），
   进出各有过渡（`startSpectrumEntryRollTransition` / `startSpectrumExitTransition`）。
9. **"正在检索歌词…"**：取词中当前行显示 `SEARCHING_TEXT`，并有专门的切换过渡（`trackSwitchSearchTransitionActive`）。
10. **磨砂浮层**：`#overlayBackdrop` 盖住 `.lyrics-pane`（`inset:0`），配合内容层的 `filter:blur`。
11. **排版**：`--layout-padding-inline`（4 DIP）、封面 36 DIP、`coverGap = 8`、曲目信息间距 8 等写在 `style.css` 的变量里；
    `lyricsTextAlignments` 支持 `Left / Center / Right / Split`（`Split` 时译文按 `left` 起排）。
12. **减少动效**：`prefersReducedMotion()`（`app.js:127`）直接落位，不做过渡。

### 3.4 渲染层要尊重的设置项（`Classes/Settings/AppSettings.cs`）

`LyricsEnabled` `TwoLineLyricsEnabled` `LyricsSecondaryLine` `LyricsSyllableHighlightEnabled` `LyricsCoverArtEnabled`
`LyricsBarAlignment` `LyricsTextAlignment` `LyricsUnsungOpacityPercent` `LyricsCharacterSpacingPercent`
`LyricsLineGapPercent` `LyricsFixedWidthEnabled` `LyricsFixedWidthDip` `LyricsInfoLineFilterEnabled`
`LyricsFontSize` `LyricsBarHeight` `LyricsMaxWidthDip` `FontFamily` `LyricsProgressBarEnabled` `LyricsTextColor`
`LyricsTextShadowEnabled` `TaskbarOffsetDip` `LyricsShowTrackInfoWhenNoLyrics` `LyricsMouseControlEnabled`
`LyricsTrackInfoBlockEnabled` `LyricsChineseConversion` `LyricsSource` `LyricsArtistSeparators`

另有三个是 WebView2 专用的，重写后要么改造成"渲染器降级级别"、要么删除：
`WebView2RendererTier`、`WebView2CompatibilityMode`、`DisableWebView2Gpu`（用途见 §4.3）。

---

## 4. 宿主侧接缝（替换点）

以下行号全部在 `src/LyricsBar/Views/TaskbarLyricsWindow.xaml.cs`（除非另行注明）。

### 4.1 `LyricsWebViewRenderer` 的公开面（新渲染器应逐一对应）

```csharp
// src/LyricsBar/Classes/Services/Lyrics/Presentation/LyricsWebViewRenderer.cs:42
public sealed class LyricsWebViewRenderer(WebView2CompositionControl webView,
                                          string? additionalBrowserArguments = null) : IDisposable
{
    public event EventHandler? Ready;                 // 文档就绪，可以接收消息
    public event EventHandler? Failed;                // 导航/脚本失败或子进程反复崩溃，由宿主决定重建
    public bool IsReady { get; }
    public Task<bool> InitializeAsync();
    public void ApplyStyle(LyricsWebStyle style);
    public void SetCover(string dataUri, string fallbackText, string fallbackColor, string trackId);
    public void SetTrackInfo(string title, string artist, double widthDip);
    public void SetOverlay(bool visible);
    public void Present(LyricsPresentationFrame frame, bool animateTransition);
    public void PauseDelivery();                      // 任务栏不可见时只保留最新消息
    public void ResumeDelivery();
    public Task<bool> TrySuspendAsync();              // WPF 已隐藏后尽力挂起
    public void Resume();
    public void Dispose();
}
```

`LyricsWebStyle`（`:11-27`）：`FontFamily` `LatinFontFamily` `CjkFontFamily` `FontSize` `FontWeight`
`CharacterSpacingPercent` `LineGapPercent` `TextAlignment` `PrimaryColor` `SecondaryColor` `TranslationColor`
`WordScanBaseColor` `WordScanOverlayColor` `TextShadow` `ShowCover` `CoverSizeDip`。

**设计建议**：`Classes/Abstractions/` 目前只有 `ILyricsProvider.cs`，**没有渲染器接口**。
建议第一步先抽出 `ILyricsRenderer`（把上面这些成员搬进去，去掉 WebView2 类型），
让原生实现与旧 WebView2 实现**并存一段时间**，用设置项或编译开关切换 —— 风险最小，也方便逐项对拍观感。

### 4.2 宿主在哪里调用它

| 位置 | 内容 |
|---|---|
| `:133-151` | `_renderer` 与其世代号、失败标志、降级级别、已应用外观的字段 |
| `:668-673` | 唯一的帧入口：`canPresent` 判 `IsReady` 后 `_renderer!.Present(_frame, animateTransition: true)` |
| `:1028-1032` | `SetOverlay(shown)`（悬停显隐，与 `TransportPanel.Tag` 同步） |
| `:1037-1107` | `EnsureRendererAsync()`：创建 WebView2 控件 → `LyricsWebViewRenderer` → `InitializeAsync` → 挂 `Ready`/`Failed` → 超时重试 |
| `:1117-1182` | 失败与降级：先按当前级别重建一次，仍失败就升一级（`ResolveBrowserArguments()`），并把可用级别写回设置（`:1152`） |
| `:1221-1292` | 推送 `ApplyStyle` / `SetCover` / `SetTrackInfo` |
| `:1334-1344` | `BuildStyle(settings)` → `new LyricsWebStyle(...)` |
| `:1398-1400` | 关闭时释放 |
| `:729-733` | `TransportPanel.Margin = new Thickness(ResolveLyricsPaneLeftDip(settings), 0, LayoutPaddingInlineDip, 0)` —— **"三个按钮看起来居中在磨砂底板里"全靠这个数** |
| `:827` | `ResolveLyricsPaneLeftDip(AppSettings)`：`排版内边距 + (有封面 ? 封面尺寸 + 8 : 0) + (有曲目信息 ? 曲目信息宽度 + 8 : 0)`，与 `style.css` 的排版同源，不依赖任何 IPC |
| `:902` | `UpdateProgress()`：进度条（`Grid.Row=1` 的 `ProgressTrack`/`ProgressFill`），显示条件含 `Duration > 0` |
| `:960-1013` | 按钮区的可用性（`TransportPanel.Tag`） |

### 4.3 生命周期与降级（已有的省内存手段，别误删）

- `Presentation/LyricsWebViewLifetimePolicy.cs`：`Resolve(hostLoaded, lyricsEnabled, graphicsDisabled, mediaConnected, lyricLineCount, hasRenderer)`
  → `None / Create / Retain / ReleaseAfterGrace / ReleaseNow`；无歌词宽限期 `NoLyricsGracePeriod = 30 秒`。
  **也就是说：没歌词时浏览器 30 秒后会被整个释放。** 原生渲染不需要"起一个引擎"，这套策略可以简化甚至退休，
  但删之前先看清所有调用点。
- `Classes/Services/Lyrics/WebView2GraphicsFaultPolicy.cs`：判断显卡相关异常（虚拟机/远程会话里 GPU 进程反复崩溃，
  靠 `--disable-gpu` 降级重建恢复）。

### 4.4 窗口本体（重写要接着用的部分）

`Views/TaskbarLyricsWindow.xaml`：`WindowStyle="None"` + `AllowsTransparency="True"`（分层窗口）；
`RootGrid` 两行 —— 第 0 行是歌词区（`Grid x:Name="LyricsWebViewHost"`，**WebView2 由代码创建后挂进来**，见 `:93-94`）
并叠加 `TransportPanel`（`:131-154`：三个 34 DIP 的 `Segoe MDL2 Assets` 图标按钮），第 1 行是 `ProgressTrack`（`:157-158`）。
悬停命中走**窗口消息钩子**（`WM_MOUSEMOVE` / `WM_MOUSELEAVE`）而不是 WPF 的 `IsMouseOver`，
原因是 WebView2 区域的命中测试不可靠（`:107-127` 的注释）。
换成原生渲染后这条理由消失，可以改回 WPF 命中测试，**但要顺手验证悬停显隐没退化**。

---

## 5. 重写路线（建议，不是命令）

1. **先补测量**（§1.3）：托管堆 / native 拆分 + "不起浏览器时宿主的真实基线"。没有这两个数，收益天花板无从判断。
2. **先拿便宜的那 100 MB**（§1.2a）：对比 framework-dependent 与关掉压缩的单文件，权衡体积 / 启动 / 内存后再定发布方式。
3. **抽 `ILyricsRenderer`**：把现有 WebView2 实现原样包进去，先跑通"接口化但行为完全不变"这一版（出问题好定位）。
4. **写原生渲染器**，建议顺序：静态两行 + 换行 → 逐字擦亮 → 水平滚动 → 封面淡入淡出 → 频谱 → 磨砂浮层。
   每一步都对着 `app.js` 的常量（§3.3 给了名字），并在 `SOFTWARE-STATUS.md` 的验证表里加一行。
5. **逐项对拍**：新旧两个渲染器同时可用（设置项切换），同一首歌来回切、暂停/恢复/拖动/换歌各走一遍。
6. **再删 WebView2**：包引用、`LyricsBar.csproj` 末尾删 XML 文档的 `Target`、`WebView2GraphicsFaultPolicy`、
   三个 WebView2 设置项、`Web/Lyrics/` 六个文件（**确认新实现完全覆盖观感之后再删**）、`README.md` / `SOFTWARE-STATUS.md` /
   `THIRD-PARTY-NOTICES.md` 里的相关段落。

**技术取向（供参考，别盲从）**：本项目现有风格是**不引新依赖**。逐字擦亮可以用两段文本叠加裁剪，
或用带 `LinearGradientBrush` 的 `Foreground` 扫光；水平滚动用 `TranslateTransform` 动画；
封面淡入淡出用两个 `Image` + `DoubleAnimation`；频谱用 N 个 `Rectangle` + `CompositionTarget.Rendering`
（注意 60 Hz 下的 CPU 占用）；所有位移记得对齐物理像素。**不要**为了省事再引入一个新的渲染引擎或 UI 框架。

---

## 6. 可复现的命令与验收

### 6.1 命令

```powershell
# 构建（Debug，快）
dotnet build src\LyricsBar\LyricsBar.csproj

# 发布（Release 单文件，现状）
dotnet publish src\LyricsBar\LyricsBar.csproj -c Release -r win-x64 -o dist

# 发布（framework-dependent，用于内存对比）
dotnet publish src\LyricsBar\LyricsBar.csproj -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=false -o <临时目录>

# 无窗口跑一遍：取一次词、锁一次网易云内存位置，然后退出（结果写进日志）
dist\LyricsBar.exe --diagnose

# 日志（UTF-8）
notepad "$env:LOCALAPPDATA\LyricsBar\lyricsbar.log"

# 统计本程序的内存（按 user-data-dir 认领 WebView2 子进程，别把 Windows 外壳的算进来）
Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" |
  Where-Object { $_.CommandLine -match 'AppData\\Local\\LyricsBar' } |
  ForEach-Object { Get-Process -Id $_.ProcessId } |
  Measure-Object WorkingSet64 -Sum
```

### 6.2 不许退化的清单（v1.3.3 已实测通过，重写后逐条复测）

- **停靠与尺寸**：日志出现 `[Taskbar] 已停靠到任务栏 \\.\DISPLAY1（2560×72）` 与
  `[Taskbar] 歌词栏位置：Center，左边界 …px，宽度 …px`。
- **取词与显示**：`[Lyrics] 取词结束: source=… status=Available`、`歌词栏显示：「…」（行下标 n）`。
- **三个按钮**：悬停显示、居中共磨砂底板（曾实测偏心 **0.0 px**）、放大到 **34 DIP**、上一首 / 下一首 / 播放暂停均生效。
- **进度条**：`LyricsProgressBarEnabled` 打开时可见并随位置推进；网易云把时长报成 0 时用歌词末行推算刻度
  （`Presentation/LyricsDurationPolicy.cs`）。
- **网易云位置**（**与渲染层无关，重写后必须仍然出现**）：
  `[Media] 网易云内存通道已锁定 N 个播放位置地址。` + `[Media] 已改用网易云进程内存中的播放位置：x 秒。`
- **换歌**：无残影、封面 / 曲目信息 / 歌词同时更新、不闪回上一首。
- **闲置与释放**：无歌词 30 秒后释放渲染资源；闲置 1 分钟后
  `[Memory] 程序已闲置超过 1 分钟，将闲置工作集交还操作系统。`
- **内存目标**：播放中总工作集降到 **200 MB 量级**（宿主 + 新渲染层），且**不再有任何 `msedgewebview2.exe`**。

---

## 7. 环境与工具坑（已踩过，别再踩）

- 本机 `pwsh` 实际是 **Windows PowerShell 5.1**；用写文件工具生成的 `.ps1` 若含中文又没有 BOM，
  `& script.ps1` 会解析失败（`字符串缺少终止符`）。脚本内容用 ASCII，或写成带 BOM 的 UTF-8。
- 日志是 UTF-8；直接在控制台 `Select-String` 中文会乱码 —— 落成文件再用编辑器 / 读文件工具看。
- `dotnet publish` 之前先关掉正在运行的 `dist\LyricsBar.exe`，否则文件被占用。
- `--diagnose` 是"无窗口跑一遍然后退出"，适合脚本化验证，会写日志。
- 本机 WebView2 运行时是 **155.0.4283.45**；`dotnet-counters` / `dotnet-gcdump` 都没装。
- 屏幕环境：`\\.\DISPLAY1` 上 2560×72 的任务栏，PerMonitorV2。

---

## 8. 明确不在本次范围内（别顺手改坏）

- `Classes/Services/NeteasePositionService.cs` + `Classes/Interop/ProcessMemory.cs`（网易云只读内存位置通道，刚做完并实测通过）
- `Classes/Services/Lyrics/**` 的取词 / 解析 / 匹配 / 繁简转换
- `Classes/Services/SmtcMediaReader.cs`、`MediaTransportService`、`TaskbarDockService`、`TrayIconService`、`SettingsWindow`
- 鼠标滚轮切歌、悬停命中钩子、任务栏停靠与 DPI 策略

---

*作者：上一轮负责的 Agent（2026-10-15，v1.3.3 / `b2c5253`）。结论对应的实测命令都记在 `SOFTWARE-STATUS.md` 的验证表与"限制"一节。*
