using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using LyricsBar.Classes.Utils;

namespace LyricsBar.Classes.Services;

/// <summary>
/// 应用图标的统一取用地。托盘图标与设置窗口标题栏都从这里拿，同一个图标只会被加载一次。
/// The single place the app icon comes from. Both the tray icon and the settings window's title bar read from here, so one
/// file backs every surface.
///
/// 为什么从程序集里读而不是从磁盘上的 .ico 读：单文件发布后磁盘上只有 LyricsBar.exe，图标以**嵌入资源**存在，
/// 既不破坏"发布目录只有一个 exe"的约定，也不会因为安装位置变动而找不到。
/// Why it is read from the assembly rather than an .ico on disk: after a single-file publish the only file on disk is
/// LyricsBar.exe, so the icon is embedded in the assembly. That keeps the "one executable in the publish directory"
/// contract and has no dependency on where the app was installed.
/// </summary>
public static class AppIcon
{
    private static readonly object _sync = new();
    private static Icon? _cached;
    private static bool _attempted;

    /// <summary>资源后缀；用后缀匹配而不是全名，命名空间调整时不必同步改这里。/ The resource suffix; matched by suffix so a namespace change needs no edit here.</summary>
    private const string ResourceSuffix = "LyricsBar.ico";

    /// <summary>
    /// 取应用图标。取不到时返回 null，由调用方决定退路（托盘退回运行时画的音符，窗口退回无图标），绝不在加载路径上抛异常。
    /// Loads the app icon. Returns null when unavailable and lets the caller decide the fallback (the tray falls back to a
    /// runtime-drawn note, the window to no icon); it never throws on the load path.
    /// </summary>
    public static Icon? LoadIcon()
    {
        lock (_sync)
        {
            if (_attempted)
                return _cached;

            _attempted = true;
            try
            {
                var assembly = typeof(AppIcon).Assembly;
                var name = assembly.GetManifestResourceNames()
                    .FirstOrDefault(candidate => candidate.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase));
                if (name is null)
                {
                    Log.Warn("Icon", "未在程序集里找到嵌入的 LyricsBar.ico，改用备选图标。");
                    return null;
                }

                using var stream = assembly.GetManifestResourceStream(name);
                if (stream is null)
                {
                    Log.Warn("Icon", "嵌入的 LyricsBar.ico 无法打开，改用备选图标。");
                    return null;
                }

                // Icon 会接管流的生命周期，因此必须先整份读进内存再交给它：
                // 直接把 ManifestResourceStream 传进去，流被释放后图标仍持有它的缓冲区，导出时会不稳定。
                // Icon takes over the stream's lifetime, so the whole file is read into memory first: handing it the manifest
                // stream directly leaves the icon holding a buffer the stream still owns, which exports unstably.
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                buffer.Position = 0;
                _cached = new Icon(buffer);
                return _cached;
            }
            catch (Exception ex)
            {
                Log.Warn("Icon", $"加载应用图标失败: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>取 WPF 窗口可用的图标源（ImageSource）。取不到时返回 null。/ Loads the icon as an ImageSource for a WPF window; null when unavailable.</summary>
    public static BitmapSource? LoadImageSource()
    {
        var icon = LoadIcon();
        if (icon is null)
            return null;

        try
        {
            // 直接用句柄转换，不再多保存一份副本：WPF 只读这张位图，而 Icon 在整个进程生命周期内都由本类持有。
            // The handle is converted directly without keeping a second copy: WPF only reads this bitmap, and the Icon itself
            // is held by this class for the process's lifetime.
            return Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        catch (Exception ex)
        {
            Log.Warn("Icon", $"转换窗口图标失败: {ex.Message}");
            return null;
        }
    }
}
