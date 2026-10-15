using System.IO;
using LyricsBar.Classes.Settings;

namespace LyricsBar.Classes.Services;

/// <summary>日志级别。/ Log severity.</summary>
public enum AppLogLevel
{
    Info = 0,
    Warning = 1,
    Error = 2
}

/// <summary>
/// 极简日志服务。原项目的同名类型负责滚动文件、内存缓冲与诊断导出；精简版只需要"能写一行到文件、出错时有据可查"，
/// 因此这里保留同名 API 形态（<c>Current?.Info/Warn/Error</c>）但把实现压到最小，便于移植过来的歌词层零改动复用。
/// A minimal log service. The original of the same name handled rolling files, memory buffering, and diagnostic export; the
/// trimmed build only needs "write a line to a file so a failure leaves a trace", so the API shape
/// (<c>Current?.Info/Warn/Error</c>) is kept while the implementation is reduced to the minimum, letting the ported lyric
/// layer reuse it without edits.
/// </summary>
public sealed class AppLogService
{
    private const long MaximumBytes = 1L * 1024 * 1024;

    private readonly object _sync = new();
    private readonly string _path;

    private AppLogService(string path) => _path = path;

    /// <summary>进程内唯一实例；始终非 null，因此移植代码里的 <c>?.</c> 写法依旧安全。/ The single in-process instance; never null, so the ported <c>?.</c> call sites stay safe.</summary>
    public static AppLogService Current { get; } = new(Path.Combine(SettingsManager.Directory, "lyricsbar.log"));

    /// <summary>写一条信息级日志。/ Writes an informational entry.</summary>
    public void Info(string category, string message) => Write(AppLogLevel.Info, category, message, null);

    /// <summary>写一条警告级日志。/ Writes a warning entry.</summary>
    public void Warn(string category, string message) => Write(AppLogLevel.Warning, category, message, null);

    /// <summary>写一条带异常的警告级日志。/ Writes a warning entry carrying an exception.</summary>
    public void Warn(string category, string message, Exception exception) =>
        Write(AppLogLevel.Warning, category, message, exception);

    /// <summary>写一条错误级日志。/ Writes an error entry.</summary>
    public void Error(string category, string message, Exception? exception = null) =>
        Write(AppLogLevel.Error, category, message, exception);

    private void Write(AppLogLevel level, string category, string message, Exception? exception)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{category}] {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (_sync)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                // 单文件超过 1MB 就整体截断：精简版不追求历史留存，只要求最近的现场还在。
                if (File.Exists(_path) && new FileInfo(_path).Length > MaximumBytes)
                {
                    File.WriteAllText(_path, string.Empty);
                }

                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch
            {
                // 日志自身绝不抛出：磁盘满、权限不足都不该影响播放中的歌词。
            }
        }
    }
}
