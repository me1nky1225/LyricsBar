# LyricsBar 工作交接文档 / Handoff

> 生成时间：2026-10-15
> 当前版本：1.4.3（`src/LyricsBar/LyricsBar.csproj` `<Version>1.4.3</Version>`）

---

## 一、本次会话已完成的工作

### 1. 原生歌词渲染器（v1.4.0 主线）

按 `RENDERER-REWRITE.md` 把歌词渲染层从 WebView2 换成了原生 WPF 自绘：

- **新增 `ILyricsRenderer` 接口**（`Classes/Services/Lyrics/ILyricsRenderer.cs`）：统一原生与 WebView2 两套实现的公开面。
- **新增 `LyricsNativeRenderer`**（`Classes/Services/Lyrics/Presentation/LyricsNativeRenderer.cs`，~1100 行）：
  - 两行歌词、换行上滚（单行 560ms / 译文对 760ms）
  - 逐字擦亮（两遍绘制 + 渐变不透明度遮罩）
  - 超长行按 65% 锚点水平滚动
  - 封面交叉淡入淡出 + 首字母色块
  - 曲目信息块
  - 悬停磨砂（原生 `BlurEffect`）
  - 四种对齐（含左右分离）
  - 物理像素对齐（`SnapToPhysicalPixel`）
- **宿主接入**：`TaskbarLyricsWindow` 按 `AppSettings.LyricsRendererEngine` 选择渲染器，默认原生。
- **内存优化**：`LyricsBar.csproj` 关闭 `EnableCompressionInSingleFile`，实测空闲 **~28MB**、播放中 **~57MB**（之前压缩单文件是 112–148MB）。

### 3. 「文字颜色」常用颜色色板：加了又回退（v1.4.2 加，v1.4.3 已整体回退）

**背景**：用户要求"在文字颜色那里添加几种常用颜色"。

**做过什么（v1.4.2）**：`Views/SettingsWindow.xaml` 外观区颜色输入框下面加一行「常用颜色」，`Views/SettingsWindow.xaml.cs` 里用 `ColorPresetPanel` 铺出 **9 个色块**——纯白 `#FFFFFF` / 米白 `#FFF6E5` / 浅金 `#FFE082` / 天蓝 `#8FD3FF` / 青绿 `#8CE8C8` / 樱粉 `#FFB3C7` / 浅灰 `#C9CDD4` / 深灰 `#6B7280` / 纯黑 `#000000`；点一下写进 `ColorBox`、即时生效并存盘。验证沿用独立探针 `tools/SettingsRoundTrip`：9 个色块逐个 `Measure`/`Arrange`/渲染并**读回中心像素**，9 个色值逐字节一致，再对「天蓝」发一次真实 `Click`，核对 `ColorBox.Text` / 内存设置 / `settings.json` / `ColorPreview.Background` 四处一致，`RESULT: PASS`。

**为什么回退**：用户看过真机截图后明确要求「回退到没有添加几种常用文字颜色的版本」，不需要这组色块。

**回退范围（v1.4.3）**：

- `Views/SettingsWindow.xaml`：删掉「常用颜色」标签行、`ColorPresetPanel`、以及"点一下常用颜色即可填入…"的 `CardHint`。
- `Views/SettingsWindow.xaml.cs`：删掉 `ColorPresets` 常量表、`BuildColorPresets()`、`ParsePresetColor()`、构造函数里的 `BuildColorPresets();` 调用及随之无用的 `using`。
- `tools/SettingsRoundTrip/Program.cs`：删掉 `VerifyColorPresets()` 与 `MeasureButtonColor()`（下拉框往返那部分探针保留，仍 `RESULT: PASS`）。
- **保留**：`ColorBox` 本身（自由填 `#RRGGBB` 或颜色名）与 `ColorPreview` 预览块，与 v1.4.1 及更早版本一致；`RenderPreview.cs` 的帧编号重排与色板无关，也保留。

