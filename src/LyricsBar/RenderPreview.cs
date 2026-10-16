using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LyricsBar.Classes.Services.Lyrics;
using LyricsBar.Classes.Utils;

namespace LyricsBar;

/// <summary>
/// 命令行离屏预览：把原生歌词渲染器画成一叠 PNG，用来在没有第二块屏、也不方便一直盯着任务栏的情况下核对观感。
/// 用法：<c>LyricsBar.exe --render-preview [输出目录]</c>，不传目录就写进 <c>%TEMP%\lyricsbar-preview</c>。
/// Command-line offscreen preview: draws the native lyrics renderer into a set of PNGs so the look can be checked without a
/// second monitor or staring at the taskbar. Usage: <c>LyricsBar.exe --render-preview [output directory]</c>; without a
/// directory it writes to <c>%TEMP%\lyricsbar-preview</c>.
///
/// 为什么需要它：换成原生自绘之后，排版、逐字擦亮、水平滚动、换行上滚全部是手写的，改坏了不会有任何编译期信号，
/// 只有人眼看着任务栏才知道。这组图把每种情形各截一张（居中 / 左右分离 / 译文 / 超长行滚动 / 字距 / 截断省略号 /
/// 悬停磨砂 / 换行上滚的三帧），改渲染层之后跑一遍对着图看，比反复开关程序快得多。
/// Why it exists: with the native drawing surface, layout, the syllable scan, horizontal scrolling, and the line roll are all
/// hand-written, so a regression produces no compile-time signal — only a human watching the taskbar would notice. This
/// produces one image per case (centred, left/right split, translation, overlong-line scrolling, character spacing, trimming,
/// hover frost, and three frames of a roll), which is far faster to review after a rendering change than restarting the app
/// over and over.
/// </summary>
internal static class RenderPreview
{
    private static string _directory = string.Empty;

    private static LyricsWebStyle BaseStyle { get; } = new(
        "Microsoft YaHei UI",
        "Microsoft YaHei UI",
        "Microsoft YaHei UI",
        14,
        400,
        0,
        0,
        "Center",
        "#FFFFFFFF",
        "rgba(255, 255, 255, 0.68)",
        "rgba(255, 255, 255, 0.68)",
        "rgba(255, 255, 255, 0.45)",
        "#FFFFFFFF",
        "0 0 3px rgba(0, 0, 0, 0.85)",
        true,
        34);

    /// <summary>执行预览；返回 true 表示调用方应直接退出而不启动界面。/ Runs the preview; true means the caller should exit without starting the UI.</summary>
    internal static bool TryRun(string[] args)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, "--render-preview", StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return false;

