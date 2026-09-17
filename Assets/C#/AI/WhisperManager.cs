using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// AI 之间悄悄话的调度中心。
/// 每条悄悄话最终都会得到一个字符串结果返回给调用方（发送方 AI 的 tool result）。
/// 调度规则：
/// 1. 目标空闲：立刻单独发起一次目标 AI 的回复请求，回复内容作为 tool result。
/// 2. 目标忙碌：排队等待；目标忙完后按 FIFO 依次回复等待者。
/// 3. 互相等待（A→B 且 B→A）：按环形等待处理，只强制最早一条返回“繁忙”，
///    另一条等对方忙完后获得真实回复，不浪费对话机会。
/// 4. 环形等待（A→B→C→A）：强制最早那条（A→B）返回“繁忙中请等待通话调配”，
///    并把 A 的话转投给 C，让 C→A、B→C、A→C 依次消化。
/// </summary>
public static class WhisperManager
{
    public class WhisperRequest
    {
        public int Sender;
        public int Target;
        public string Message;
        public TaskCompletionSource<string> Tcs = new();
        public int? PreviousCooldownRound;
        public int Round;
        /// <summary>这次悄悄话扣掉的升级能量（被系统调配终止时按这个数退还）。</summary>
        public float EnergyCost;
    }

    /// <summary>由 AIAgent 注册：target 收到 sender 的悄悄话后，单独发起一次请求并返回回复文本。</summary>
    public static Func<int, int, string, Task<string>> ReplyProvider;

    private static readonly Dictionary<int, int> busyCount = new();
    private static readonly Dictionary<int, List<WhisperRequest>> waiters = new();
    private static readonly Dictionary<int, WhisperRequest> waitingOn = new(); // sender -> 正在等待的悄悄话
    private static readonly List<WhisperRequest> waitOrder = new();
    private static readonly HashSet<int> deadStages = new();

    public static void SetDead(int stage)
    {
        if (stage < 0) return;
        deadStages.Add(stage);

        // 击杀瞬间清掉所有发给它的等待，并退还这些调用方的冷却机会。
        if (waiters.TryGetValue(stage, out var list))
        {
            var snapshot = new List<WhisperRequest>(list);
            foreach (WhisperRequest req in snapshot)
                CompleteRequest(req, $"悄悄话失败：{AIAgent.GetStageName(stage)}已被击杀，无法接收悄悄话。本次未消耗使用机会。", refundCooldown: true);
        }
    }

    public static bool IsDead(int stage) => stage >= 0 && deadStages.Contains(stage);

    /// <summary>标记一个 AI 的主请求/悄悄话回复任务开始或结束。计数器归零时才处理它的等待队列。</summary>
    public static void SetBusy(int stage, bool busy)
    {
        if (stage < 0) return;
        if (busy)
        {
            busyCount[stage] = busyCount.TryGetValue(stage, out int c) ? c + 1 : 1;
        }
        else
        {
            if (!busyCount.TryGetValue(stage, out int c) || c <= 0) return;
            c--;
            if (c > 0) busyCount[stage] = c;
            else
            {
                busyCount.Remove(stage);
                ProcessWaiters(stage);
            }
        }
    }

    public static bool IsBusy(int stage) => stage >= 0 && busyCount.TryGetValue(stage, out int c) && c > 0;

