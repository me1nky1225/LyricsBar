using System.Runtime.InteropServices;
using LyricsBar.Classes.Utils;
using Windows.Foundation;
using Windows.Media.Control;

namespace LyricsBar.Classes.Services;

/// <summary>
/// 通过系统媒体会话（SMTC）执行切歌。歌词栏上的"上一首 / 下一首"按钮与滚轮都走这里。
/// Issues track skips through the Windows system media session (SMTC). The bar's previous/next buttons and the mouse wheel
/// both go through here.
///
/// 为什么用 SMTC 而不是模拟快捷键：<c>TrySkipNextAsync</c> 是播放器自己注册的系统接口，与键盘上的多媒体键等价，
/// 不需要知道当前是哪个播放器、也不需要它在前台，更不会像模拟 <c>VK_MEDIA_NEXT_TRACK</c> 那样受"哪个窗口有焦点"影响。
/// 代价是只有通过 SMTC 暴露控制能力的播放器才有效；不支持的播放器上按钮会置灰。
/// Why SMTC instead of synthesised hotkeys: <c>TrySkipNextAsync</c> is the system interface the player itself registers, the
/// equivalent of the keyboard's media keys. It needs no knowledge of which player is running, does not require it to be in the
/// foreground, and is not affected by "which window has focus" the way synthesising <c>VK_MEDIA_NEXT_TRACK</c> is. The cost is
/// that it only works for players exposing transport control over SMTC; unsupported ones get greyed-out buttons.
/// </summary>
public sealed class MediaTransportService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    /// <summary>请求媒体会话管理器。失败只记日志，返回 false——没有它时歌词栏照常显示，只是不能切歌。</summary>
    /// <summary>Requests the media session manager. A failure is logged and returns false: without it the bar still displays, it just cannot skip.</summary>
    public async Task<bool> InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            return _manager is not null;
        }
        catch (Exception ex)
        {
            Log.Warn("Transport", $"无法获取媒体会话管理器，切歌功能不可用: {ex.Message}");
            return false;
        }
    }

    /// <summary>切到下一首。/ Skips to the next track.</summary>
    public Task<bool> SkipNextAsync() => TryInvokeAsync(
        session => session.TrySkipNextAsync(),
        "下一首");

    /// <summary>切到上一首。/ Skips to the previous track.</summary>
    public Task<bool> SkipPreviousAsync() => TryInvokeAsync(
        session => session.TrySkipPreviousAsync(),
        "上一首");

    /// <summary>
    /// 切换播放/暂停。文案按"将要发生的动作"写：暂停中显示"继续播放"，播放中显示"暂停"。
    /// Toggles play/pause. The label names the action about to happen: "resume" while paused, "pause" while playing.
    /// </summary>
    public Task<bool> TogglePlayPauseAsync() => TryInvokeAsync(
        session => session.TryTogglePlayPauseAsync(),
        "播放/暂停");

    /// <summary>
    /// 在"当前会话"上执行一次切歌。选会话的口径与读取器一致：优先系统认定的当前会话，否则退到第一个正在播放的，
    /// 再否则用当前会话——暂停时切歌同样要能用，所以不能只认"正在播放"的那一个。
    /// Invokes one skip on the current session. Session selection matches the reader's: the system's current session first, else
    /// the first playing one, else the current one. Skipping has to work while paused too, so it cannot insist on a playing session.
    /// </summary>
    private async Task<bool> TryInvokeAsync(
        Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> invoke,
        string actionName)
    {
        var manager = _manager;
        if (manager is null)
        {
            Log.Warn("Transport", $"尚未连接到媒体会话，无法执行「{actionName}」。");
            return false;
        }

        try
        {
            var session = SelectSession(manager);
            if (session is null)
            {
                Log.Warn("Transport", $"没有可用的媒体会话，无法执行「{actionName}」。");
                return false;
            }

            var accepted = await invoke(session);
            if (!accepted)
            {
                // 播放器明确拒绝了这次操作（通常是它自己没有"上一首/下一首"的概念），这是正常结果，不是错误。
                // The player explicitly refused (usually because it has no notion of previous/next). That is a normal outcome, not an error.
                Log.Info("Transport", $"播放器未接受「{actionName}」操作（该播放器可能不支持切歌）。");
            }
            else
            {
                Log.Info("Transport", $"已执行「{actionName}」（{MediaSourceNameFormatter.GetDisplayName(session.SourceAppUserModelId ?? string.Empty)}）。");
            }

            return accepted;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // 播放器在切歌的瞬间退出是常态，不能让它把歌词栏带崩。
            // A player exiting at the moment of a skip is routine and must not take the bar down with it.
            Log.Warn("Transport", $"执行「{actionName}」失败: {ex.GetType().Name}");
            return false;
        }
    }

    private static GlobalSystemMediaTransportControlsSession? SelectSession(
        GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var current = manager.GetCurrentSession();
        if (current is not null && IsPlaying(current))
            return current;

        foreach (var session in manager.GetSessions())
        {
            if (IsPlaying(session))
                return session;
        }

        return current;
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetPlaybackInfo().PlaybackStatus ==
                   GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    public void Dispose() => _manager = null;
}
