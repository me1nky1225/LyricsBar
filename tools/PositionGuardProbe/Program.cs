// 位置层看门狗自检：把 SmtcMediaReader 新接的两个纯策略（MemoryChannelWatchdog / LocalClockStandInGuard）
// 单独拎出来跑一遍。
// Position-layer watchdog self-check: runs the two pure policies newly wired into SmtcMediaReader
// (MemoryChannelWatchdog / LocalClockStandInGuard) through their cases.
//
// 为什么需要它：HANDOFF-NETEASE-SYNC §6.3 点名的两件事——"内存通道锁定后连续 N 拍与本地秒表对照，偏差超阈值
// 就作废重扫"（真机日志里通道失效一次都没触发过，而锁定地址数在 3–113 之间乱跳）与"秒表只适合短时间顶替，
// 超过若干秒应冻结而不是继续跑偏"。这两个策略不碰 Win32，探针能把它们的每个形状固定成断言。
// Why it exists: HANDOFF-NETEASE-SYNC §6.3 names two things — "once the memory channel locks, compare it against the
// local clock for N polls and invalidate on a persistent gap" (channel invalidation never fired in the real-hardware
// logs while the locked address count bounced between 3 and 113) and "the stopwatch is only fit to stand in briefly;
// past a few seconds it should freeze rather than keep drifting". Neither policy touches Win32, so a probe can pin
// every shape of them down as assertions.
//
// 2026-10-16 追加（v1.4.7）：第三节跑 MemoryLockSanityPolicy——真机日志证明网易云更新后扫描会锁到一个 12:39 起算的
// 全局计时器（内存值 4776–4969 秒、换歌不归零、越过所有行尾时间戳，用户报"匹配不准、不会动了"），
// 健全性检查负责在仲裁之前把这种锁定挡掉。形状全部取自日志实测数据。
// Added 2026-10-16 (v1.4.7): the third section runs MemoryLockSanityPolicy — the real-hardware log proved that after
// a NetEase update the scan locks onto a global timer started at 12:39 (memory values 4776–4969 seconds, never
// resetting on a track change, sailing past every line's timestamp; the user reported "matching is off and it stopped
// moving"). The sanity check blocks such locks before the arbiter sees them. Every shape below comes from measured
// log data.

using LyricsBar.Classes.Services;

var failures = new List<string>();

void Check(string name, bool ok, string detail)
{
    Console.WriteLine($"{(ok ? "[ok]  " : "[FAIL]")} {name}：{detail}");
    if (!ok)
        failures.Add(name);
}

Console.WriteLine("=== 位置层看门狗自检（MemoryChannelWatchdog / LocalClockStandInGuard）===");
Console.WriteLine();

// ---------- 一、MemoryChannelWatchdog ----------

Console.WriteLine("--- 内存通道看门狗 ---");
Console.WriteLine($"常量：窗口 {MemoryChannelWatchdog.WindowSeconds} 秒 / 推进量容差 {MemoryChannelWatchdog.RateToleranceSeconds} 秒");

Check(
    "W0 常量未被动过",
    MemoryChannelWatchdog.WindowSeconds == 10 && MemoryChannelWatchdog.RateToleranceSeconds == 5,
    "窗口 10 / 容差 5");

// W1：健康通道——内存与本地计时同步推进，多个窗口都判 Ok。
{
    var watchdog = new MemoryChannelWatchdog();
    var ok = true;
    double memory = 100, clock = 100;
    for (var poll = 0; poll < 80; poll++) // 80 拍 × 0.25 秒 = 20 秒，跨两个窗口
    {
        memory += 0.25;
        clock += 0.25;
        if (watchdog.Observe("track-W1", true, memory, clock, true, 0.25) is MemoryChannelVerdict.Invalidate)
            ok = false;
    }

    Check("W1 内存与本地计时同步推进 → 一直 Ok", ok, $"20 秒 / 两个窗口 / 末值 {memory:0.#} 对 {clock:0.#}");
}

