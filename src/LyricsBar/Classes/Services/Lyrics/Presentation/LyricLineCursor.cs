using LyricsBar.Classes.Models;

namespace LyricsBar.Classes.Services.Lyrics;

/// <summary>
/// 一次选行的结果：行下标，以及**给擦亮用的位置**。
/// The outcome of one line selection: the line index and the position the highlight should use.
/// </summary>
/// <param name="Index">歌词行下标；-1 表示位置早于首行（调用方按前奏处理）。/ Line index, or -1 before the first line.</param>
/// <param name="ScanSeconds">算行内进度与逐字擦亮要用的位置（秒）。/ Position in seconds for line and syllable progress.</param>
public readonly record struct LyricLineSelection(int Index, double ScanSeconds);

/// <summary>
/// 选行迟滞。为可变的播放位置提供**单调**的选行结果，专治"擦亮反复跳 / 不能依次擦亮"这一类画面抖动。
/// Line-selection hysteresis. Turns a jittery playback position into a monotonic selection, which is what the
/// "highlight jumps back and forth / does not light up in order" family of symptoms actually needs.
///
/// 规则只有两条，其余全部保持不变：
/// Only two rules, everything else stays as it was:
/// ① 行号不回退：位置在本行内抖动（哪怕掉到行首之前一点）时，仍旧显示同一行；
///    Line index does not step back: while the position jitters inside the current line — even a little before its
///    start — the same line stays on screen.
/// ② 擦亮位置不回退：同一行内，用于算进度的位置只增不减。
///    Scan position does not step back: inside one line the position feeding the progress only moves forward.
/// 真的往回拖（位置退到当前行起点之前超过 <see cref="BackwardSeekToleranceSeconds"/> 秒）时两条规则都立即让路，
/// 否则用户拖回上一句会看到"歌词赖着不动"。
/// A real drag backwards (the position falls more than <see cref="BackwardSeekToleranceSeconds"/> seconds before the start
/// of the line being shown) releases both rules at once; without that release a deliberate rewind would look stuck.
/// 换歌（或歌词行集合换了一份）时状态整体重置，迟滞绝不跨曲生效。
/// The state resets on a track change (or a new line collection), so hysteresis never leaks across songs.
/// </summary>
public sealed class LyricLineCursor
{
    /// <summary>
    /// 判定"真的往回拖了"的容差（秒）。抖动通常远小于它，而用户拖回上一句通常远大于它。
    /// Tolerance for "this really is a rewind" in seconds. Jitter is far below it; a deliberate rewind is far above it.
    /// </summary>
    public const double BackwardSeekToleranceSeconds = 0.6;

    private string _trackId = string.Empty;
    private int _lineCount = -1;
    private int _index = -1;
    private double _lineStart;
    private double _scanSeconds;

    /// <summary>当前显示的行下标，调试与探针用。/ The line index currently being shown; for diagnostics and probes.</summary>
    public int CurrentIndex => _index;

    /// <summary>当前用于算进度的位置（秒）。/ The position currently feeding progress, in seconds.</summary>
    public double CurrentScanSeconds => _scanSeconds;

    /// <summary>
    /// 丢掉全部迟滞状态，下一次调用按当前快照重新开始。
    /// Drops every hysteretic state so the next call starts fresh from the current snapshot.
    /// </summary>
    public void Reset()
    {
        _trackId = string.Empty;
        _lineCount = -1;
        _index = -1;
        _lineStart = 0;
        _scanSeconds = 0;
    }

    /// <summary>
    /// 求这一拍该显示哪一行、该用哪个位置算进度。
    /// Resolves which line this frame shows and which position feeds its progress.
    /// </summary>
    /// <param name="trackId">曲目身份（换歌即重置）。/ Track identity; a change resets the state.</param>
    /// <param name="lines">按起始时间升序排列的行。/ Lines ordered by start time.</param>
    /// <param name="positionSeconds">本拍的播放位置（秒）。/ Playback position for this frame, in seconds.</param>
    public LyricLineSelection Resolve(string trackId, IReadOnlyList<LyricLine> lines, double positionSeconds)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (_lineCount != lines.Count || !string.Equals(trackId, _trackId, StringComparison.Ordinal))
        {
            Reset();
            _trackId = trackId;
            _lineCount = lines.Count;
        }

        var raw = LyricLineIndexPolicy.FindIndex(lines, positionSeconds);
        if (raw != _index)
        {
            var rewind = raw < _index;
            var seekBack = rewind && positionSeconds < _lineStart - BackwardSeekToleranceSeconds;
            if (!rewind || seekBack)
            {
                // 换行（前进，或确认是往回拖）：行号跟随，擦亮位置重新对准当前帧。
                // A line change (moving forward, or a confirmed rewind): the index follows and the scan position re-anchors
                // to this frame.
                _index = raw;
                _lineStart = raw >= 0 && raw < lines.Count ? lines[raw].Start : 0;
                _scanSeconds = positionSeconds;
            }
            else if (positionSeconds > _scanSeconds)
            {
                // 行内抖动导致的"原始行号回退"：行号原地不动，但位置真的前进了就照样推进擦亮。
                // A raw index that steps back from jitter: the index stays put, but real forward movement still advances the
                // highlight.
                _scanSeconds = positionSeconds;
            }
        }
        else if (positionSeconds > _scanSeconds)
        {
            // 同一行：行号不动，擦亮位置只增不减。
            // Same line: the index stays put and the scan position only moves forward.
            _scanSeconds = positionSeconds;
        }

        return new LyricLineSelection(_index, _scanSeconds);
    }
}