### 4. 左右分离「依次亮起」的尝试与回退（v1.4.2，已回退）

**背景**：用户要求左右分离下逐字亮起时**依次亮起**——擦亮头从主行一路走到第二行，而不是把下一句搬到左上角再滚一次（改之前一换行就把第二行整句搬上去，同一句歌词要亮两遍）。

**做过什么**：帧里加 `CascadeHighlight` / `NextWordScanProgress` 两个字段，原生渲染器命中时不做上滚（`ApplyInPlace`）而把第二行的扫描目标设成它自己的轨迹，网页渲染器同帧取消在途动画直接落位；`--render-preview` 加过 `12-cascade-split` 帧，另建 `tools/CascadeProbe`（32 项断言）钉住投影器的判定。

**为什么回退**：

1. 投影器第一版把判定写在**下一句**挑出来的来源上（`nextSecondary`），只要下一句带译文/音译就整帧自关。用户那首歌第二行顺序是「音译 → 翻译 → 下一句」，于是该功能几乎从不生效——用户实测反馈就是"无效"。
2. 改判**当前句**的来源（`currentSecondary`）后断言探针 32/32 通过，但**用户真机再看仍然是"不行"**。教训：探针只能证明"帧里的判定算对了"，证明不了两帧之间擦亮头的观感；这类连续位移的观感改动，没有真机确认就不算验证通过——而真机确认只能由用户做。

**回退范围**（`git checkout HEAD --` 对下列文件是权威基线，注意 `Presentation/LyricsNativeRenderer.cs` 当时还未入版本库、是手工回退的）：

- `Presentation/LyricsPresentationProjector.cs`：删掉两个帧字段及其计算，`Hidden` / `Project` / `BuildTrackInfoFrame` 三处构造恢复。
- `Presentation/LyricsNativeRenderer.cs`：删掉 `IsSplitCascade` 与 `PresentCore` 里的落位分支、`ApplyInPlace` 与上滚途中的第二行扫描同步。
- `LyricsWebViewRenderer.cs`、`Web/Lyrics/presentation.js`、`Web/Lyrics/app.js`、`Web/Lyrics/index.html`、`Web/Lyrics/style.css`：`git checkout HEAD --` 整体还原（`index.html` 的 `#incomingLineScanText`、`app.js` 的 `setSecondaryWordScanProgress`/`clearSecondaryWordScanProgress` 与 `applyFrame` 的落位分支、`style.css` 里被放宽的 `.next-line.word-scanning` 选择器都随之消失）。
- `RenderPreview.cs`：删掉 `12-cascade-split` 帧与 `RenderCascadeAsync`；**保留**编号重排这一项修复。
- `tools/CascadeProbe/`：整个删除。

**保留下来的一项**：离屏自检的编号重排（原先 `08`–`13` 两套重复编号，`12` 还被两张图共用）。回退后是 **12 张图、编号 01–12 连续无重复**：`01` 居中 / `02` 左右分离 / `03` 译文对 / `04` 超长行滚动 / `05` 字距 / `06` 无封面靠左 / `07` 省略号 / `08-overlay-before` + `08-overlay` 磨砂前后 / `09-roll-before` + `10-roll-mid` + `11-roll-after` 上滚三帧。

### 5. 两个 UI BUG 修复

- **设置窗口下拉框点不开**：自定义 ComboBox 模板漏了 `ToggleButton`（WPF 靠它切换 `IsDropDownOpen`），补上后字体/对齐/中文转换/停靠位置四个下拉框正常展开。
- **左右分离时新行先出现在右侧**：`Split` 对齐下 `.next-line.promoting` 没提前改成靠左，导致升格行在 560ms 上滚全程贴在右边。补了 CSS 规则与原生渲染器的对等处理。

### 6. AF Media Bar 歌词匹配分析 + 移植

用户反馈 AF Media Bar 歌词匹配更准确、能选第二句优先级。分析结论：