// W2：窗口没走满之前不下结论。
{
    var watchdog = new MemoryChannelWatchdog();
    var verdict = MemoryChannelVerdict.Ok;
    double memory = 100, clock = 100;
    for (var poll = 0; poll < 39; poll++) // 9.75 秒，差一点点不满一个窗口
    {
        if (watchdog.Observe("track-W2", true, memory, clock, true, 0.25) is MemoryChannelVerdict.Invalidate)
            verdict = MemoryChannelVerdict.Invalidate;
        memory += 0.25;
    }

    Check("W2 窗口未满 → 不下结论", verdict == MemoryChannelVerdict.Ok, "9.75 秒 < 10 秒");
}

// W3：内存卡死不动而播放中——一个窗口后作废。
{
    var watchdog = new MemoryChannelWatchdog();
    var verdict = MemoryChannelVerdict.Ok;
    double clock = 100;
    for (var poll = 0; poll < 41; poll++) // 10.25 秒，刚好越过一个窗口
    {
        clock += 0.25;
        verdict = watchdog.Observe("track-W3", true, 100, clock, true, 0.25);
    }

    Check("W3 内存卡死而播放中 → 作废重扫", verdict == MemoryChannelVerdict.Invalidate, "内存推进 0 / 计时推进 10.25 秒");
}

// W4：内存按两倍速漂移（锁到别的东西）——同样作废。
{
    var watchdog = new MemoryChannelWatchdog();
    var verdict = MemoryChannelVerdict.Ok;
    double memory = 100, clock = 100;
    for (var poll = 0; poll < 41; poll++)
    {
        memory += 0.5;
        clock += 0.25;
        verdict = watchdog.Observe("track-W4", true, memory, clock, true, 0.25);
    }

    Check("W4 内存两倍速漂移 → 作废重扫", verdict == MemoryChannelVerdict.Invalidate, "内存推进 20.5 / 计时推进 10.25，差 10.25 秒 > 5");
}

// W5：暂停期间内存不动——分不清"卡死"与"暂停"，不判；恢复后重新开窗。
{
    var watchdog = new MemoryChannelWatchdog();
    watchdog.Observe("track-W5", true, 100, 100, true, 0.25);
    var pausedVerdict = MemoryChannelVerdict.Ok;
    double clock = 100;
    for (var poll = 0; poll < 60; poll++) // 暂停 15 秒，内存一直停在 100
    {
        pausedVerdict = watchdog.Observe("track-W5", true, 100, clock, false, 0.25);
    }

    var resumed = watchdog.Observe("track-W5", true, 100, 100.25, true, 0.25);
    Check(
        "W5 暂停期间内存不动 → 不判失效，恢复后重新开窗",
        pausedVerdict == MemoryChannelVerdict.Ok && resumed == MemoryChannelVerdict.Ok,
        $"暂停 15 秒全 Ok / 恢复首拍 {resumed}");
}

// W6：通道读不到值（正在重扫）——不是看门狗的事，恢复后窗口重开。
{
    var watchdog = new MemoryChannelWatchdog();
    watchdog.Observe("track-W6", true, 100, 100, true, 0.25);
    var missingVerdict = MemoryChannelVerdict.Ok;
    for (var poll = 0; poll < 40; poll++)
    {
        missingVerdict = watchdog.Observe("track-W6", false, 0, 105, true, 0.25);
    }

    var back = watchdog.Observe("track-W6", true, 105, 105, true, 0.25);
    Check(
        "W6 通道重扫读不到值 → 不判失效",
        missingVerdict == MemoryChannelVerdict.Ok && back == MemoryChannelVerdict.Ok,
        $"重扫 10 秒全 Ok / 回来后首拍 {back}");
}

