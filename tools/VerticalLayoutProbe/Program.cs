// Vertical-taskbar geometry harness for the v1.6.1 "lyrics bar reads bottom-to-top" work.
// The window itself needs a live taskbar to dock onto, so this probe pins down the parts that are pure decisions:
// how a taskbar rect turns into an orientation, and how "靠左 / 居中 / 靠右 + 偏移量" turns into a leading offset for
// either axis. Both are the pieces that can silently be got wrong (an off-by-one clamp, the wrong axis, a stale
// "unsupported" label) without anything crashing.
using System.Reflection;
using System.Windows.Media;
using LyricsBar.Classes.Services;
using LyricsBar.Classes.Settings;
using LyricsBar.Views;

internal static class Program
{
    private static int _fail;
    private static int _pass;

    [STAThread]
    private static int Main()
    {
        Console.WriteLine("== taskbar orientation ==");
        var horizontal = Target(1920, 48);
        var vertical = Target(60, 1080);

        Check("1920×48 is horizontal", horizontal.IsVertical, false);
        Check("60×1080 is vertical", vertical.IsVertical, true);
        CheckLabel("horizontal label has no orientation suffix", horizontal.DisplayName, "竖向", false);
        CheckLabel("vertical label says 竖向", vertical.DisplayName, "竖向", true);

        // 旧版本在竖向任务栏上会标「暂不支持」，现在支持了就不该再出现这几个字。
        // Older builds flagged vertical taskbars as "暂不支持"; now that they work, those words must be gone.
        CheckLabel("vertical label no longer says 暂不支持", vertical.DisplayName, "暂不支持", false);

        Console.WriteLine();
        Console.WriteLine("== leading offset (shared by both orientations) ==");

        // 靠左：偏移量就是距起始边的距离。
        // Left: the offset is the distance from the leading edge.
        Check("left, no offset", Offset(LyricsBarAlignment.Left, 0, 800, 1), 0);
        Check("left, 24 DIP", Offset(LyricsBarAlignment.Left, 24, 800, 1), 24);
        Check("left, 24 DIP at 150%", Offset(LyricsBarAlignment.Left, 24, 800, 1.5), 36);

        // 居中：先取剩余空间的一半，再把偏移量当微调量，右移为正。
        // Centre: half the free space, with the offset as a signed nudge.
        Check("centre, no offset", Offset(LyricsBarAlignment.Center, 0, 800, 1), 400);
        Check("centre, +40 nudge", Offset(LyricsBarAlignment.Center, 40, 800, 1), 440);
        Check("centre, -40 nudge", Offset(LyricsBarAlignment.Center, -40, 800, 1), 360);

        // 靠右：从剩余空间的终点往回退偏移量。
        // Right: back off from the end of the free space.
        Check("right, no offset", Offset(LyricsBarAlignment.Right, 0, 800, 1), 800);
        Check("right, 24 DIP", Offset(LyricsBarAlignment.Right, 24, 800, 1), 776);

        // 夹取：偏移量再大也不能把歌词栏推出任务栏，否则它会"消失"得毫无痕迹。
        // Clamping: a large offset must never push the bar outside the taskbar, where it would vanish without a trace.
        Check("left, huge offset clamps to 0", Offset(LyricsBarAlignment.Left, -500, 800, 1), 0);
        Check("right, huge offset clamps to end", Offset(LyricsBarAlignment.Right, -500, 800, 1), 800);
        Check("left, offset beyond end clamps", Offset(LyricsBarAlignment.Left, 5000, 800, 1), 800);

        // 可用空间为零（歌词栏和任务栏一样长）时，三种对齐都该落在 0，不能出现负数。
        // With zero free space (bar as long as the taskbar) all three land on 0 rather than going negative.
        Check("centre, zero free space", Offset(LyricsBarAlignment.Center, 0, 0, 1), 0);
        Check("right, zero free space", Offset(LyricsBarAlignment.Right, 0, 0, 1), 0);

        Console.WriteLine();
        Console.WriteLine("== rotated layout wiring ==");

        // 竖排靠 XAML 里那个 LayoutTransform 把整块内容转过来。字段不存在说明 XAML 被改回只做横排了。
        // The rotation hangs off the LayoutTransform in the XAML; a missing field means the XAML was reverted to horizontal only.
        var rotationField = typeof(TaskbarLyricsWindow)
            .GetField("ContentRotation", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Check("ContentRotation field exists", rotationField is not null, true);
        Check("ContentRotation is a RotateTransform", rotationField?.FieldType == typeof(RotateTransform), true);

        var gridField = typeof(TaskbarLyricsWindow)
            .GetField("RootGrid", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Check("RootGrid field exists", gridField is not null, true);

        // 竖排时右边缘那条进度条要贴在竖条的边上，所以它也必须在被旋转的那块布局里。
        // The progress strip must live inside the rotated block so it lands on the vertical strip's edge.
        var progressField = typeof(TaskbarLyricsWindow)
            .GetField("ProgressTrack", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Check("ProgressTrack field exists", progressField is not null, true);

        Console.WriteLine();
        Console.WriteLine($"RESULT: {(_fail == 0 ? "PASS" : "FAIL")} ({_pass} passed, {_fail} failed)");
        return _fail == 0 ? 0 : 1;
    }

    private static TaskbarTarget Target(int width, int height)
        => new(IntPtr.Zero, @"\\?\DISPLAY#DUMMY", true, width, height, 1.0);

    /// <summary>
    /// 调窗口里那个纯函数 <c>ResolveLeadingOffsetPx</c>。它是 private static 且不碰任何窗口状态，
    /// 所以反射调用不需要创建窗口、也不需要真实任务栏。
    /// Calls the window's pure <c>ResolveLeadingOffsetPx</c>. It is private static and touches no window state, so a reflected
    /// call needs neither a window nor a real taskbar.
    /// </summary>
    private static int Offset(LyricsBarAlignment alignment, double offsetDip, int available, double scale)
    {
        var method = typeof(TaskbarLyricsWindow).GetMethod(
            "ResolveLeadingOffsetPx",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        if (method is null)
            throw new InvalidOperationException("TaskbarLyricsWindow.ResolveLeadingOffsetPx not found");

        return (int)method.Invoke(null, [alignment, offsetDip, available, scale])!;
    }

    private static void Check(string name, object? actual, object? expected)
    {
        var ok = Equals(actual, expected);
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name} → {actual}{(ok ? string.Empty : $" (expected {expected})")}");
    }

    private static void CheckLabel(string name, string text, string needle, bool shouldContain)
    {
        var contains = text.Contains(needle, StringComparison.Ordinal);
        Check(name, contains, shouldContain);
    }
}
