// =====================================================================
// 作者：zhenhua
// 功能：上下文窗口账本。① TokenEstimator：不依赖分词器估算 token 数（CJK 与拉丁分别按经验系数，
//         并可用引擎回报的真实 prompt_tokens 校准）；② ContextBudget：给定上下文长度与回复预留，
//         算出这次请求要占多少 ctx、装不下时该丢掉哪几条最旧的消息、以及该不该提醒用户。
//       单独成文件是因为这套算法决定了「聊着聊着它把前面说过的忘了」还是「直接报错」，必须能不起引擎就断言。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

using ModelXStudio.Core.Models;

namespace ModelXStudio.Core.Services;

/// <summary>
/// 粗略 token 估算（没有分词器，也不能为了显示一个数字去起引擎）。
///
/// 系数来源：主流中文 GGUF（Qwen / DeepSeek / GLM 系 BPE）中文约 1.2~1.6 字/token，
/// 英文约 3.5~4.5 字符/token。取偏保守的一侧（宁可估多 15% 也不要估少导致请求被服务端拒），
/// 再在运行时用引擎真正回报的 prompt_tokens 做一次比例校准（见 <see cref="Calibrate"/>），
/// 校准后同一台机器同一个模型的误差通常在 ±5% 内。
/// </summary>
public static class TokenEstimator
{
    /// <summary>中日韩文字：每个字符算 0.8 token。</summary>
    public const double CjkTokensPerChar = 0.8;

    /// <summary>拉丁字母/数字/标点/空白：每字符算 0.28 token（约 3.6 字符/token）。</summary>
    public const double LatinTokensPerChar = 0.28;

    /// <summary>
    /// 每条消息在对话模板里额外的开销（<c>&lt;|im_start|&gt;role … &lt;|im_end|&gt;</c> 这类标记）。
    /// 各家模板差 2~6 个 token，取 5 个：不算它会让条数多的历史明显低估。
    /// </summary>
    public const int PerMessageOverhead = 5;

    private static bool IsCjk(char c) =>
        (c >= 0x3040 && c <= 0x30FF) ||   // 日文假名
        (c >= 0x3400 && c <= 0x4DBF) ||   // 扩展 A
        (c >= 0x4E00 && c <= 0x9FFF) ||   // 常用汉字
        (c >= 0xAC00 && c <= 0xD7AF) ||   // 韩文音节
        (c >= 0xF900 && c <= 0xFAFF) ||   // 兼容汉字
        (c >= 0xFF00 && c <= 0xFF60);     // 全角标点

    /// <summary>估算一段文本的 token 数（factor 为校准系数，1.0 = 不校准）。</summary>
    public static int Estimate(string? text, double factor = 1.0)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (factor <= 0) factor = 1.0;

        double cjk = 0, other = 0;
        foreach (var c in text)
        {
            if (IsCjk(c)) cjk++;
            else other++;
        }

        // 代码/日志这类「连续符号 + 短词」实际比自然语言更费 token，按整段偏保守直接进 other 计数即可
        var raw = cjk * CjkTokensPerChar + other * LatinTokensPerChar;
        return (int)Math.Ceiling(raw * factor);
    }

    public static int EstimateMessage(ChatMessage message, double factor = 1.0) =>
        Estimate(message.Content, factor) + PerMessageOverhead;

    /// <summary>一串消息的估算（含每条模板开销，不含 system）。</summary>
    public static int EstimateMessages(IEnumerable<ChatMessage> messages, double factor = 1.0)
    {
        var sum = 0;
        foreach (var m in messages) sum += EstimateMessage(m, factor);
        return sum;
    }

    /// <summary>
    /// 用引擎回报的真实值校准估算系数：<c>实际 / 估算</c>。
    /// 夹在 0.4~2.5 之间：某次异常数据（比如服务端把整段模板都算进去了）不该把之后的估算整体带偏。
    /// </summary>
    public static double Calibrate(double currentFactor, int estimated, int actual)
    {
        if (estimated <= 0 || actual <= 0) return currentFactor <= 0 ? 1.0 : currentFactor;
        var raw = (double)actual / estimated;
        var clamped = Math.Clamp(raw, 0.4, 2.5);

        // 平滑一点：单次请求的抖动（同一段文本 ±3%）不该让界面上的百分比跳来跳去
        var prev = currentFactor <= 0 ? 1.0 : currentFactor;
        return Math.Round(prev * 0.7 + clamped * 0.3, 3);
    }
}