| 差异点 | AF Media Bar | LyricsBar（改之前） |
|---|---|---|
| 网易云歌曲 ID | 从进程内存读取（`NetEaseMemoryReader`） | **永远是 null**，没有网易云媒体源 |
| 匹配严格度 | 有「宽松/适中/严格」三档设置 | **只有一档**（固定 55 分门槛） |
| 第二行顺序 | 用户可调整翻译/音译/下一句的优先级 | **硬编码**（翻译→音译→下一句） |

已落地的改进：

- **网易云队列反查服务** `NeteaseQueueService.cs`：
  - **不碰进程内存**，只读网易云本地队列文件（`%LOCALAPPDATA%\NetEase\CloudMusic\WebData\file\playingList` 与 `fmPlay`）
  - 按曲名（含别名）+ 歌手反查歌曲 ID
  - 命中后填充 `LyricsRequest.NetEaseSongId`，走网易云「按 id 精确取词」路径（100 分命中）
  - 已接入 `LyricsCoordinator`，换歌时自动尝试
- **匹配严格度**：
  - 新增 `LyricsMatchStrictness` 枚举（`Lenient`/`Balanced`/`Strict`）
  - 新增 `LyricsMatchStrictnessPolicy`：三档对应不同的最低匹配分与首选源采纳门槛
  - `LyricsService`、`LyricsSearch`、`QQMusicLyricsProvider` 都已接入
- **第二行优先级**：
  - 设置窗口新增「第二行优先」下拉框：翻译优先 / 音译优先 / 下一句优先
  - 映射到 `LyricsSecondaryLineSettings.Order` 数组
- **诊断增强**：`--diagnose` 现在会报告「网易云本地队列」命中状态

---

## 二、文件改动清单

### 新增文件

| 文件 | 说明 |
|---|---|
| `src/LyricsBar/Classes/Services/Lyrics/ILyricsRenderer.cs` | 渲染器接口 + `LyricsWebStyle` |
| `src/LyricsBar/Classes/Services/Lyrics/Presentation/LyricsNativeRenderer.cs` | 原生 WPF 渲染器 |
| `src/LyricsBar/Classes/Services/Lyrics/LyricsMatchStrictnessPolicy.cs` | 匹配严格度策略 |
| `src/LyricsBar/Classes/Services/NeteaseQueueService.cs` | 网易云队列反查 |
| `src/LyricsBar/RenderPreview.cs` | `--render-preview` 离屏自检（12 张图，编号 01–12 连续无重复） |
| `tools/SettingsRoundTrip/` | 独立 net10 控制台探针：真实实例化 `SettingsWindow` 驱动下拉框往返（见 §三.2） |

### 修改文件（核心）

| 文件 | 改动 |
|---|---|
| `LyricsBar.csproj` | 版本 1.4.3；关闭 `EnableCompressionInSingleFile` |
| `AppSettings.cs` | 新增 `LyricsRendererEngine`、`LyricsMatchStrictness` |
| `LyricsRetrievalPolicy.cs` | 严格度阈值策略 |
| `LyricsService.cs` | 应用严格度到取词流程 |
| `LyricsSearch.cs` | 严格度影响最低匹配分 |
| `LyricsCoordinator.cs` | 接入 `NeteaseQueueService` |
| `TaskbarLyricsWindow.xaml(.cs)` | 宿主支持双渲染器切换 |
| `SettingsWindow.xaml(.cs)` | 新增「歌词渲染」「匹配严格度」「第二行优先」三个下拉框（v1.4.2 的「文字颜色」常用颜色色板已于 v1.4.3 回退，见 §一.3） |
| `Diagnostics.cs` | 新增网易云队列诊断 |
| `RenderPreview.cs` | 把 `08`–`13` 的编号按输出顺序重排成 01–12 连续无重复（v1.4.2；曾新增 `12-cascade-split` 帧，已随回退删除） |
| `style.css` | 修复 `Split` 下 `.next-line.promoting` 对齐 |

