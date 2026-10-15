using System.Text;
using LyricsBar.Classes.Interop;
using LyricsBar.Classes.Utils;
using static LyricsBar.Classes.Interop.NativeMethods;

namespace LyricsBar.Classes.Services;

/// <summary>
/// 一块可停靠的任务栏。承载它的 HWND、所在显示器与几何信息，供设置界面列出来让用户选择。
/// A dockable taskbar: the HWND hosting it, the monitor it sits on, and its geometry, listed in the settings UI for the user to pick.
/// </summary>
/// <param name="Handle">任务栏窗口句柄。/ The taskbar window handle.</param>
/// <param name="MonitorDeviceId">显示器设备名，如 <c>\\.\DISPLAY1</c>。/ Monitor device name such as <c>\\.\DISPLAY1</c>.</param>
/// <param name="IsPrimary">是否为主显示器上的主任务栏。/ Whether this is the main taskbar on the primary display.</param>
/// <param name="Width">任务栏物理宽度。/ Taskbar width in physical pixels.</param>
/// <param name="Height">任务栏物理高度。/ Taskbar height in physical pixels.</param>
/// <param name="DpiScale">任务栏所在显示器的 DPI 缩放。/ DPI scale of the display the taskbar sits on.</param>
public sealed record TaskbarTarget(
    IntPtr Handle,
    string MonitorDeviceId,
    bool IsPrimary,
    int Width,
    int Height,
    double DpiScale)
{
    /// <summary>是否竖向任务栏（停靠在屏幕左右侧）。精简版只支持横向任务栏，因此竖向会被设置界面标记出来。
    /// Whether the taskbar is vertical (docked left or right). The trimmed build supports horizontal taskbars only, so the
    /// settings UI marks vertical ones.</summary>
    public bool IsVertical => Height > Width;

    /// <summary>给用户看的名称。/ Display name shown to the user.</summary>
    public string DisplayName =>
        $"{(IsPrimary ? "主显示器" : "副显示器")} · {MonitorDeviceId} · {Width}×{Height}" + (IsVertical ? "（竖向，暂不支持）" : string.Empty);
}

/// <summary>
/// 任务栏停靠：把顶层窗口变成 Explorer 任务栏的子窗口，从而"长"在任务栏里。
/// 这是整个精简版最关键也最脆弱的一环——它依赖 Explorer 的内部窗口类名与父子关系，属于非公开接口。
/// Taskbar docking: turning a top-level window into a child of the Explorer taskbar so it grows inside the bar.
/// This is the most critical and most fragile part of the whole build — it depends on Explorer's internal window class
/// names and parent/child relationship, which are undocumented interfaces.
///
/// 实现参考 AF Media Bar 的同名服务，后者又移植自 FluentFlyout（GPL-3.0-or-later）。
/// The implementation follows AF Media Bar's service of the same name, itself ported from FluentFlyout (GPL-3.0-or-later).
/// </summary>
public sealed class TaskbarDockService
{
    /// <summary>主任务栏的窗口类名。/ Window class name of the main taskbar.</summary>
    private const string PrimaryTaskbarClass = "Shell_TrayWnd";

    /// <summary>副显示器任务栏的窗口类名。/ Window class name of a secondary-display taskbar.</summary>
    private const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";

    /// <summary>
    /// 枚举当前所有可停靠的任务栏。先找主任务栏，再按"主任务栏线程 → 全部窗口"的顺序找副任务栏，
    /// 因为副任务栏通常与主任务栏同线程，先查线程比遍历全系统窗口快得多。
    /// Enumerates every dockable taskbar. The main one comes first, then secondaries are looked up along "main taskbar's thread,
    /// then all windows", because a secondary usually shares the main taskbar's thread and checking that thread is far cheaper
    /// than walking every window on the system.
    /// </summary>
    public IReadOnlyList<TaskbarTarget> EnumerateTaskbars()
    {
        var result = new List<TaskbarTarget>();
        var primary = FindWindow(PrimaryTaskbarClass, null);
        if (primary != IntPtr.Zero && TryDescribe(primary, true, out var primaryTarget))
        {
            result.Add(primaryTarget);
        }

        var secondaries = new List<IntPtr>();
        void Collect(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero && hwnd != primary && ClassNameOf(hwnd) == SecondaryTaskbarClass)
            {
                secondaries.Add(hwnd);
            }
        }

        if (primary != IntPtr.Zero)
        {
            var threadId = GetWindowThreadProcessId(primary, IntPtr.Zero);
            EnumThreadWindows(threadId, (hwnd, _) => { Collect(hwnd); return true; }, IntPtr.Zero);
        }

        EnumWindows((hwnd, _) => { Collect(hwnd); return true; }, IntPtr.Zero);

        foreach (var hwnd in secondaries.Distinct())
        {
            if (TryDescribe(hwnd, false, out var target))
            {
                result.Add(target);
            }
        }

