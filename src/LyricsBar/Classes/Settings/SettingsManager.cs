using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LyricsBar.Classes.Utils;

namespace LyricsBar.Classes.Settings;

/// <summary>
/// 设置变更通知参数。带上"改的是哪个属性"，取词层才能分辨"该重新联网取词"与"只需重绘"。
/// Settings-change arguments. Carrying which property changed lets the retrieval layer tell "refetch over the network" apart
/// from "just repaint".
/// </summary>
/// <param name="PropertyName">变更的属性名；整体替换或重置时为 null。/ The changed property name; null for a bulk replace or a reset.</param>
/// <param name="ResetScope">重置范围；普通变更为 null。/ The reset scope; null for an ordinary change.</param>
public sealed class SettingsChangedEventArgs(string? propertyName = null, SettingsResetScope? resetScope = null) : EventArgs
{
    /// <summary>变更的属性名。/ The changed property name.</summary>
    public string? PropertyName { get; } = propertyName;

    /// <summary>重置范围。/ The reset scope.</summary>
    public SettingsResetScope? ResetScope { get; } = resetScope;
}

/// <summary>
/// 设置的读写与变更广播。取代原项目那个被引用 334 次的静态单例，这里只保留精简版真正需要的一小块能力：
/// 一个进程内单例、JSON 落盘、以及一个"设置变了"的事件。
/// Settings load/save plus change broadcast. This replaces the original static singleton that was referenced 334 times; the
/// trimmed build needs only a small slice of that: one in-process instance, a JSON file, and one "settings changed" event.
/// </summary>
public static class SettingsManager
{
    private static readonly object _sync = new();
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    private static AppSettings _current = new();

    /// <summary>当前设置；渲染与取词层直接读它。/ The current settings, read directly by the rendering and retrieval layers.</summary>
    public static AppSettings Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    /// <summary>设置文件所在目录：%LOCALAPPDATA%\LyricsBar。/ The settings directory: %LOCALAPPDATA%\LyricsBar.</summary>
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LyricsBar");

    /// <summary>设置文件完整路径。/ Full path of the settings file.</summary>
    public static string FilePath { get; } = Path.Combine(Directory, "settings.json");

    /// <summary>设置发生变化后触发；订阅者需自行切回 UI 线程。/ Raised after settings change; subscribers must marshal to the UI thread themselves.</summary>
    public static event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <summary>
    /// 从磁盘读取设置；文件缺失或损坏时回落到默认值并保持可用，不抛异常。
    /// Loads settings from disk; a missing or corrupt file falls back to defaults and stays usable instead of throwing.
    /// </summary>
    public static void Load()
    {
        AppSettings loaded;
        try
        {
            loaded = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), _json) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Warn("Settings", $"设置文件读取失败，已回落到默认值: {ex.Message}");
            loaded = new AppSettings();
        }

        lock (_sync)
        {
            _current = loaded.Normalize();
        }
    }

    /// <summary>
    /// 整体替换当前设置并落盘、广播。可选地声明"这次改的是哪个属性"，取词层据此决定是否清空歌词缓存。
    /// Replaces the current settings, persists them, and broadcasts. Optionally declares which property changed so the retrieval
    /// layer can decide whether to clear the lyric cache.
    /// </summary>
    /// <param name="settings">新设置。/ The new settings.</param>
    /// <param name="propertyName">变更的属性名；整体替换时为 null。/ The changed property name; null for a bulk replace.</param>
    /// <param name="resetScope">重置范围；普通变更为 null。/ The reset scope; null for an ordinary change.</param>
    public static void Apply(AppSettings settings, string? propertyName = null, SettingsResetScope? resetScope = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_sync)
        {
            _current = settings.Normalize();
        }

        Save();
        Changed?.Invoke(null, new SettingsChangedEventArgs(propertyName, resetScope));
    }

    /// <summary>
    /// 静默修改当前设置并落盘，不广播 <see cref="Changed"/>。
    /// 供程序自己维护的"内部状态"类设置使用（例如已经跑通的 WebView2 降级级别）。这类变更不是用户操作，
    /// 一旦广播，取词层会按"设置整体变了"清空歌词缓存并重新联网取词，白白浪费一次请求。
    /// Mutates the current settings and persists them without raising <see cref="Changed"/>. Meant for internal state the app
    /// maintains itself, such as the WebView2 fallback tier that worked. Such a change is not a user action, and broadcasting
    /// it would make the retrieval layer treat it as a bulk settings change, clear the lyric cache, and refetch over the
    /// network for nothing.
    /// </summary>
    /// <param name="mutate">对当前设置做的修改。/ The mutation to apply to the current settings.</param>
    public static void UpdateSilently(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_sync)
        {
            mutate(_current);
        }

        Save();
    }

    /// <summary>把当前设置写入磁盘；失败只记日志，不影响运行中的界面。/ Writes the current settings to disk; a failure is logged and does not disturb the running UI.</summary>
    public static void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, _json));
        }
        catch (Exception ex)
        {
            Log.Warn("Settings", $"设置文件保存失败: {ex.Message}");
        }
    }
}
