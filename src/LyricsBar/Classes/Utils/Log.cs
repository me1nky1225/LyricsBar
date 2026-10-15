using LyricsBar.Classes.Services;

namespace LyricsBar.Classes.Utils;

/// <summary>
/// 静态日志门面：新写的代码用 <c>Log.Warn(...)</c> 这种短写法，移植过来的歌词层继续用
/// <c>AppLogService.Current?.Warn(...)</c>，两者落到同一个实现上。
/// Static logging facade: newly written code uses the short <c>Log.Warn(...)</c> form while the ported lyric layer keeps its
/// <c>AppLogService.Current?.Warn(...)</c> call sites, and both land on the same implementation.
/// </summary>
public static class Log
{
    /// <summary>信息级日志。/ Informational entry.</summary>
    public static void Info(string category, string message) => AppLogService.Current.Info(category, message);

    /// <summary>警告级日志。/ Warning entry.</summary>
    public static void Warn(string category, string message) => AppLogService.Current.Warn(category, message);

    /// <summary>错误级日志。/ Error entry.</summary>
    public static void Error(string category, string message, Exception? exception = null) =>
        AppLogService.Current.Error(category, message, exception);
}
