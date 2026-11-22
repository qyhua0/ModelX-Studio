// =====================================================================
// 作者：zhenhua
// 功能：生成/微调参数（温度、top-p、top-k、min-p、重复惩罚、最大长度、随机种子、
//       系统提示词、模型标识）的数据模型：默认值、上下限、三档预设的映射与「是否还和某档预设一致」判定。
//       这些值持久化在 settings.json 的 Generation 节点里，聊天页即时改即时生效（不需要重启模型）。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自许可授权。
// =====================================================================

namespace ModelXStudio.Core.Models;

/// <summary>
/// 一组生成/微调参数。数值范围照 llama.cpp 的常用口径，界面用滑块 + 输入框双向编辑，
/// 提交前一律过一遍 <see cref="Clamp"/>：手输 99 这种值不该把模型带崩，也不该写出没法复现的设置。
/// </summary>
public sealed class GenerationParams
{
    /// <summary>温度：越大越随机越发散，0 = 近乎确定。默认 0.6（原来的「均衡」档）。</summary>
    public double Temperature { get; set; } = 0.6;

    /// <summary>top-p（核采样）：累计概率到多少就截断候选。默认 0.92。</summary>
    public double TopP { get; set; } = 0.92;

    /// <summary>top-k：最多只在多少个候选里挑，0 = 不限制。默认 30。</summary>
    public int TopK { get; set; } = 30;

    /// <summary>min-p：概率低于「最优候选 × 这个比例」的直接丢掉，比 top-p 更能滤掉胡话。默认 0.05。</summary>
    public double MinP { get; set; } = 0.05;

    /// <summary>重复惩罚：1 = 不惩罚（llama.cpp 默认）。调大能压复读，太大容易说胡话。默认 1.0。</summary>
    public double RepeatPenalty { get; set; } = 1.0;

    /// <summary>单次回复最多生成多少 token；-1 = 不限（由上下文剩余决定）。</summary>
    public int MaxTokens { get; set; } = -1;

    /// <summary>随机种子：0 = 每次随机（此时不把 seed 发给服务）；填固定值可复现同一段回复。</summary>
    public int Seed { get; set; }

    /// <summary>系统提示词；留空表示用程序内置的那句（见 AppInfo.DefaultSystemPrompt）。</summary>
    public string SystemPrompt { get; set; } = "";

    /// <summary>
    /// 请求体里的 model 字段；留空表示用当前加载的模型文件名。
    /// 本地 llama-server 不看这个字段，但接别的 OpenAI 兼容服务时它是必填项。
    /// </summary>
    public string ModelId { get; set; } = "";

    // ---- 界面滑块的上下限（集中定义：XAML 里的 Minimum/Maximum 要与这里一致，改一处要改两处） ----
    public const double MinTemperature = 0, MaxTemperature = 2;
    public const double MinTopP = 0, MaxTopP = 1;
    public const double MinTopK = 0, MaxTopK = 200;
    public const double MinMinP = 0, MaxMinP = 0.5;
    public const double MinRepeatPenalty = 1, MaxRepeatPenalty = 2;
    public const int MinMaxTokens = -1, MaxMaxTokens = 32768;
    public const int MinSeed = 0, MaxSeed = 999_999_999;

    /// <summary>把每个值夹回合法区间（NaN / 负数 / 手输 99 都在这一步消化掉）。</summary>
    public void Clamp()
    {
        Temperature = ClampD(Temperature, MinTemperature, MaxTemperature);
        TopP = ClampD(TopP, MinTopP, MaxTopP);
        TopK = (int)ClampD(TopK, MinTopK, MaxTopK);
        MinP = ClampD(MinP, MinMinP, MaxMinP);
        RepeatPenalty = ClampD(RepeatPenalty, MinRepeatPenalty, MaxRepeatPenalty);
        MaxTokens = (int)ClampD(MaxTokens, MinMaxTokens, MaxMaxTokens);
        Seed = (int)ClampD(Seed, MinSeed, MaxSeed);
        SystemPrompt = (SystemPrompt ?? "").Trim();
        ModelId = (ModelId ?? "").Trim();
    }

    private static double ClampD(double v, double lo, double hi) => ClampValue(v, lo, hi);

    /// <summary>界面写值时走同一个夹取，保证「滑块拖到底」与「手输 99」结果一致。</summary>
    public static double ClampValue(double v, double lo, double hi) =>
        double.IsNaN(v) || double.IsInfinity(v) ? lo : Math.Min(hi, Math.Max(lo, v));

    /// <summary>深拷贝（测试与「恢复默认」都用它，避免同一实例被两处改）。</summary>
    public GenerationParams Clone() => new()
    {
        Temperature = Temperature,
        TopP = TopP,
        TopK = TopK,
        MinP = MinP,
        RepeatPenalty = RepeatPenalty,
        MaxTokens = MaxTokens,
        Seed = Seed,
        SystemPrompt = SystemPrompt,
        ModelId = ModelId,
    };

    /// <summary>三档预设只规定这三样（其余参数保持用户当前值）。</summary>
    public static (double Temp, double TopP, int TopK) PresetFor(string preset) => preset switch
    {
        "Fast" => (0.3, 0.9, 20),
        "Quality" => (0.8, 0.95, 40),
        _ => (0.6, 0.92, 30),      // Balanced
    };

    /// <summary>按预设改温度/top-p/top-k（就地改，改完调用方负责落盘）。</summary>
    public void ApplyPreset(string preset)
    {
        var (t, p, k) = PresetFor(preset);
        Temperature = t;
        TopP = p;
        TopK = k;
    }

    /// <summary>温度/top-p/top-k 是否正好等于某一档预设（用来决定下拉框显示「自定义」还是档位名）。</summary>
    public bool MatchesPreset(string preset)
    {
        var (t, p, k) = PresetFor(preset);
        return Math.Abs(Temperature - t) < 0.001 && Math.Abs(TopP - p) < 0.001 && TopK == k;
    }

    /// <summary>全部等于默认值（「恢复默认」按钮据此决定要不要置灰提示）。</summary>
    public bool IsDefault => MatchesPreset("Balanced")
        && Math.Abs(MinP - 0.05) < 0.001
        && Math.Abs(RepeatPenalty - 1.0) < 0.001
        && MaxTokens == -1 && Seed == 0
        && string.IsNullOrWhiteSpace(SystemPrompt) && string.IsNullOrWhiteSpace(ModelId);
}