// W7：换歌清窗——上一首攒下的窗口历史不带入新歌。
{
    var watchdog = new MemoryChannelWatchdog();
    double memory = 300, clock = 300;
    for (var poll = 0; poll < 40; poll++) // 攒 10 秒的健康历史
    {
        memory += 0.25;
        clock += 0.25;
        watchdog.Observe("track-W7a", true, memory, clock, true, 0.25);
    }

    // 换歌：内存回到 0.3，计时归零。若旧窗口没被清掉，这一拍就会拿"新值 - 旧基线"算出荒谬的推进量。
    var verdict = watchdog.Observe("track-W7b", true, 0.3, 0.1, true, 0.25);
    Check("W7 换歌 → 旧窗口历史不带入", verdict == MemoryChannelVerdict.Ok, $"{verdict}");
}

// W8：作废之后重新开窗——通道恢复健康就重新判 Ok（不会无限作废下去）。
{
    var watchdog = new MemoryChannelWatchdog();
    double memory = 100, clock = 100;
    var firstInvalidate = false;
    for (var poll = 0; poll < 41; poll++)
    {
        clock += 0.25; // 内存卡在 100
        if (watchdog.Observe("track-W8", true, 100, clock, true, 0.25) is MemoryChannelVerdict.Invalidate)
            firstInvalidate = true;
    }

    // 模拟"重扫后锁对了"：内存从 100 起重新跟着计时走。
    var recovered = true;
    memory = 100;
    for (var poll = 0; poll < 41; poll++)
    {
        memory += 0.25;
        clock += 0.25;
        if (watchdog.Observe("track-W8", true, memory, clock, true, 0.25) is MemoryChannelVerdict.Invalidate)
            recovered = false;
    }

    Check(
        "W8 作废后恢复同步 → 重新 Ok",
        firstInvalidate && recovered,
        $"第一次 {firstInvalidate} / 恢复后两个窗口健康 {recovered}");
}

// W9：用户在一个窗口内拖进度条——两条通道一起跳，不误判。
{
    var watchdog = new MemoryChannelWatchdog();
    double memory = 100, clock = 100;
    for (var poll = 0; poll < 30; poll++)
    {
        memory += 0.25;
        clock += 0.25;
        watchdog.Observe("track-W9", true, memory, clock, true, 0.25);
    }

    // 第 30 拍用户从 107.5 拖到 200：内存跳、计时被钉到 200，之后同步推进。
    memory = 200;
    clock = 200;
    var verdict = MemoryChannelVerdict.Ok;
    for (var poll = 0; poll < 11; poll++)
    {
        memory += 0.25;
        clock += 0.25;
        verdict = watchdog.Observe("track-W9", true, memory, clock, true, 0.25);
    }

    Check("W9 窗口内拖进度条 → 不误判", verdict == MemoryChannelVerdict.Ok, $"{verdict}");
}

// W10：Reset() 清窗（换歌/手动复位时会用到）。
{
    var watchdog = new MemoryChannelWatchdog();
    double clock = 100;
    for (var poll = 0; poll < 40; poll++)
    {
        clock += 0.25;
        watchdog.Observe("track-W10", true, 100, clock, true, 0.25); // 攒一个"卡死"窗口
    }

    watchdog.Reset();
    var verdict = watchdog.Observe("track-W10", true, 100, clock, true, 0.25);
    Check("W10 Reset 后重新开窗", verdict == MemoryChannelVerdict.Ok, $"{verdict}");
}

Console.WriteLine();

// ---------- 二、LocalClockStandInGuard ----------

Console.WriteLine("--- 本地计时顶替上限 ---");
Console.WriteLine($"常量：顶替上限 {LocalClockStandInGuard.MaximumStandInSeconds} 秒");

Check(
    "S0 常量未被动过",
    LocalClockStandInGuard.MaximumStandInSeconds == 30,
    "上限 30 秒");

// S1：没有被顶替 → 一直 Continue，计时清零。
{
    var guard = new LocalClockStandInGuard();
    var verdict = LocalClockStandInVerdict.Continue;
    for (var poll = 0; poll < 200; poll++)
        verdict = guard.Observe(false, true, 0.25);

    Check("S1 真实通道在场 → 不冻结", verdict == LocalClockStandInVerdict.Continue && guard.StandInSeconds == 0, $"计时 {guard.StandInSeconds:0.#} 秒");
}

