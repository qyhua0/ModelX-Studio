// =====================================================================
// 作者：zhenhua
// 功能：硬件信息模型与「这台机器跑得动哪个模型」的判定逻辑：CPU/内存/显卡显存的数据结构，
//       以及 ModelRecommender 给出的适配等级（显存全载 / CPU 可跑 / 偏紧 / 跑不动）和给人看的建议文案。
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

public enum ModelFit { GpuFast, CpuOk, Tight, TooBig, Unknown }

/// <summary>根据显存/内存给模型打「适配等级」并生成给普通用户看的建议文案。</summary>
public static class ModelRecommender
{
    private const double GiB = 1024.0 * 1024 * 1024;

    public static (ModelFit Fit, string Text) Evaluate(long modelBytes, SystemHardware hw)
    {
        if (!hw.Detected || modelBytes <= 0) return (ModelFit.Unknown, "");

        var sizeGb = modelBytes / GiB;
        var best = hw.BestDiscrete;

        if (best?.VramGb != null && sizeGb <= best.VramGb * 0.9)
            return (ModelFit.GpuFast, "🟢 可全部装入显卡显存，GPU 加速速度最快");

        if (hw.RamGb > 0 && sizeGb <= hw.RamGb * 0.6)
            return (ModelFit.CpuOk, "🔵 内存足够，CPU 可流畅运行");

        if (hw.RamGb > 0 && sizeGb <= hw.RamGb * 0.85)
            return (ModelFit.Tight, "🟡 内存偏紧张，建议换更小的量化档");

        if (hw.RamGb > 0)
            return (ModelFit.TooBig, "⚠️ 超出本机能力，不建议下载");

        return (ModelFit.Unknown, "");
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
