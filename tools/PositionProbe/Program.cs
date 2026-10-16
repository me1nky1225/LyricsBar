// 位置仲裁自检：把 SmtcMediaReader 里那套"三条通道挑一条"的规则单独拎出来跑一遍。
// Position-arbitration self-check: runs the "pick one of three channels" rule used by SmtcMediaReader through its cases.
//
// 为什么需要它：真机日志（2026-10-15/16）里 84 次位置跳变有 74 次向后跳、且全部落在 3 秒以内 —— 那是网易云
// 上报的时间轴周期性"塌回开头"。这个自检把那几个形状固定成断言，以后改仲裁规则时不会再把它改回去。
// Why it exists: in the real-hardware log (2026-10-15/16), 74 of 84 position jumps went backwards and every one landed
// within three seconds — NetEase's reported timeline periodically "collapsing back to the start". These assertions pin
// those shapes down so a later change to the rule cannot quietly reintroduce them.

using LyricsBar.Classes.Services;

var failures = new List<string>();

void Check(string name, bool ok, string detail)
{
    Console.WriteLine($"{(ok ? "[ok]  " : "[FAIL]")} {name}：{detail}");
    if (!ok)
        failures.Add(name);
}

PositionObservation Observation(
    bool playerReports,
    double playerPosition,
    bool hasMemory,
    double memoryPosition,
    double localClock,
    bool isPlaying = true) =>
    new(playerReports, playerPosition, hasMemory, memoryPosition, localClock, isPlaying);

Console.WriteLine("=== 播放位置仲裁自检（PositionArbitrationPolicy）===");
Console.WriteLine($"常量：一致容差 {PositionArbitrationPolicy.AgreementToleranceSeconds} 秒 / "
    + $"塌回上界 {PositionArbitrationPolicy.CollapseCeilingSeconds} 秒 / "
    + $"确认拍数 {PositionArbitrationPolicy.BackwardConfirmationPolls}");
Console.WriteLine();

Check(
    "常量未被动过",
    PositionArbitrationPolicy.AgreementToleranceSeconds == 3
    && PositionArbitrationPolicy.DiscontinuitySeconds == 3
    && PositionArbitrationPolicy.CollapseCeilingSeconds == 5
    && PositionArbitrationPolicy.BackwardConfirmationPolls == 3,
    "3 / 3 / 5 / 3");

// A：内存与上报值一致 —— 用上报值，因为它带真实的时间轴基准时刻。
var policy = new PositionArbitrationPolicy();
var decision = policy.Decide("track-A", Observation(true, 100.2, true, 100.4, 100.3));
Check(
    "A 内存与上报值一致 → 用上报值",
    decision.Source == PositionSource.Player
    && decision.Reason == PositionDecisionReason.AgreesWithMemory
    && Math.Abs(decision.Position - 100.2) < 0.001,
    $"{decision.Source}/{decision.Reason}/{decision.Position:0.#}");

// B：真机那个形状 —— 内存 288.2 秒，上报值 0.4 秒。旧规则会采纳 0.4 把歌词拽回第一行。
policy = new PositionArbitrationPolicy();
decision = policy.Decide("track-B", Observation(true, 0.4, true, 288.2, 288.0));
Check(
    "B 上报值塌回开头而内存可用 → 用内存值",
    decision.Source == PositionSource.Memory
    && decision.Reason == PositionDecisionReason.DiffersFromMemory
    && Math.Abs(decision.Position - 288.2) < 0.001,
    $"{decision.Source}/{decision.Reason}/{decision.Position:0.#}");

// C：播放器根本不上报时间轴（网易云常见）—— 内存值直接采信。
policy = new PositionArbitrationPolicy();
decision = policy.Decide("track-C", Observation(false, 0, true, 42.5, 42.4));
Check(
    "C 播放器不上报而内存可用 → 用内存值",
    decision.Source == PositionSource.Memory
    && decision.Reason == PositionDecisionReason.Normal
    && Math.Abs(decision.Position - 42.5) < 0.001,
    $"{decision.Source}/{decision.Reason}/{decision.Position:0.#}");

// D：没有内存通道时，正常推进照旧立即采纳。
policy = new PositionArbitrationPolicy();
policy.Decide("track-D", Observation(true, 10, false, 0, 10));
decision = policy.Decide("track-D", Observation(true, 10.25, false, 0, 10.25));
Check(
    "D 无内存通道 + 正常推进 → 用上报值",
    decision.Source == PositionSource.Player
    && decision.Reason == PositionDecisionReason.Normal
    && Math.Abs(decision.Position - 10.25) < 0.001,
    $"{decision.Source}/{decision.Reason}/{decision.Position:0.##}");

// E：只上报、且落回开头（200 → 0.4）：脏值先不采纳，本地计时顶着。
policy = new PositionArbitrationPolicy();
policy.Decide("track-E", Observation(true, 200, false, 0, 200));
decision = policy.Decide("track-E", Observation(true, 0.4, false, 0, 200.3));
Check(
    "E 单拍塌回开头 → 先顶住（本地计时）",
    decision.Source == PositionSource.LocalClock
    && decision.Reason == PositionDecisionReason.BackwardCollapsePending
    && Math.Abs(decision.Position - 200.3) < 0.001,
    $"{decision.Source}/{decision.Reason}/{decision.Position:0.#}");