// S2：差一点点不满 30 秒 → 继续顶；踩线 → 冻结。
{
    var guard = new LocalClockStandInGuard();
    var verdict = LocalClockStandInVerdict.Continue;
    for (var poll = 0; poll < 119; poll++) // 29.75 秒
        verdict = guard.Observe(true, true, 0.25);
    var almost = verdict;

    verdict = guard.Observe(true, true, 0.25); // 第 120 拍 = 30 秒
    Check(
        "S2 顶替满 30 秒 → 冻结",
        almost == LocalClockStandInVerdict.Continue && verdict == LocalClockStandInVerdict.Freeze,
        $"29.75 秒 {almost} / 30 秒 {verdict}");
}

// S3：暂停期间顶替不计时——暂停时位置本来就不动，没有偏差可积累。
{
    var guard = new LocalClockStandInGuard();
    for (var poll = 0; poll < 100; poll++) // 先顶 25 秒
        guard.Observe(true, true, 0.25);

    var paused = LocalClockStandInVerdict.Continue;
    for (var poll = 0; poll < 40; poll++) // 暂停 10 秒
        paused = guard.Observe(true, false, 0.25);

    Check(
        "S3 暂停 → 顶替计时清零",
        paused == LocalClockStandInVerdict.Continue && guard.StandInSeconds == 0,
        $"暂停 10 秒后计时 {guard.StandInSeconds:0.#} 秒");
}

// S4：顶替中途被真实通道接走 → 计时清零，重新攒。
{
    var guard = new LocalClockStandInGuard();
    for (var poll = 0; poll < 100; poll++) // 顶 25 秒
        guard.Observe(true, true, 0.25);

    var takenOver = guard.Observe(false, true, 0.25); // 内存通道回来了
    var verdict = LocalClockStandInVerdict.Continue;
    for (var poll = 0; poll < 100; poll++) // 再顶 25 秒
        verdict = guard.Observe(true, true, 0.25);

    Check(
        "S4 顶替被接走 → 计时清零",
        takenOver == LocalClockStandInVerdict.Continue && verdict == LocalClockStandInVerdict.Continue,
        $"接走时 {takenOver} / 再顶 25 秒仍 {verdict}");
}

// S5：冻结后继续顶替 → 保持 Freeze；真实通道恢复 → 清零。
{
    var guard = new LocalClockStandInGuard();
    for (var poll = 0; poll < 120; poll++)
        guard.Observe(true, true, 0.25);

    var stillFrozen = guard.Observe(true, true, 0.25);
    var recovered = guard.Observe(false, true, 0.25);
    var afterRecovery = guard.Observe(true, true, 0.25);
    Check(
        "S5 冻结期间保持 / 恢复后清零",
        stillFrozen == LocalClockStandInVerdict.Freeze &&
        recovered == LocalClockStandInVerdict.Continue &&
        afterRecovery == LocalClockStandInVerdict.Continue &&
        guard.StandInSeconds < 0.5,
        $"保持 {stillFrozen} / 恢复 {recovered} / 之后 {afterRecovery}（计时 {guard.StandInSeconds:0.##} 秒）");
}

// ---------- 三、MemoryLockSanityPolicy ----------

Console.WriteLine();
Console.WriteLine("--- 内存锁定健全性检查 ---");
Console.WriteLine($"常量：新锁定容差 {MemoryLockSanityPolicy.LockAgreementSeconds} 秒 / "
    + $"归零上界 {MemoryLockSanityPolicy.TrackResetCeilingSeconds} 秒 / "
    + $"归零宽限 {MemoryLockSanityPolicy.TrackResetGraceSeconds} 秒 / "
    + $"最长等待 {MemoryLockSanityPolicy.TrackResetMaxWaitSeconds} 秒");

MemoryLockSanityVerdict Sanity(
    MemoryLockSanityPolicy policy,
    bool armTrackReset,
    bool hasMemory,
    double memory,
    double clock,
    bool isPlaying = true,
    double elapsed = 0.25) =>
    policy.Observe(armTrackReset, hasMemory, memory, clock, isPlaying, elapsed);