        _directory = args.Length > index + 1 ? args[index + 1] : Path.Combine(Path.GetTempPath(), "lyricsbar-preview");
        Directory.CreateDirectory(_directory);
        _ = RunAsync();
        return true;
    }

    private static async Task RunAsync()
    {
        try
        {
            RenderStatic("01-center", BaseStyle, 700, 48,
                ("In My Head", "Lil Tjay"),
                Frame("And girl, I don't know what to call it (Call)", "我不知道该叫它什么", false, 0.5, 0.35));

            RenderStatic("02-split", BaseStyle with { TextAlignment = "Split" }, 700, 48,
                ("In My Head", "Lil Tjay"),
                Frame("多少人为生命在努力勇敢的走下去", "我们是不是该知足", false, 0.5, 0.35));

            RenderStatic("03-translation", BaseStyle, 640, 48,
                ("インドア系ならトラックメイカー", "Yunomi"),
                Frame("NEWハウス エレクトロハウス", "my house 是纸房子", true, 0.4, 0.6));

            RenderStatic("04-long-scroll", BaseStyle, 420, 48,
                null,
                Frame("这是一行特别特别长的歌词用来验证超长行在可视宽度不够时会水平滚动起来", "下一句", false, 0.5, 0.8));

            RenderStatic("05-letter-spacing", BaseStyle with { CharacterSpacingPercent = 20 }, 700, 48,
                null,
                Frame("字距拉开二十个百分点", "Spaced out", false, 0.5, null));

            RenderStatic("06-no-cover-left", BaseStyle with { TextAlignment = "Left", ShowCover = false }, 600, 48,
                null,
                Frame("靠左对齐，无封面", "left aligned", false, 0.5, 0.2));

            RenderStatic("07-trimmed", BaseStyle, 300, 48,
                null,
                Frame("这一行太长了放不下所以应该出现省略号而不是换行", "下一句", false, 0.5, null));

            // 纯音乐 / 无歌词：可见但两行都空。这一帧是"栏必须留着"的视觉证据——它应该只剩封面（与开了的曲目信息块），
            // 而不是整条栏淡出。
            // Pure music / no lyrics: visible with both rows empty. This frame is the visual evidence that the bar has to stay — it
            // should show just the artwork (and the track-info block when enabled) instead of fading out entirely.
            RenderStatic("08-pure-music", BaseStyle with { TextAlignment = "Left" }, 300, 48,
                null,
                Frame(string.Empty, string.Empty, false, 0, null));

            // 编号按"输出顺序"排，并且必须互不重复：每个编号就是一个文件名，
            // 撞号会让后写的一张覆盖先写的一张，预览看起来会凭空少图。
            // Numbers follow the output order and must be unique: each number is one file name, and a collision makes the later
            // capture overwrite the earlier one, so the preview silently ends up with a missing image.
            await RenderOverlayAsync();
            await RenderRollAsync();

            Log.Info("Preview", $"离屏预览已输出到 {_directory}");
        }
        catch (Exception ex)
        {
            Log.Error("Preview", "离屏预览失败。", ex);
        }
        finally
        {
            System.Windows.Application.Current?.Shutdown();
        }
    }

    private static LyricsPresentationFrame Frame(
        string current,
        string next,
        bool translationMode,
        double progress,
        double? wordScan,
        int lineIndex = 7) => new(
            true,
            "track-1",
            lineIndex,
            current,
            translationMode ? string.Empty : next,
            translationMode ? next : string.Empty,
            string.Empty,
            translationMode,
            progress,
            wordScan,
            true);

    private static (LyricsNativeRenderer Renderer, Grid Host) Build(
        LyricsWebStyle style,
        int width,
        int height,
        (string Title, string Artist)? trackInfo)
    {
        var renderer = new LyricsNativeRenderer();
        renderer.ApplyStyle(style);
        if (trackInfo is { } info)
            renderer.SetTrackInfo(info.Title, info.Artist, 140);
        renderer.SetCover(string.Empty, "A", "hsl(210, 42%, 36%)", "track-1");

        var host = new Grid
        {
            Width = width,
            Height = height,
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20))
        };
        host.Children.Add(renderer);
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();
        return (renderer, host);
    }

    private static void RenderStatic(
        string name,
        LyricsWebStyle style,
        int width,
        int height,
        (string Title, string Artist)? trackInfo,
        LyricsPresentationFrame frame)
    {
        var (renderer, host) = Build(style, width, height, trackInfo);
        renderer.Present(frame, animateTransition: false);
        host.UpdateLayout();
        Save(host, width, height, name);
    }

    /// <summary>
    /// 悬停磨砂：淡入本身是 140ms 的动画，所以必须等它走完再截图，否则截到的还是没上雾的那一帧。
    /// Hover frost: the fade itself is a 140 ms animation, so the capture has to wait for it or it grabs the pre-frost frame.
    /// </summary>
    private static async Task RenderOverlayAsync()
    {
        var (renderer, host) = Build(BaseStyle, 700, 48, ("In My Head", "Lil Tjay"));
        renderer.Present(
            Frame("And girl, I don't know what to call it (Call)", "我不知道该叫它什么", false, 0.5, 0.35),
            animateTransition: false);
        host.UpdateLayout();
        Save(host, 700, 48, "09-overlay-before");

        renderer.SetOverlay(LyricsOverlayRegion.Lyrics);
        await Task.Delay(260);
        host.UpdateLayout();
        Save(host, 700, 48, "10-overlay");

        // 指针挪到曲目信息那一块上：这一块被糊掉，歌词恢复清晰。两张图摆在一起就是"只糊指针所在的那一块"的证据。
        // The pointer moves onto the track-info part: that part is blurred while the lyrics return to normal. The two captures
        // side by side are the evidence for "only the part under the pointer is blurred".
        renderer.SetOverlay(LyricsOverlayRegion.Info);
        await Task.Delay(260);
        host.UpdateLayout();
        Save(host, 700, 48, "14-info-overlay");
    }

    /// <summary>
    /// 起一次真实的换行上滚，并在中途与收尾各截一帧。动画由渲染循环（或它停摆时的看门狗定时器）推进，
    /// 所以这里用 await 把消息循环让出去，让它真的走起来——只测静态帧是看不出上滚对不对的。
    /// Starts a real line roll and captures one frame mid-way and one at the end. The animation is advanced by the render loop
    /// (or its watchdog timer when rendering stalls), so the awaits let the message loop run and actually move it; static
    /// frames alone would not show whether the roll is right.
    /// </summary>
    private static async Task RenderRollAsync()
    {
        var (renderer, host) = Build(BaseStyle with { TextAlignment = "Split" }, 700, 48, ("In My Head", "Lil Tjay"));
        renderer.Present(
            Frame("多少人为生命在努力勇敢的走下去", "我们是不是该知足", false, 0.9, null, lineIndex: 5),
            animateTransition: false);
        host.UpdateLayout();
        Save(host, 700, 48, "11-roll-before");

        renderer.Present(
            Frame("我们是不是该知足", "珍惜一切就算没有拥有", false, 0.1, null, lineIndex: 6),
            animateTransition: true);
        await Task.Delay(140);
        host.UpdateLayout();
        Save(host, 700, 48, "12-roll-mid");

        await Task.Delay(700);
        host.UpdateLayout();
        Save(host, 700, 48, "13-roll-after");
    }

    private static void Save(Visual visual, int width, int height, string name)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(_directory, name + ".png"));
        encoder.Save(output);
    }
}