/// <summary>上下文装不下时的做法。</summary>
public enum ContextStrategy
{
    /// <summary>自动省略最旧的消息，保留最近的内容继续聊（默认：不打断普通人）。</summary>
    AutoTrim,
    /// <summary>不改内容，先告诉用户快满了、建议开新会话，由用户决定（适合在意「模型必须看到全部上文」的人）。</summary>
    AskMe,
}

/// <summary>一次请求的上下文账本（界面用它画进度条和那句说明）。</summary>
public sealed record ContextPlan
{
    /// <summary>引擎窗口大小（设置里的上下文长度）。</summary>
    public required int ContextSize { get; init; }

    /// <summary>给回复预留的 token。</summary>
    public required int ReserveTokens { get; init; }

    /// <summary>可用于「系统提示词 + 历史 + 本轮输入」的额度。</summary>
    public required int BudgetTokens { get; init; }

    /// <summary>发送前估算的总占用（未截断）。</summary>
    public required int EstTokens { get; init; }

    /// <summary>实际会发出去的估算占用。</summary>
    public required int SentTokens { get; init; }

    /// <summary>参与本轮的历史消息总条数（不含 system）。</summary>
    public required int HistoryCount { get; init; }

    /// <summary>被省略的最旧消息条数。</summary>
    public required int DroppedCount { get; init; }

    /// <summary>system 提示词占用。</summary>
    public required int SystemTokens { get; init; }

    /// <summary>本轮新输入的占用。</summary>
    public required int InputTokens { get; init; }

    /// <summary>是否真的丢掉了消息。</summary>
    public bool Trimmed => DroppedCount > 0;

    /// <summary>即使丢掉全部历史也塞不下（或「先问我」策略下超出额度）。</summary>
    public required bool OverLimit { get; init; }

    /// <summary>占用百分比（按发送量算，封顶 100）。</summary>
    public double Percent => ContextSize <= 0 ? 0 : Math.Min(100, SentTokens * 100.0 / ContextSize);

    /// <summary>还剩多少 token 可用（可能是负数，负数表示已经超了）。</summary>
    public int Remaining => ContextSize - SentTokens;

    /// <summary>界面上一行看得懂的短语，例如「6.2k / 8k · 已省 4 条」。</summary>
    public string ShortLabel
    {
        get
        {
            var label = $"{ContextBudget.K(SentTokens)} / {ContextBudget.K(ContextSize)}（{Percent:0}%）";
            if (Trimmed) label += $" · 已省略最旧 {DroppedCount} 条";
            else if (OverLimit) label += " · 超出上限";
            return label;
        }
    }

    /// <summary>给「i」提示气泡看的完整说明。</summary>
    public string Detail
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"上下文窗口 {ContextBudget.K(ContextSize)} token（在「设置」里改，改完要重新加载模型）。\n");
            sb.Append($"本轮预计占用约 {SentTokens} token：系统提示词 {SystemTokens} + 历史 {HistoryCount - DroppedCount} 条 + 你这条 {InputTokens}。\n");
            sb.Append($"其中给模型回复预留 {ReserveTokens} token，所以历史最多用到 {BudgetTokens}。\n");
            if (Trimmed)
                sb.Append($"最早的 {DroppedCount} 条消息这次没有发给模型 —— 它看不到那部分内容了。重要信息请重新说一遍，或开个新会话。\n");
            else if (OverLimit)
                sb.Append("光系统提示词和当前输入就快放不下了：请开新会话，或把上下文长度调大（要占更多内存）。\n");
            else if (Percent >= 80)
                sb.Append("快到上限了：继续聊下去会开始省略最早的消息。想一直让模型记得全部上下文，建议现在就开个新会话。\n");
            sb.Append("（数字是估算值，误差约 ±10%；每轮发完会用引擎回报的真实值自动校准。）");
            return sb.ToString();
        }
    }

}

/// <summary>按上下文窗口决定这轮发哪些消息。</summary>
public static class ContextBudget
{
    /// <summary>1234 → 1.2k：进度条与会话列表上的数字不至于把人的一行字顶跑。</summary>
    public static string K(int tokens) =>
        tokens >= 1000 ? $"{tokens / 1000.0:0.#}k" : tokens.ToString();