        return result;
    }

    /// <summary>读取任务栏的物理像素矩形；句柄无效或查询失败返回 false。仅供本程序内部使用，因此不暴露 Win32 矩形类型。</summary>
    internal bool TryGetTaskbarRect(IntPtr taskbarHandle, out RECT rect)
    {
        rect = default;
        return taskbarHandle != IntPtr.Zero && GetWindowRect(taskbarHandle, out rect);
    }

    /// <summary>任务栏所在显示器的 DPI 缩放；句柄无效时返回 1。/ DPI scale of the display the taskbar sits on; 1 for an invalid handle.</summary>
    public double GetDpiScale(IntPtr taskbarHandle) =>
        taskbarHandle == IntPtr.Zero ? 1 : GetDpiForWindow(taskbarHandle) / 96.0;

    /// <summary>
    /// 把顶层窗口变成任务栏的子窗口。必须先把 <c>WS_POPUP</c> 换成 <c>WS_CHILD</c>：
    /// 否则窗口会以独立顶层窗口的身份继续试图浮在任务栏之上，父子的层叠关系不会生效。
    /// Turns a top-level window into a taskbar child. <c>WS_POPUP</c> must be swapped for <c>WS_CHILD</c> first: otherwise the
    /// window keeps trying to float above the taskbar as its own top-level window and the parent/child stacking never applies.
    /// </summary>
    public void Dock(IntPtr windowHandle, IntPtr taskbarHandle)
    {
        if (windowHandle == IntPtr.Zero || taskbarHandle == IntPtr.Zero)
            return;

        var style = GetWindowLong(windowHandle, GWL_STYLE);
        style = (style & ~WS_POPUP) | WS_CHILD;
        SetWindowLong(windowHandle, GWL_STYLE, style);
        SetParent(windowHandle, taskbarHandle);
    }

    /// <summary>
    /// 解除停靠：先隐藏并摘掉父窗口，再恢复顶层窗口样式。退出与重新停靠都必须走这里，
    /// 否则窗口会以一个"父窗口已销毁的子窗口"身份留在系统里。
    /// Undocks: hides and detaches the child first, then restores top-level styles. Both exit and re-docking must go through
    /// here, otherwise the window is left behind as a child whose parent is gone.
    /// </summary>
    public void Undock(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
            return;

        SetWindowPos(windowHandle, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_HIDEWINDOW);
        SetParent(windowHandle, IntPtr.Zero);

        var style = GetWindowLong(windowHandle, GWL_STYLE);
        style = (style & ~WS_CHILD) | WS_POPUP;
        SetWindowLong(windowHandle, GWL_STYLE, style);
    }

    /// <summary>
    /// 把已停靠窗口放到任务栏客户区中的指定位置与尺寸，不改变 Z 序、不抢焦点。
    /// 坐标以**物理像素**给出、相对父窗口客户区；调用方负责按任务栏 DPI 缩放换算。
    /// Positions the docked window inside the taskbar client area with the given size, without touching Z order or stealing focus.
    /// Coordinates are **physical pixels** relative to the parent's client area; the caller converts them using the taskbar's DPI scale.
    /// </summary>
    /// <param name="windowHandle">已停靠的窗口。/ The docked window.</param>
    /// <param name="x">客户区内的横坐标（物理像素）。/ Horizontal offset within the client area, in physical pixels.</param>
    /// <param name="y">客户区内的纵坐标（物理像素）。/ Vertical offset within the client area, in physical pixels.</param>
    /// <param name="width">宽度（物理像素）。/ Width in physical pixels.</param>
    /// <param name="height">高度（物理像素）。/ Height in physical pixels.</param>
    public void SetPosition(IntPtr windowHandle, int x, int y, int width, int height)
    {
        if (windowHandle == IntPtr.Zero)
            return;

        SetWindowPos(windowHandle, IntPtr.Zero, x, y, Math.Max(1, width), Math.Max(1, height),
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>
    /// 显示或隐藏已停靠窗口，不改变几何与 Z 序。用于"没有媒体在播放"时把歌词栏收起来，
    /// 但保留窗口本身——反复停靠/解挂比单纯改可见性危险得多。
    /// Shows or hides a docked window without touching geometry or Z order. Used to retract the bar while nothing is playing
    /// while keeping the window itself: re-docking is far more dangerous than toggling visibility.
    /// </summary>
    public void SetVisible(IntPtr windowHandle, bool visible)
    {
        if (windowHandle == IntPtr.Zero)
            return;

        SetWindowPos(windowHandle, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE |
            (visible ? SWP_SHOWWINDOW : SWP_HIDEWINDOW));
    }

    private static bool TryDescribe(IntPtr hwnd, bool isPrimary, out TaskbarTarget target)
    {
        target = null!;
        if (!TryGetRect(hwnd, out var rect))
            return false;

        target = new TaskbarTarget(
            hwnd,
            MonitorDeviceIdOf(hwnd),
            isPrimary,
            rect.Width,
            rect.Height,
            GetDpiForWindow(hwnd) / 96.0);
        return true;

        static bool TryGetRect(IntPtr h, out RECT r)
        {
            r = default;
            return h != IntPtr.Zero && GetWindowRect(h, out r);
        }
    }

    private static string ClassNameOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string MonitorDeviceIdOf(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, 2);
        if (monitor == IntPtr.Zero)
            return "unknown";

        var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfo(monitor, ref info) ? info.szDevice : "unknown";
    }
}
