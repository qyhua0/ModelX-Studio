// =====================================================================
// 作者：zhenhua
// 功能：扫描本机模型目录下的 .gguf 文件（含子目录），产出文件名、路径、大小、修改时间，
//       按修改时间倒序返回，供「模型库 / 聊天 / 本地服务」三个页面的下拉列表使用。
// =====================================================================

using ModelXStudio.Core.Models;

namespace ModelXStudio.Core.Services;

public sealed record LocalModel(string FileName, string FilePath, long SizeBytes, DateTime ModifiedAt)
{
    public string DisplayName => Path.GetFileNameWithoutExtension(FileName);
    public string SizeText => SizeBytes >= 1024L * 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024 / 1024:0.00} GB"
        : $"{SizeBytes / 1024.0 / 1024:0.0} MB";
}

/// <summary>扫描本地模型目录中的 .gguf 文件。</summary>
public sealed class LocalModelScanner
{
    public List<LocalModel> Scan(string modelsDirectory)
    {
        var list = new List<LocalModel>();
        try
        {
            if (!Directory.Exists(modelsDirectory)) return list;
            foreach (var file in Directory.EnumerateFiles(modelsDirectory, "*.gguf", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(file);
                    list.Add(new LocalModel(fi.Name, file, fi.Length, fi.LastWriteTime));
                }
                catch
                {
                    // 单个文件不可读则跳过
                }
            }
        }
        catch
        {
            // 目录不可访问时返回空列表
        }

        return list
            .OrderByDescending(m => m.ModifiedAt)
            .ToList();
    }
}
