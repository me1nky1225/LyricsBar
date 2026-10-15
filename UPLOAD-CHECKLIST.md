# 上传到 GitHub 前要做的事 / Upload checklist

本文件是给**仓库维护者**看的操作清单，不是给使用者看的。归档目录里其它文件都是要提交的内容，本文件可以提交，也可以留着不上传。

---

## 一、归档包里有什么、没有什么

**有**（共 99 个文件，1,197,938 字节；不含 `docs/github-upload-manifest.csv` 自身时为 98 个文件、1,183,442 字节）：仓库的 86 个受版本控制的文件（源码、XAML、歌词前端、图标、`LICENSE`、`THIRD-PARTY-NOTICES.md`、`SOFTWARE-STATUS.md`、`RENDERER-REWRITE.md`）、本目录重写的 `.gitignore`、新增的 `.gitattributes`（统一按 LF 存取，避免自动换行转换在 diff 里刷出整文件改动）、`README.md`（英文）、`README.zh-CN.md`（中文）、`CHANGELOG.md`、`HANDOFF.md`、设置窗口自检工具 `tools/SettingsRoundTrip`，以及 `docs/settings-preview.png` 与 `docs/github-upload-manifest.csv`。完整清单（含每个文件的字节数与 SHA256）见 `docs/github-upload-manifest.csv`。构成：`src/` 84 个 = 72 个 `.cs` + 3 个 `.xaml` + 6 个歌词前端资源 + `LyricsBar.csproj` / `app.manifest` / `LyricsBar.ico`；`tools/` 2 个；`docs/` 2 个；根目录 11 个。

> **归档在本文件写下之后刷新过三次（同日，当前版本 v1.4.3）**：① "依次亮起失效"的修复与 `--render-preview` 编号重排（13 张图改成 `01`–`12` 连续不重复）；② **「依次亮起」整体回退**（连同 `tools/CascadeProbe` 一起删除）并新增「文字颜色」的 9 色常用颜色色板；③ **色板也整体回退**（v1.4.3，`ColorPresets` / `BuildColorPresets` / `ParsePresetColor` 与探针里的色板用例全部删除，「文字颜色」回到只有一个自由输入框）。本目录已按最终状态（v1.4.3）重出：`HANDOFF.md`、`SOFTWARE-STATUS.md`、两份 README、`CHANGELOG.md`、`src/` 全树与清单都是最后一次回退**之后**的版本。文件数与字节数以本段为准；第 6 节末尾那张表记的是**首次**归档时的核对。

> 归档目录里的 `README.md` 是**新写的英文版**，和原目录里那份中文 README 不是同一个文件；中文说明现在是 `README.zh-CN.md`。两者互为语言入口。

**没有**（故意排除，都有理由）：

| 排除项 | 体积 | 理由 |
|---|---|---|
| `dist/` | 178MB | 自包含单文件 exe，`.gitignore` 已忽略；要发就发到 Release，不要进版本库 |
| `releases/` | 298MB | 历史版本的 exe 归档，`.gitignore` 已忽略 |
| `src/**/bin`、`src/**/obj`、`tools/**/bin`、`tools/**/obj` | 287MB | 编译产物，`.gitignore` 已忽略 |
| `.preview/` | 0.7MB | `--render-preview` 的离屏输出，随时可重新生成，`.gitignore` 已忽略 |
| `build.log`、`publish*.log` | 2KB | 本次会话的临时构建日志 |

> `docs/settings-preview.png` **要一起提交**：中英文 README 都引用了它，不提交首页图片会裂。

---

## 二、推到 GitHub（三种情况选一种）

### 情况 A：仓库还没有，直接沿用现有的 git 历史（最省事）

归档目录里**没有** `.git`（只含要提交的文件）。想保留 master 分支上已有的 10 条提交，就用原项目目录推：

```powershell
cd E:\PcApp\WorkBuddy\2026-10-14-20-18-19\LyricsBar
git add -A
git commit -m "Release v1.4.3: revert the text-colour palette, keep the preview frame renumbering"
git remote add origin https://github.com/<你的账号>/LyricsBar.git
git branch -M main          # 可选：GitHub 默认分支叫 main
git push -u origin main
```

### 情况 B：用归档目录当全新仓库（干净历史，推荐公开项目）

```powershell
cd E:\PcApp\WorkBuddy\2026-10-14-20-18-19\LyricsBar-github-upload
git init
git add -A
git commit -m "LyricsBar v1.4.3: minimal taskbar lyrics for Windows, natively rendered"
git branch -M main
git remote add origin https://github.com/<你的账号>/LyricsBar.git
git push -u origin main
```

### 情况 C：先在 GitHub 网页上建仓库（带 README 的那种）

建完仓库后把远程加上再推。**注意**：网页建的仓库已经有一条提交，直接推会被拒，先拉回来合并一次：

