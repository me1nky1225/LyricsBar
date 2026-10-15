# 第三方组件与许可

## NuGet 依赖

| 包 | 许可 | 用途 |
|---|---|---|
| Lyricify.Lyrics.Helper | MIT | 歌词检索与解析（LRC / YRC / QRC / LYS） |
| Microsoft.Web.WebView2 | 专有（可再分发） | 歌词渲染宿主——**v1.4.0 起默认不再使用**，只在设置里把「歌词渲染」切回「网页（WebView2）」时才启用；默认路径是原生自绘，不起任何浏览器子进程 |
| OpenccNetLib | Apache-2.0 | 繁简转换 |
| F23.StringSimilarity | MIT | 曲目元数据匹配打分 |

> 注意：歌词前端（`src/LyricsBar/Web/Lyrics/**`）与 WebView2 宿主是**逐字移植**自 AF Media Bar 的部分。
> v1.4.0 新增的原生渲染器（`Classes/Services/Lyrics/Presentation/LyricsNativeRenderer.cs`）是**本项目自己写的**，
> 它按 `app.js` / `style.css` 的常量重写了观感，不含上游代码。

## 源码来源

本项目基于 [AF Media Bar](https://github.com/Fervent-Tempo/AF-Media-Bar)（MIT，Copyright (c) 2026 AmorFate）
commit `917ebf4a478bfc5f981c0dfb0ef499a45ef4304f` 衍生，复用了其中的歌词取词解析链路与歌词前端。

AF Media Bar 的任务栏停靠实现又移植自
[FluentFlyout](https://github.com/unchihugo/FluentFlyout)（GPL-3.0-or-later）。

## ⚠️ 需要留意的许可冲突

`src/LyricsBar/Classes/Utils/LruCache.cs` 头部标注为：

```
Copyright (c) 2024-2026 The FluentFlyout Authors
SPDX-License-Identifier: GPL-3.0-or-later
```

也就是说，本仓库里**混入了一份 GPL-3.0-or-later 的源码文件**，而仓库整体声明为 MIT。
上游 AF Media Bar 也是同样的状态。这不是本衍生项目引入的问题，但如果你打算**再分发**本项目，
需要自行处理这一冲突，可选做法：

1. 用自己实现的 LRU 缓存替换 `LruCache.cs`（它只被歌词转换缓存与取词缓存用到，替换成本很低）
2. 或把整个项目的许可改为 GPL-3.0-or-later

自用不受影响。

## 内容版权

在线取到的**歌词内容版权属于各音乐平台**（网易云音乐、QQ 音乐、酷狗音乐、汽水音乐、LRCLIB 等）。
本项目只做检索与显示，不存储、不再分发歌词内容。商用请自行评估。