    /// <summary>发送悄悄话，返回给发送方 AI 的 tool result 文本。</summary>
    public static async Task<string> WhisperAsync(int sender, int target, string message, int currentRound)
    {
        if (ReplyProvider == null)
            return "悄悄话失败：悄悄话系统未注册回复通道。";
        if (sender == target)
            return "悄悄话失败：不能给自己发悄悄话。";
        if (string.IsNullOrWhiteSpace(message))
            return "悄悄话失败：content 不能为空。";
        if (IsDead(sender))
            return "悄悄话失败：你已被击杀，无法使用工具。";
        if (IsDead(target))
            return $"悄悄话失败：{AIAgent.GetStageName(target)}已被击杀，无法接收悄悄话。";
        // 冷却轮数与能量消耗都取 MapConfig：冷却不再写死 4 回合，能量与移动共用同一个池子
        MapConfig cfg = MapConfig.Instance;
        int cooldownRounds = cfg != null ? Mathf.Max(1, cfg.whisperCooldownRounds) : 4;
        float energyCost = cfg != null ? cfg.whisperEnergyCost : 0f;
        MarbleManager mm = MarbleManager.Instance;
        if (mm != null && energyCost > 0f && mm.GetUpgradeEnergy(sender) < energyCost)
            return "悄悄话失败：升级能量不足（本次需要 " + energyCost.ToString("0.#")
                + "，当前 " + mm.GetUpgradeEnergy(sender).ToString("0.#") + "）。能量来自空槽升级进度，会随时间积累。";

        // 开局就算"在冷却中"：没记录过就按第 0 回合算，所以第一发要等到第 cooldownRounds 回合。
        // 退款也按这个基线还原（见 TryAcquireCooldown 的注释）。
        int? previousCooldownRound = lastWhisperRound.TryGetValue(sender, out int prev) ? prev : 0;
        if (!TryAcquireCooldown(sender, currentRound, cooldownRounds, out string cooldownError))
            return cooldownError;

        // 冷却拿到手了才扣能量（冷却没拿到就直接返回，不会白扣）
        if (mm != null && energyCost > 0f) mm.TrySpendUpgradeEnergy(sender, energyCost);

        var req = new WhisperRequest
        {
            Sender = sender,
            Target = target,
            Message = message,
            PreviousCooldownRound = previousCooldownRound,
            Round = currentRound,
            EnergyCost = energyCost
        };
        // 演出开着的时候这次会晤会整段演在舞台上，横幅别再剧透一遍
        if (!StoryTeller.SuppressLegacy)
            UISystemMessageShow.Show($"{AIAgent.GetStageName(sender)}对{AIAgent.GetStageName(target)}说悄悄话：{message}");

        if (IsBusy(target))
        {
            Enqueue(req);
            ResolveCycleIfAny();
        }
        else
        {
            // 目标空闲，立即单独发起回复
            _ = RunReplyAsync(req);
        }

        string result = await req.Tcs.Task;
        return result;
    }

    private static void Enqueue(WhisperRequest req)
    {
        if (!waiters.TryGetValue(req.Target, out var list))
        {
            list = new List<WhisperRequest>();
            waiters[req.Target] = list;
        }
        list.Add(req);
        waitingOn[req.Sender] = req;
        waitOrder.Add(req);
    }

    /// <summary>单向转投：只进目标队列，不建立等待边，不参与死锁检测；回复只上横幅，不再返回给发送方工具。</summary>
    private static void EnqueueOrphan(WhisperRequest req)
    {
        if (req == null || req.Sender == req.Target) return;
        if (!waiters.TryGetValue(req.Target, out var list))
        {
            list = new List<WhisperRequest>();
            waiters[req.Target] = list;
        }
        list.Add(req);
    }

    private static async Task RunReplyAsync(WhisperRequest req)
    {
        SetBusy(req.Target, true);
        try
        {
            string reply = "";
            try
            {
                reply = await ReplyProvider(req.Target, req.Sender, req.Message);
            }
            catch (Exception ex)
            {
                reply = $"（回复失败：{ex.Message}）";
            }

            if (string.IsNullOrWhiteSpace(reply)) reply = "（无回复）";
            req.Tcs.TrySetResult($"{AIAgent.GetStageName(req.Target)}的悄悄话回复：{reply}");
            // 同上：回复也已经由舞台演出来了
            if (!StoryTeller.SuppressLegacy)
                UISystemMessageShow.Show($"{AIAgent.GetStageName(req.Target)}回复{AIAgent.GetStageName(req.Sender)}的悄悄话：{reply}");
        }
        finally
        {
            SetBusy(req.Target, false);
        }
    }

    private static void ProcessWaiters(int target)
    {
        if (IsBusy(target)) return;
        if (!waiters.TryGetValue(target, out var list) || list.Count == 0) return;

        WhisperRequest next = list[0];
        list.RemoveAt(0);
        RemoveEdge(next.Sender);

        // 发起单独回复；完成后 SetBusy(false) 会继续处理下一位。
        _ = RunReplyAsync(next);
    }