### 文档更新

| 文件 | 改动 |
|---|---|
| `SOFTWARE-STATUS.md` | 新增 3.5/3.6 节，更新版本历史、验证记录 |
| `README.md` | 更新内存数据、系统要求、排查指南 |
| `RENDERER-REWRITE.md` | 标记已完成，保留原文作为依据 |
| `THIRD-PARTY-NOTICES.md` | 说明 WebView2 变为可选依赖 |

---

## 三、待验证 / 待收尾

### 1. 网易云队列反查的端到端验证 —— ✅ 已通过（2026-10-15 19:31，真实播放）

真实播放 **爱一点 — 莫艳琳** 时，生产日志出现：

```
已载入网易云播放队列: 593 个候选（跳过 3 条无标题或 ID 的条目）
取词结束: source=Netease score=100 / 39 行
```

同期歌词栏显示的就是这首歌自己的歌词（「看地上有个人 还睡不着」正是其第 4 个带时间戳的行）。`score=100` 只有**精确 ID 路径**会给出，搜索路径的 source 是 `NeteaseSearch`，所以反查命中链路闭环成立。

**两个要注意的地方（都会让人误判）：**

1. **`网易云本地队列: 命中歌曲 ID=xxx，将走精确取词路径` 这行在生产版日志里不出现** —— 它目前只写进 `--diagnose` 的裁决输出。所以生产日志里"看不到这行"不代表没命中，判据应看 `source=Netease` + `score=100`。
2. **`--diagnose` 本身验证不了这条路径**：它的自检取词把 `NeteaseSongId` 显式置为 `null`，走的是搜索兜底。因此同一天 18:44:09 与 19:31:32 两次自检都取到**别人的歌**（实测把「爱一点」搜成了周杰伦的《爱一点》），这是自检构造决定的，不是回归；诊断输出里的「命中歌曲 ID=276035」来自**播放队列反查**，也不能当作搜索路径可取到同名歌的证据（同名不同曲才是此处的关键点）。要单独跑诊断验证，得给它传 `songId`。

若要复现：独立复核队列文件的命令见 SOFTWARE-STATUS.md 验证记录（`FileShare.ReadWrite` 共享读 `playingList`，按 `id` 命中 `276035 / 爱一点 / 莫艳琳`；590 条 + 跳过 3 条 = 593）。

### 2. 设置窗口新控件的实际点击测试 —— 大部分已覆盖，剩一步人工

- 已做（2026-10-15 19:35，独立宿主真实实例化 `SettingsWindow` 做读写往返）：`StrictnessCombo` 改选「宽松」→ 落盘 `LyricsMatchStrictness: "Loose"`；改回「严格」→ 落盘回 `Strict`；两个下拉框实际宽度 326.0 / 189.0px 且开合前后不变，确认是真实可视控件而非空占位。
- 已做（2026-10-15 21:40，同一个探针扩了色板用例，「常用颜色」9 个色块逐个渲染并读回中心像素，9 个色值逐字节一致；点「天蓝」后 `ColorBox.Text` / 内存设置 / `settings.json` / `ColorPreview.Background` 四处都是 `#8FD3FF`，`RESULT: PASS`）。**该色板用例已随 v1.4.3 的回退一起删除**，现在探针只做下拉框往返。
- 剩一步：**真实鼠标展开下拉列表**没做 —— 本机沙箱下宿主用 `SendInput` 投递报 `ACCESS_DENIED`。人工点一次即可确认（v1.4.0 已修过"自定义 ComboBox 模板漏 ToggleButton 导致点不开"的根因）。

### 3. 发布产物的完整性 —— 已复核

最后一次 `dotnet publish` 报了一个 SDK 内部错误（`MSB4018`），但 `dist/LyricsBar.exe` 文件已写出（186MB）。