```powershell
git pull --rebase origin main
git push -u origin main
```

---

## 三、关于提交历史的两点提醒

1. **现有 10 条提交是"先基线、后逐版本"的中文提交信息**（首条是 `v1.0 基线：只保留任务栏歌词显示…`），
   历史里没有原作者仓库的提交，是原项目目录里重新 init 的。沿用没问题；想要更干净的对外历史就走情况 B。
2. **`master` / `main` 二选一**。GitHub 新建仓库默认 `main`，本文档按 `main` 写；用 `master` 就把命令里的
   `main` 全部换成 `master`。

---

## 四、建仓库时的填写建议

| 字段 | 建议 |
|---|---|
| 仓库名 | `LyricsBar` |
| 描述 | `Minimal taskbar lyrics for Windows 10/11 — natively rendered, no embedded browser.` |
| 可见性 | 公开（`LICENSE` 是 MIT；若要私有，注意 `LruCache.cs` 的 GPL 冲突在再分发时才要求处理） |
| 初始化 | **不要**勾 "Add a README"、不要选 .gitignore 模板、不要选 License 模板 —— 仓库里都带了，勾了反而要先合并 |
| Topics | `windows`, `taskbar`, `lyrics`, `wpf`, `dotnet`, `smtc`, `karaoke`, `netease-cloud-music` |

---

## 五、发 Release（当前版本 v1.4.3）

> 线上已经发过 `v1.4.2`（标题 `LyricsBar v1.4.2 — 文字颜色常用色板`，附件 186,193,559 字节的那份 exe）。
> v1.4.2 的色板已在 v1.4.3 回退，因此**再发一个 v1.4.3**，不要覆盖 v1.4.2 的 tag。

1. Tag 名 `v1.4.3`，目标选 `main`。
2. 标题：`LyricsBar v1.4.3 — 回退文字颜色色板`。
3. 正文直接抄 `CHANGELOG.md` 里 1.4.3 那一段。
4. 附件上传 `dist/LyricsBar.exe`（186,189,463 字节，文件版本 1.4.3.0）。
   GitHub 单个 Release 附件上限 2GB，178MB 没问题。
5. **不要**把 exe 提交进仓库（`.gitignore` 已挡住 `dist/` 与 `releases/`）。

> 更早的历史版本 exe 在本地 `releases/` 目录里（v1.0 / v1.1.0 / v1.3.0 / v1.3.1，各约 78MB）。
> 想让 Release 页完整，就按同样方式逐个补建 tag 并上传；不补也不影响使用。

---

## 六、推之前最后核对（1 分钟）

```powershell
cd E:\PcApp\WorkBuddy\2026-10-14-20-18-19\LyricsBar-github-upload
git status --short                      # 应该干净
(Get-ChildItem -Recurse -Force -File).Count                   # 应为 99
Test-Path dist, releases, .preview      # 应该全是 False
Select-String -Path README.md,README.zh-CN.md -Pattern 'docs/settings-preview.png'   # 应有命中
```

如果 `git status` 里冒出 `dist/`、`releases/`、`bin/`、`obj/`、`.preview/`，说明 `.gitignore` 没生效，
先停下来查 `.gitignore` 的内容，**不要**直接 `git add -A` 提交上去。

### 归档时已经替你做过的核对（2026-10-15，色板回退落地后重跑）

| 核对项 | 结果 |
|---|---|
| 归档里的受控文件与原项目目录**逐字节一致** | 86 个受控文件逐个比 SHA256：**0 处差异**；只有 `.gitignore` 是有意改写的。另做了一次"回退残留"扫描：`src/` 全树（`.cs` / `.js` / `.html` / `.css`）已无 `Cascade` / `nextWordScanProgress` / `incomingLineScanText` / `ColorPreset` / `ParsePresetColor` 等已回退符号 |
| `git init` + `git add -A` 试跑 | 干净暂存，索引里**没有** `dist/`、`releases/`、`.preview/`、`bin/`、`obj/`、`*.log` |
| 目录里是否混入二进制 / 构建产物 | `dist`、`releases`、`.preview`、`bin`、`obj`、`build.log`、`publish*.log` 全部不存在（`tools/` 下只有 `.csproj` 与 `Program.cs`，`bin`/`obj` 未复制） |
| Markdown 结构（代码块闭合、表格列数、相对链接与图片目标） | md 全部通过，`docs/settings-preview.png` 与各文档互链目标均存在 |
| 源码规模 | 72 个 `.cs`、3 个 `.xaml`、6 个歌词前端资源文件 |
| 自检工具可用 | `tools/SettingsRoundTrip` 读写往返通过：两个下拉框（严格度 / 第二行顺序）改选后输入框 / 内存设置 / `settings.json` 三处一致，`RESULT: PASS`（色板用例已随 v1.4.3 回退删除） |
