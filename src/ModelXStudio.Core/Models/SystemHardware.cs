// =====================================================================
// 作者：zhenhua
// 功能：硬件信息模型与「这台机器跑得动哪个模型」的判定逻辑：CPU/内存/显卡显存的数据结构，
//       以及 ModelRecommender 给出的适配等级（显存全载 / CPU 可跑 / 偏紧 / 跑不动）和给人看的建议文案。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

namespace ModelXStudio.Core.Models;

public enum GpuVendor { Nvidia, Amd, Intel, Other }

public sealed class GpuInfo
{
    public string Name { get; set; } = "";
    /// <summary>显存（GB），null 表示无法精确检测（WMI 的 AdapterRAM 上限 4GB）。</summary>
    public double? VramGb { get; set; }
    public GpuVendor Vendor { get; set; } = GpuVendor.Other;
    public bool IsDiscrete { get; set; }

    public string VramText => VramGb is > 0 ? $"{VramGb:0.#} GB" : "显存未知";

    public static GpuVendor GuessVendor(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("rtx") || n.Contains("gtx") || n.Contains("quadro") || n.Contains("tesla"))
            return GpuVendor.Nvidia;
        if (n.Contains("radeon") || (n.Contains("amd") && n.Contains("graphics")))
            return GpuVendor.Amd;
        if (n.Contains("intel") || n.Contains("iris") || n.Contains("uhd") || n.Contains("arc") || n.Contains("hd graphics"))
            return GpuVendor.Intel;
        return GpuVendor.Other;
    }

    public static bool GuessDiscrete(string name, GpuVendor vendor)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("arc")) return true;                                  // Intel Arc 是独显
        if (n.Contains("uhd") || n.Contains("iris") || n.Contains("hd graphics")) return false;
        if (vendor == GpuVendor.Amd && !n.Contains("rx")) return false;      // Radeon 核显通常不带 RX
        return vendor is GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Other && !n.Contains("basic render");
    }
}

/// <summary>一次系统环境检测结果。</summary>
public sealed class SystemHardware
{
    public string CpuName { get; set; } = "";
    public int PhysicalCores { get; set; }
    public int LogicalCores { get; set; }
    public double RamGb { get; set; }
    public List<GpuInfo> Gpus { get; set; } = new();

    public bool Detected => RamGb > 0 || Gpus.Count > 0;

    /// <summary>显存最大且已知的独显。</summary>
    public GpuInfo? BestDiscrete =>
        Gpus.Where(g => g.IsDiscrete && g.VramGb is > 0).MaxBy(g => g.VramGb!.Value);
}

/// <summary>
/// 模型与本机硬件的适配等级。界面用它画「指示灯」（Themes/Dark.xaml 的 Led*Core / Led*Halo，
/// 由 Converters.ModelFitToLedConverter 映射）：绿＝能整份塞进显存、蓝＝内存跑没问题、
/// 黄＝紧张、红＝跑不动、灰＝还没检测到硬件。
/// </summary>
public enum ModelFit { GpuFast, CpuOk, Tight, TooBig, Unknown }

/// <summary>
/// 根据显存/内存给模型打「适配等级」并生成给普通用户看的建议文案。
/// 注意：文案里不要放 🟢🔵🟡⚠️ 这类 emoji —— WPF 的文字渲染不支持彩色字体（COLR），
/// 它们只会被画成一个灰白空心圈或方块，看着像「灯没亮」。等级请交给 Fit 字段，由界面画真指示灯。
/// </summary>
public static class ModelRecommender
{
    private const double GiB = 1024.0 * 1024 * 1024;

    public static (ModelFit Fit, string Text) Evaluate(long modelBytes, SystemHardware hw)
    {
        if (!hw.Detected || modelBytes <= 0) return (ModelFit.Unknown, "");

        var sizeGb = modelBytes / GiB;
        var best = hw.BestDiscrete;

        if (best?.VramGb != null && sizeGb <= best.VramGb * 0.9)
            return (ModelFit.GpuFast, "可全部装入显卡显存，GPU 加速速度最快");

        if (hw.RamGb > 0 && sizeGb <= hw.RamGb * 0.6)
            return (ModelFit.CpuOk, "内存足够，CPU 可流畅运行");

        if (hw.RamGb > 0 && sizeGb <= hw.RamGb * 0.85)
            return (ModelFit.Tight, "内存偏紧张，建议换更小的量化档");

        if (hw.RamGb > 0)
            return (ModelFit.TooBig, "超出本机能力，不建议下载");

        return (ModelFit.Unknown, "");
    }

