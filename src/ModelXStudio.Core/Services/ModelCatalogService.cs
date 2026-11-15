// =====================================================================
// 作者：zhenhua
// 功能：读取内嵌资源 catalog.json 得到精选模型目录，并按来源站点（huggingface / modelscope）
//       与设置里的镜像域名拼出可直接下载的模型直链。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

using System.Reflection;
using System.Text.Json;
using ModelXStudio.Core.Models;

namespace ModelXStudio.Core.Services;

/// <summary>加载内置精选模型目录（EmbeddedResource: Resources/catalog.json）。</summary>
public sealed class ModelCatalogService
{
    private static readonly string ResourceName =
        typeof(ModelCatalogService).Assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("catalog.json", StringComparison.OrdinalIgnoreCase)) ?? "";

    public List<CatalogEntry> Entries { get; }

    public ModelCatalogService()
    {
        Entries = new List<CatalogEntry>();
        try
        {
            if (ResourceName.Length > 0)
            {
                using var stream = typeof(ModelCatalogService).Assembly.GetManifestResourceStream(ResourceName);
                if (stream != null)
                {
                    var root = JsonSerializer.Deserialize<CatalogRoot>(stream,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (root?.Models != null) Entries = root.Models;
                }
            }
        }
        catch
        {
            // 目录解析失败不影响应用其它功能
        }
    }

    /// <summary>根据设置构造下载 URL。huggingface 源会套用镜像域名。</summary>
    public string BuildDownloadUrl(CatalogEntry entry, QuantOption file, AppSettings settings)
    {
        if (string.Equals(entry.Source, "modelscope", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://modelscope.cn/models/{entry.Repo}/resolve/master/{Uri.EscapeDataString(file.Filename)}";
        }

        var mirror = string.IsNullOrWhiteSpace(settings.HfMirror) ? "https://huggingface.co" : settings.HfMirror.TrimEnd('/');
        if (!mirror.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            mirror = "https://" + mirror;
        return $"{mirror}/{entry.Repo}/resolve/main/{Uri.EscapeDataString(file.Filename)}";
    }
}