    /// <summary>策略字符串（存在 settings.json 里）→ 枚举；写错或老版本没有时按 AutoTrim。</summary>
    public static ContextStrategy ParseStrategy(string? value) =>
        string.Equals(value, "AskMe", StringComparison.OrdinalIgnoreCase) ? ContextStrategy.AskMe : ContextStrategy.AutoTrim;

    /// <summary>
    /// 给回复预留多少 token：用户设了「最大长度」就按它（真的会用到那么多），
    /// 但要留出至少一半窗口给上文，否则一条都发不出去；没设（-1 不限）时按窗口的 1/4 预留，
    /// 并且不超过 1024 —— 留太多会让长窗口反而早早开始丢历史。
    /// </summary>
    public static int ReserveFor(int contextSize, int maxTokens)
    {
        if (contextSize <= 0) return 0;
        var cap = Math.Max(64, contextSize / 2);
        if (maxTokens > 0) return Math.Min(maxTokens, cap);
        return Math.Min(1024, Math.Max(128, contextSize / 4));
    }

    /// <summary>
    /// 算账并给出这次要发的历史。
    /// <paramref name="history"/> 不含 system、不含本轮还没生成的空回复；
    /// <paramref name="userInput"/> 是本轮要发的那句话（可以为空，用于「只想看现在占多少」的场景）。
    /// </summary>
    public static (List<ChatMessage> Kept, ContextPlan Plan) Apply(
        string systemPrompt,
        IReadOnlyList<ChatMessage> history,
        string userInput,
        int contextSize,
        int reserveTokens,
        ContextStrategy strategy,
        double calibration = 1.0)
    {
        history ??= Array.Empty<ChatMessage>();
        var sys = TokenEstimator.Estimate(systemPrompt, calibration);
        var input = TokenEstimator.Estimate(userInput, calibration);
        var histTokens = TokenEstimator.EstimateMessages(history, calibration);

        var budget = Math.Max(0, contextSize - reserveTokens);
        var total = sys + histTokens + input;

        // 「先问我」：内容原样发，只把超没超告诉界面
        if (strategy == ContextStrategy.AskMe)
        {
            return (history.ToList(), new ContextPlan
            {
                ContextSize = contextSize,
                ReserveTokens = reserveTokens,
                BudgetTokens = budget,
                EstTokens = total,
                SentTokens = total,
                HistoryCount = history.Count,
                DroppedCount = 0,
                SystemTokens = sys,
                InputTokens = input,
                OverLimit = total > budget,
            });
        }

        if (total <= budget || history.Count == 0)
        {
            return (history.ToList(), new ContextPlan
            {
                ContextSize = contextSize,
                ReserveTokens = reserveTokens,
                BudgetTokens = budget,
                EstTokens = total,
                SentTokens = total,
                HistoryCount = history.Count,
                DroppedCount = 0,
                SystemTokens = sys,
                InputTokens = input,
                OverLimit = sys + input > budget,
            });
        }

        // 从最新往回收，收到装不下为止；至少留最后一条，否则这轮没法回答
        var kept = new List<ChatMessage>();
        var used = sys + input;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var cost = TokenEstimator.EstimateMessage(history[i], calibration);
            if (kept.Count > 0 && used + cost > budget) break;
            kept.Insert(0, history[i]);
            used += cost;
        }

        // 窗口开头尽量落在「用户说的话」上：一上来就是半句 assistant 的回复，模型更容易接着那个语气跑偏
        while (kept.Count > 1 && kept[0].Role == ChatRole.Assistant)
        {
            used -= TokenEstimator.EstimateMessage(kept[0], calibration);
            kept.RemoveAt(0);
        }

        var sent = used;
        return (kept, new ContextPlan
        {
            ContextSize = contextSize,
            ReserveTokens = reserveTokens,
            BudgetTokens = budget,
            EstTokens = total,
            SentTokens = sent,
            HistoryCount = history.Count,
            DroppedCount = history.Count - kept.Count,
            SystemTokens = sys,
            InputTokens = input,
            OverLimit = sys + input > budget,
        });
    }

    /// <summary>
    /// 只看现在这个会话占了多少（不算本轮输入）：聊天页底部那根进度条平时显示的就是它。
    /// </summary>
    public static ContextPlan Preview(string systemPrompt, IReadOnlyList<ChatMessage> history,
        int contextSize, int reserveTokens, ContextStrategy strategy, double calibration = 1.0) =>
        Apply(systemPrompt, history, "", contextSize, reserveTokens, strategy, calibration).Plan;
}