**已复核（2026-10-15）**：删掉 `dist/` 重跑发布，**`MSB4018` 未再出现**（此后每次发布都干净），产物与"关闭压缩后体积"一致。`dist\LyricsBar.exe --render-preview` 每次都把 12 张 PNG 全部出图（01 居中 / 02 左右分离 / 03 译文对 / 04 超长行滚动 / 05 字距 / 06 无封面靠左 / 07 省略号 / 08-overlay-before + 08-overlay 磨砂前后 / 09–11 上滚三帧），无空白帧。该项可以视为关闭。

### 4. 左右分离「依次亮起」—— 已回退，不再做

2026-10-15：用户实测两轮都判"不行"，该功能**整体回退**（详见 §一.4 的复盘）。回退后不再有"差一次真机确认"这件事。留下的教训写进了 §一.4：这类连续位移动效的观感改动，自动化探针证明不了观感，必须由用户真机拍板；既然用户不要了，就不要再提。

### 5. 可能的后续优化

| 方向 | 说明 |
|---|---|
| 网易云进程内存读 ID | 如果队列反查命中率不够高，可以移植上游 `NetEaseMemoryReader.cs`（需要维护内存偏移，网易云更新后可能失效） |
| 更细粒度的第二行顺序 | 现在是三档预设；AF Media Bar 支持拖拽排序，实现更复杂 |
| 歌词来源各自独立严格度 | 目前所有来源共用同一档严格度；AF Media Bar 支持按来源分别设置 |

---

## 四、快速启动命令

```bash
# 构建
cd "E:/PcApp/WorkBuddy/2026-10-14-20-18-19/LyricsBar"
DOTNET_ROOT="$PWD/../.dotnet" ../.dotnet/dotnet.exe build src/LyricsBar/LyricsBar.csproj

# 发布
cd "E:/PcApp/WorkBuddy/2026-10-14-20-18-19/LyricsBar"
rm -rf dist
DOTNET_ROOT="$PWD/../.dotnet" ../.dotnet/dotnet.exe publish src/LyricsBar/LyricsBar.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist

# 离屏自检（不用启动界面，输出 12 张 PNG 到 .preview/）
cd "E:/PcApp/WorkBuddy/2026-10-14-20-18-19/LyricsBar"
DOTNET_ROOT="$PWD/../.dotnet" ./dist/LyricsBar.exe --render-preview "E:/PcApp/WorkBuddy/2026-10-14-20-18-19/LyricsBar/.preview"

# 诊断（查看当前播放曲目与取词链路）
cd "E:/PcApp/WorkBuddy/2026-10-14-20-18-19/LyricsBar"
DOTNET_ROOT="$PWD/../.dotnet" ./dist/LyricsBar.exe --diagnose

# 日志位置
%LOCALAPPDATA%\LyricsBar\lyricsbar.log
```

---

## 五、关键约定（改代码前先看）

- **不要动**：`Classes/Services/NeteasePositionService.cs` + `Classes/Interop/ProcessMemory.cs`（网易云播放位置的只读内存通道，实测通过）；`Classes/Services/Lyrics/**` 的取词/解析/匹配/繁简转换。
- 加一个新设置的三步：`AppSettings` 加属性（含默认值 + `Normalize()` + `Clone()`）→ `SettingsWindow.xaml(.cs)` 加控件并在 `LoadFromSettings`/`ApplyFromUi` 读写 → 消费方读 `SettingsManager.Current`。
- 设置窗口里的控件要能被 `tools/SettingsRoundTrip` 驱动到：给它起一个 `x:Name`，并按 `LoadFromSettings` / `ApplyFromUi` 的双向语义接上。改完控件跑一次该探针（它会在结束时还原用户的 `settings.json`）。
- 构建用项目自带的隔离 SDK：`DOTNET_ROOT=$PWD/../.dotnet ../.dotnet/dotnet.exe build src/LyricsBar/LyricsBar.csproj`
