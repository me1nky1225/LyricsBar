using System.Runtime.InteropServices;
using System.Text;

namespace LyricsBar.Classes.Interop;

/// <summary>
/// 精简版的 Win32 互操作声明。原项目的同名文件有 549 行，覆盖音频、电源、网络、钩子等十几个领域；
/// 精简版只保留"把窗口停靠进任务栏"这一件事需要的部分。
/// The trimmed Win32 interop surface. The original of the same name ran 549 lines across audio, power, networking, hooks and a
/// dozen more areas; the trimmed build keeps only what "dock a window into the taskbar" requires.
/// </summary>
internal static partial class NativeMethods
{
    // 窗口样式 GetWindowLong 的索引
    internal const int GWL_STYLE = -16;
    internal const int GWL_EXSTYLE = -20;

    internal const int WS_CHILD = 0x40000000;
    internal const int WS_POPUP = unchecked((int)0x80000000);

    /// <summary>扩展样式：命中测试穿透到下层窗口。/ Extended style: hit-testing falls through to the window below.</summary>
    internal const int WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>扩展样式：点击不激活本窗口。/ Extended style: clicking does not activate this window.</summary>
    internal const int WS_EX_NOACTIVATE = 0x08000000;

    // SetWindowPos 标志
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const uint SWP_HIDEWINDOW = 0x0080;

    internal const int RGN_OR = 2;

    /// <summary>鼠标滚轮消息。/ Mouse-wheel message.</summary>
    internal const int WM_MOUSEWHEEL = 0x020A;

    /// <summary>鼠标移动消息。悬停显示靠它判断指针是否进入窗口。/ Mouse-move message; hover reveal uses it to tell whether the pointer entered the window.</summary>
    internal const int WM_MOUSEMOVE = 0x0200;

    /// <summary>鼠标离开消息，由系统在指针移出窗口后自动投递（窗口须已请求鼠标跟踪）。/ Mouse-leave message, posted by the system once the pointer leaves the window (the window must have requested mouse tracking).</summary>
    internal const int WM_MOUSELEAVE = 0x02A3;

    /// <summary>左键按下消息。用于"单击歌词栏打开播放器"：在抬起时判断这一下是否落在按钮之外。/ Left-button-down message, used by "click the bar to open the player" to see whether the press started outside a button.</summary>
    internal const int WM_LBUTTONDOWN = 0x0201;

    /// <summary>左键抬起消息。与按下配对成一次单击（按下在按钮内、或抬起时指针已移开都不算）。/ Left-button-up message, pairing with the press into a click (a press that started on a button, or a release after the pointer moved away, does not count).</summary>
    internal const int WM_LBUTTONUP = 0x0202;

    /// <summary>滚轮每格的标准增量；wParam 的高 16 位是有符号的倍数。/ One standard wheel notch; the high word of wParam is a signed multiple of it.</summary>
    internal const int WHEEL_DELTA = 120;

    /// <summary>物理像素矩形。/ A rectangle in physical pixels.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    /// <summary>带设备名的监视器信息。/ Monitor information including the device name.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    /// <summary>窗口枚举回调。/ Window-enumeration callback.</summary>
    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>监视器枚举回调。/ Monitor-enumeration callback.</summary>
    internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumThreadWindows(uint dwThreadId, EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    internal static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    internal static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    internal static partial int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(IntPtr hObject);
}