// F：同一个落点连续三拍 → 承认它（用户真的把进度拖回开头了）。
policy = new PositionArbitrationPolicy();
policy.Decide("track-F", Observation(true, 200, false, 0, 200));
policy.Decide("track-F", Observation(true, 0.4, false, 0, 200.3));
policy.Decide("track-F", Observation(true, 0.5, false, 0, 200.5));
decision = policy.Decide("track-F", Observation(true, 0.5, false, 0, 200.8));
Check(
    "F 同一落点连续 3 拍 → 承认真实跳转",
    decision.Source == PositionSource.Player
    && decision.Reason == PositionDecisionReason.BackwardCollapseConfirmed
    && Math.Abs(decision.Position - 0.5) < 0.001,
    $"{decision.Source}/{decision.Reason}/{decision.Position:0.##}");

// G：向后跳但落在开头之外（200 → 60）：这是用户拖的，不该挡。
policy = new PositionArbitrationPolicy();
policy.Decide("track-G", Observation(true, 200, false, 0, 200));
decision = policy.Decide("track-G", Observation(true, 60, false, 0, 200.2));
Check(
    "G 向后 seek 到 60 秒 → 立即采纳",
    decision.Source == PositionSource.Player
    && decision.Reason == PositionDecisionReason.BackwardSeek
    && Math.Abs(decision.Position - 60) < 0.001,
    $"{decision.Source}/{decision.Reason}/{decision.Position:0.#}");

// H：向前跳照旧立即采纳。
policy = new PositionArbitrationPolicy();
policy.Decide("track-H", Observation(true, 60, false, 0, 60));
decision = policy.Decide("track-H", Observation(true, 200, false, 0, 60.2));
Check(
    "H 向前 seek → 立即采纳",
    decision.Source == PositionSource.Player
    && decision.Reason == PositionDecisionReason.ForwardSeek
    && Math.Abs(decision.Position - 200) < 0.001,
    $"{decision.Source}/{decision.Reason}/{decision.Position:0.#}");

// I：两条通道都没有 —— 只能靠本地计时。
policy = new PositionArbitrationPolicy();
decision = policy.Decide("track-I", Observation(false, 0, false, 0, 12.5));
Check(
    "I 两条通道都没有 → 本地计时",
    decision.Source == PositionSource.LocalClock
    && decision.Reason == PositionDecisionReason.LocalClockFallback
    && Math.Abs(decision.Position - 12.5) < 0.001,
    $"{decision.Source}/{decision.Reason}/{decision.Position:0.#}");

// J：换歌必须清空历史 —— 新歌的开头（0.4 秒）不能被当成上一首的"塌回开头"。
policy = new PositionArbitrationPolicy();
policy.Decide("track-J1", Observation(true, 200, false, 0, 200));
decision = policy.Decide("track-J2", Observation(true, 0.4, false, 0, 0.1));
Check(
    "J 换歌后开头位置不算塌回 → 立即采纳",
    decision.Source == PositionSource.Player
    && decision.Reason == PositionDecisionReason.Normal
    && Math.Abs(decision.Position - 0.4) < 0.001,
    $"{decision.Source}/{decision.Reason}/{decision.Position:0.##}");

// K：脏值只出现一拍时，恢复后候选要被清掉（否则下一拍又会误判）。
policy = new PositionArbitrationPolicy();
policy.Decide("track-K", Observation(true, 200, false, 0, 200));
policy.Decide("track-K", Observation(true, 0.4, false, 0, 200.2));
decision = policy.Decide("track-K", Observation(true, 200.3, false, 0, 200.5));
Check(
    "K 脏值恢复后清掉候选",
    decision.Source == PositionSource.Player
    && decision.Reason == PositionDecisionReason.Normal
    && double.IsNaN(policy.CandidatePosition),
    $"{decision.Source}/{decision.Reason}/候选={(double.IsNaN(policy.CandidatePosition) ? "无" : policy.CandidatePosition.ToString("0.##"))}");

// L：暂停时不做跳变判断 —— 暂停期间播放器爱报什么报什么。
policy = new PositionArbitrationPolicy();
policy.Decide("track-L", Observation(true, 200, false, 0, 200));
decision = policy.Decide("track-L", Observation(true, 0.4, false, 0, 200, isPlaying: false));
Check(
    "L 暂停时不判定塌回 → 采纳上报值",
    decision.Source == PositionSource.Player
    && decision.Reason == PositionDecisionReason.Normal
    && Math.Abs(decision.Position - 0.4) < 0.001,
    $"{decision.Source}/{decision.Reason}/{decision.Position:0.##}");

// M：Reset() 之后历史清空（换歌/复位本地计时时会用到）。
policy = new PositionArbitrationPolicy();
policy.Decide("track-M", Observation(true, 200, false, 0, 200));
policy.Reset();
decision = policy.Decide("track-M", Observation(true, 0.4, false, 0, 0.1));
Check(
    "M Reset 后历史清空",
    decision.Reason == PositionDecisionReason.Normal
    && double.IsNaN(policy.AdoptedPosition) is false,
    $"{decision.Source}/{decision.Reason}/{decision.Position:0.##}");

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("RESULT: PASS");
    return 0;
}

Console.WriteLine($"RESULT: FAIL（{failures.Count} 项不符合预期：{string.Join("、", failures)}）");
return 1;