Check(
    "M0 常量未被动过",
    MemoryLockSanityPolicy.LockAgreementSeconds == 900
    && MemoryLockSanityPolicy.TrackResetCeilingSeconds == 20
    && MemoryLockSanityPolicy.TrackResetGraceSeconds == 3
    && MemoryLockSanityPolicy.TrackResetMaxWaitSeconds == 10,
    "900 / 20 / 3 / 10");

// M1：新锁定与本地计时只差 150 秒（日志 13:58:32 健康锁定的真实形状：内存 163.1 / 计时 12.7）。
{
    var policy = new MemoryLockSanityPolicy();
    var verdict = Sanity(policy, armTrackReset: false, hasMemory: true, memory: 163.1, clock: 12.7);
    Check("M1 新锁定与计时相差 150 秒 → 可信", verdict == MemoryLockSanityVerdict.Ok, $"{verdict}");
}

// M2：新锁定相差 4862 秒（日志 14:00:18 垃圾锁定的真实形状：内存 4866.1 / 计时 4.4）。
{
    var policy = new MemoryLockSanityPolicy();
    var verdict = Sanity(policy, armTrackReset: false, hasMemory: true, memory: 4866.1, clock: 4.4);
    Check(
        "M2 新锁定相差 4862 秒 → 拒绝",
        verdict == MemoryLockSanityVerdict.RejectLockAgreement,
        $"{verdict}");
}

// M3：已建立的锁定不再每次核对——用户拖进度条造成的大阶跃不能误伤。
{
    var policy = new MemoryLockSanityPolicy();
    Sanity(policy, false, true, 100, 100); // 先建立：相差 0
    var verdict = Sanity(policy, false, true, 3000, 100); // 之后跳变不管
    Check("M3 已建立的锁定阶跃 → 不误伤", verdict == MemoryLockSanityVerdict.Ok, $"{verdict}");
}

// M4：换歌后位置落零 → 解除布防。
{
    var policy = new MemoryLockSanityPolicy();
    Sanity(policy, false, true, 180, 180);
    var verdict = Sanity(policy, armTrackReset: true, hasMemory: true, memory: 1.2, clock: 0.3);
    Check("M4 换歌后落零 → Ok", verdict == MemoryLockSanityVerdict.Ok, $"{verdict}");
}

// M5：换歌后位置不归零（4800）→ 宽限 3 秒后拒绝（布防那拍也计 0.25 秒，故第 12 次观察踩线；判负后布防即除）。
{
    var policy = new MemoryLockSanityPolicy();
    Sanity(policy, false, true, 180, 180);
    Sanity(policy, true, true, 4800, 180);
    var verdict = MemoryLockSanityVerdict.Ok;
    var polls = 0;
    for (var poll = 1; poll <= 30; poll++)
    {
        verdict = Sanity(policy, false, true, 4800, 180);
        polls = poll;
        if (verdict is not MemoryLockSanityVerdict.Ok)
            break;
    }

    Check(
        "M5 换歌后不归零 → 宽限 3 秒后拒绝",
        verdict == MemoryLockSanityVerdict.RejectTrackReset && polls <= 12,
        $"第 {polls} 次观察判定 {verdict}");
}

// M6：宽限内不判（10 次观察 = 2.75 秒 < 3 秒），第 11 次踩线才判。
{
    var policy = new MemoryLockSanityPolicy();
    Sanity(policy, false, true, 180, 180);
    Sanity(policy, true, true, 4800, 180);
    var almost = MemoryLockSanityVerdict.Ok;
    for (var poll = 0; poll < 10; poll++) // 布防 0.25 + 10×0.25 = 2.75 秒
        almost = Sanity(policy, false, true, 4800, 180);
    var verdict = Sanity(policy, false, true, 4800, 180); // 第 11 次 = 3 秒
    Check(
        "M6 宽限内不判、踩线才判",
        almost == MemoryLockSanityVerdict.Ok && verdict == MemoryLockSanityVerdict.RejectTrackReset,
        $"2.75 秒 {almost} / 3 秒 {verdict}");
}