    /// <summary>
    /// KV 缓存（上下文本体）粗估，单位 GB。
    /// 准确值是 <c>2 × 层数 × KV 宽度 × n_ctx × 字节数</c>，但这里拿不到层数与 KV 头数（没解析 GGUF），
    /// 所以用模型体积做代理：7B 级 GQA 模型 @4096 约 0.5 GB，非 GQA 的同体积约 2 GB，取中间值 0.28 GB/GiB/4k。
    /// 误差可能到 2 倍，所以文案里写「约」，只用来提醒「你的上下文很吃内存」，不作为硬卡条件。
    /// </summary>
    public static double EstimateKvCacheGb(long modelBytes, int contextSize)
    {
        if (modelBytes <= 0 || contextSize <= 0) return 0;
        return modelBytes / GiB * (contextSize / 4096.0) * 0.28;
    }

    /// <summary>
    /// 加载前的内存预检（用「当下可用」而不是「总量」：总共 16 GB 但浏览器占了 12 GB 才是真实处境）。
    /// 返回 Ok=false 时 <see cref="Text"/> 是一段可直接弹出的话，带可操作的下一步。
    /// 只提醒不拦着：用户可能刚关掉几个程序、或者就是知道自己在干什么，拦死了反而没法用。
    /// </summary>
    public static (bool Ok, string Text) Preflight(long modelBytes, int contextSize, long availableMb)
    {
        if (modelBytes <= 0 || availableMb < 0) return (true, "");

        var modelGb = modelBytes / GiB;
        var kvGb = EstimateKvCacheGb(modelBytes, contextSize);
        var needGb = modelGb + kvGb;
        var availGb = availableMb / 1024.0;

        // 留 5% 余量：加载过程中引擎自己还要几十到几百 MB，刚好持平的常常就是崩在那一下
        if (needGb <= availGb * 0.95)
            return (true, $"模型约 {modelGb:0.#} GB，上下文缓存约 {kvGb:0.#} GB，当下可用 {availGb:0.#} GB，可以加载。");

        var tips = new List<string> { "① 换一个更小的量化档（Q4_K_M 比 Q6/Q8 省三到四成）" };
        if (contextSize > 1024)
            tips.Add($"② 把「设置 → 上下文长度」从 {contextSize} 调成 {Math.Max(1024, contextSize / 2)}");
        tips.Add("③ 先关掉浏览器等占内存的程序（或重启一次系统）");
        if (kvGb > modelGb * 0.5) tips.Add("④ 这个模型不大，大头是上下文：优先调小上下文长度");

        return (false,
            $"模型约 {modelGb:0.#} GB，加上 {contextSize} 上下文的缓存约 {kvGb:0.#} GB，\n" +
            $"但现在只剩 {availGb:0.#} GB 可用，加载很可能失败或被系统关掉。\n\n" +
            "建议：\n" + string.Join("\n", tips));
    }

    /// <summary>生成硬件概况三行文案（检测不到的部分自动省略）。</summary>
    public static (string Line1, string Line2, string Advice) Summarize(SystemHardware hw)
    {
        if (!hw.Detected) return ("", "", "");

        var cores = hw.PhysicalCores > 0 && hw.LogicalCores > 0
            ? $"{hw.PhysicalCores} 核 {hw.LogicalCores} 线程"
            : $"{hw.LogicalCores} 线程";
        var cpuName = string.IsNullOrWhiteSpace(hw.CpuName) ? "处理器" : hw.CpuName.Trim();
        var line1 = $"CPU：{cpuName}（{cores}）";
        if (hw.RamGb > 0) line1 += $"  ·  内存：{hw.RamGb:0.#} GB";

        var gpus = hw.Gpus
            .Select(g => $"{g.Name}{(g.VramGb is > 0 ? $"（显存 {g.VramGb:0.#} GB）" : "（显存 4GB+，未精确识别）")}")
            .ToList();
        var line2 = gpus.Count > 0 ? "显卡：" + string.Join(" ； ", gpus) : "显卡：未检测到";

        string advice;
        var cpuMax = hw.RamGb > 0 ? hw.RamGb * 0.6 : 0;
        var best = hw.BestDiscrete;
        if (best != null && cpuMax > 0)
            advice = $"建议：≤{best.VramGb! * 0.9:0.#} GB 的模型可全部驻留显存（最快）；" +
                     $"最大约 {cpuMax:0} GB 可用 CPU 运行。优先选 Q4_K_M 量化档。";
        else if (cpuMax > 0)
            advice = $"未检测到 NVIDIA/AMD 独立显卡 → 将以 CPU 推理。" +
                     $"建议选择 ≤{cpuMax:0} GB 的模型，优先 Q4_K_M 量化档。";
        else
            advice = "建议优先选择 5 GB 以下的 Q4_K_M 量化档模型。";

        return (line1, line2, advice);
    }
}