    private static void ResolveCycleIfAny()
    {
        if (waitOrder.Count < 2) return;

        WhisperRequest newest = waitOrder[waitOrder.Count - 1];
        List<WhisperRequest> cycleEdges = new();
        HashSet<int> seen = new() { newest.Sender };
        int cursor = newest.Target;
        bool cycle = false;

        while (waitingOn.TryGetValue(cursor, out WhisperRequest edge))
        {
            cycleEdges.Add(edge);
            if (edge.Sender == newest.Sender)
            {
                cycleEdges.Insert(0, newest);
                cycle = true;
                break;
            }
            if (!seen.Add(cursor)) break;
            cursor = edge.Target;
            if (cursor == newest.Sender)
            {
                cycleEdges.Insert(0, newest);
                cycle = true;
                break;
            }
        }

        if (!cycle || cycleEdges.Count < 2) return;

        if (cycleEdges.Count == 2)
        {
            // A→B 且 B→A：不直接交换消息（那会浪费一次对话机会），按环形等待处理：
            // 只强制最早那条返回“繁忙”，让剩下的那条等对方忙完后正常得到真实回复。
            WhisperRequest oldest = cycleEdges[0];
            for (int i = 1; i < cycleEdges.Count; i++)
            {
                if (waitOrder.IndexOf(cycleEdges[i]) < waitOrder.IndexOf(oldest))
                    oldest = cycleEdges[i];
            }

            WhisperRequest other = cycleEdges[0] == oldest ? cycleEdges[1] : cycleEdges[0];
            CompleteRequest(oldest, "繁忙中请等待通话调配：你们互相等待，系统只保留后发起的悄悄话，你这条本次未消耗使用机会，下一回合可继续使用。", refundCooldown: true);
            UISystemMessageShow.Show($"悄悄话互等：{AIAgent.GetStageName(oldest.Sender)}的通话返回繁忙（不消耗机会），{AIAgent.GetStageName(other.Sender)}将等待真实回复。");
        }
        else
        {
            // 环形等待（A→B→C→A）：强制最早那条结束，并把它的话调配转给环上最后一位（C），
            // 让 C→A、B→C、A→C 依次消化。
            WhisperRequest oldest = cycleEdges[0];
            for (int i = 1; i < cycleEdges.Count; i++)
            {
                if (waitOrder.IndexOf(cycleEdges[i]) < waitOrder.IndexOf(oldest))
                    oldest = cycleEdges[i];
            }

            WhisperRequest newestInCycle = cycleEdges[0];
            CompleteRequest(oldest, "繁忙中请等待通话调配：你发送的悄悄话已被系统调配转达，本次未消耗使用机会与升级能量。", refundCooldown: true);

            // 转投：把最早那条悄悄话改为 A→C 的一次单向传达（不占用新的等待边），
            // 排在 C 已有的 B→C 等待之后，实现 C→A、B→C、A→C 的顺序。
            WhisperRequest reroute = new WhisperRequest
            {
                Sender = oldest.Sender,
                Target = newestInCycle.Sender,
                Message = oldest.Message
            };
            EnqueueOrphan(reroute);
            UISystemMessageShow.Show($"悄悄话环形等待：{AIAgent.GetStageName(oldest.Sender)}的悄悄话已调配转给{AIAgent.GetStageName(reroute.Target)}，稍后依次回复。");
        }
    }

    private static void CompleteRequest(WhisperRequest req, string result, bool refundCooldown = false)
    {
        if (req == null) return;
        if (waiters.TryGetValue(req.Target, out var list)) list.Remove(req);
        if (waitingOn.TryGetValue(req.Sender, out var cur) && cur == req) waitingOn.Remove(req.Sender);
        waitOrder.Remove(req);

        // 被系统终止（繁忙 / 环形调配）的悄悄话退还这次冷却机会**和升级能量**，下一回合可继续使用。
        if (refundCooldown)
        {
            if (req.PreviousCooldownRound.HasValue)
                lastWhisperRound[req.Sender] = req.PreviousCooldownRound.Value;
            else
                lastWhisperRound.Remove(req.Sender);

            if (req.EnergyCost > 0f && MarbleManager.Instance != null)
                MarbleManager.Instance.AddUpgradeEnergy(req.Sender, req.EnergyCost);
        }

        req.Tcs.TrySetResult(result);
    }

    private static void RemoveEdge(int sender)
    {
        if (!waitingOn.TryGetValue(sender, out WhisperRequest req)) return;
        waitingOn.Remove(sender);
        waitOrder.Remove(req);
    }

    private static readonly Dictionary<int, int> lastWhisperRound = new();

    private static bool TryAcquireCooldown(int sender, int currentRound, int cooldownRounds, out string error)
    {
        error = null;
        if (cooldownRounds <= 0)
        {
            lastWhisperRound[sender] = currentRound;
            return true;
        }

        // 开局就在冷却中：没记录过时按"第 0 回合用过"算（而不是可以立刻用），
        // 于是第一发悄悄话要等到第 cooldownRounds 回合。
        int last = lastWhisperRound.TryGetValue(sender, out int v) ? v : 0;
        if (currentRound - last < cooldownRounds)
        {
            error = $"悄悄话失败：冷却中，每{cooldownRounds}回合只能使用一次，还需等待 {cooldownRounds - (currentRound - last)} 回合。";
            return false;
        }
        lastWhisperRound[sender] = currentRound;
        return true;
    }
}