// M7：app 启动后的第一首不布防——歌可能已播了一大半（位置 163 > 20 也不拒绝）。
{
    var policy = new MemoryLockSanityPolicy();
    var verdict = Sanity(policy, armTrackReset: false, hasMemory: true, memory: 163.1, clock: 8);
    var later = MemoryLockSanityVerdict.Ok;
    for (var poll = 0; poll < 20; poll++)
        later = Sanity(policy, false, true, 163.1, 8);
    Check(
        "M7 首曲不布防 → 位置非零也不拒绝",
        verdict == MemoryLockSanityVerdict.Ok && later == MemoryLockSanityVerdict.Ok,
        $"{verdict}/{later}");
}

// M8：布防期间通道正在重扫 → 不判；恢复后落零 → Ok。
{
    var policy = new MemoryLockSanityPolicy();
    Sanity(policy, false, true, 180, 180);
    Sanity(policy, true, true, 4800, 180);
    var missing = MemoryLockSanityVerdict.Ok;
    for (var poll = 0; poll < 8; poll++) // 重扫 2 秒
        missing = Sanity(policy, false, false, 0, 182);
    var back = Sanity(policy, false, true, 0.5, 182.5);
    Check(
        "M8 布防期间重扫 → 不判，恢复落零 → Ok",
        missing == MemoryLockSanityVerdict.Ok && back == MemoryLockSanityVerdict.Ok,
        $"重扫 {missing} / 恢复 {back}");
}

// M9：等超过 10 秒通道还没回来 → 放弃布防（重扫是服务自己的失败路径在管）。
{
    var policy = new MemoryLockSanityPolicy();
    Sanity(policy, false, true, 180, 180);
    Sanity(policy, true, true, 4800, 180);
    var verdict = MemoryLockSanityVerdict.Ok;
    for (var poll = 0; poll < 44; poll++) // 11 秒 > 10 秒
        verdict = Sanity(policy, false, false, 0, 180);
    Check("M9 等待超时 → 放弃布防 Ok", verdict == MemoryLockSanityVerdict.Ok, $"{verdict}");
}

// M10：暂停中不判（切歌边界上播放器可能还没把位置换过来），但暂停时间照旧计入宽限——
// 整个暂停期间位置都没归零，恢复那一拍就没有理由再等。恢复后仍不归零 → 立即判负。
{
    var policy = new MemoryLockSanityPolicy();
    Sanity(policy, false, true, 180, 180);
    Sanity(policy, true, true, 4800, 180, isPlaying: false);
    var paused = MemoryLockSanityVerdict.Ok;
    for (var poll = 0; poll < 20; poll++)
        paused = Sanity(policy, false, true, 4800, 180, isPlaying: false);
    var resumed = Sanity(policy, false, true, 4800, 180, isPlaying: true);
    Check(
        "M10 暂停中不判、恢复后仍不归零即判",
        paused == MemoryLockSanityVerdict.Ok && resumed == MemoryLockSanityVerdict.RejectTrackReset,
        $"暂停 5 秒 {paused} / 恢复首拍 {resumed}");
}

// M11：Reset() 清空（手动复位本地计时时会用到）——清空后下一次读数重新按"新锁定"核对，垃圾照样被挡。
{
    var policy = new MemoryLockSanityPolicy();
    Sanity(policy, false, true, 100, 100);
    policy.Reset();
    var verdict = Sanity(policy, false, true, 4800, 4.4);
    Check(
        "M11 Reset 后重新按新锁定核对",
        verdict == MemoryLockSanityVerdict.RejectLockAgreement,
        $"{verdict}");
}

Console.WriteLine();
if (failures.Count == 0)
{
    Console.WriteLine("RESULT: PASS");
    return 0;
}

Console.WriteLine($"RESULT: FAIL（{failures.Count} 项不符合预期：{string.Join("、", failures)}）");
return 1;
