// =====================================================================
// 作者：zhenhua
// 功能：内置精选模型目录的数据结构：模型条目（仓库地址、来源站点、标签、推荐量化档）
//       与量化文件（文件名、近似字节数、说明），对应 Resources/catalog.json。
// =====================================================================

namespace ModelXStudio.Core.Models;

/// <summary>精选模型目录条目（内置 catalog.json 反序列化）。</summary>
public sealed class CatalogEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Tagline { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>建议内存（GB），用于给普通用户直观提示。</summary>
    public int RecommendedRamGb { get; set; } = 8;
    public List<string> Tags { get; set; } = new();
    /// <summary>huggingface 或 modelscope。</summary>
    public string Source { get; set; } = "huggingface";
    /// <summary>仓库 id，如 Qwen/Qwen3-4B-GGUF。</summary>
    public string Repo { get; set; } = "";
    /// <summary>可选量化文件。</summary>
    public List<QuantOption> Files { get; set; } = new();
    /// <summary>默认推荐的量化档位文件名。</summary>
    public string RecommendedFile { get; set; } = "";
}

public sealed class QuantOption
{
    public string Filename { get; set; } = "";
    /// <summary>近似文件大小（字节），仅用于展示。</summary>
    public long ApproxBytes { get; set; }
    /// <summary>一句话说明，如「体积最小，速度快」。</summary>
    public string Note { get; set; } = "";
}

public sealed class CatalogRoot
{
    public List<CatalogEntry> Models { get; set; } = new();
}
