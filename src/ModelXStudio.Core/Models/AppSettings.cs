// =====================================================================
// 作者：zhenhua
// 功能：应用设置数据模型与本机路径约定：%LOCALAPPDATA%\ModelXStudio 下的模型目录、推理引擎目录、
//       对话目录与 settings.json；含各项默认值（端口 8108、hf-mirror 镜像、上下文 4096 等）和目录创建。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

namespace ModelXStudio.Core.Models;

/// <summary>应用设置，持久化到 %LOCALAPPDATA%\ModelXStudio\settings.json。</summary>
public sealed class AppSettings
{
    /// <summary>GGUF 模型存放目录。</summary>
    public string ModelsDirectory { get; set; } = DefaultModelsDirectory();

    /// <summary>本地 OpenAI 兼容服务端口。</summary>
    public int ServerPort { get; set; } = 8108;

    /// <summary>HuggingFace 下载镜像（国内网络建议保留 hf-mirror.com）。</summary>
    public string HfMirror { get; set; } = "https://hf-mirror.com";

    /// <summary>默认生成风格：Fast / Balanced / Quality。</summary>
    public string GenerationPreset { get; set; } = "Balanced";

    /// <summary>上下文长度（token）。</summary>
    public int ContextSize { get; set; } = 4096;

    /// <summary>上次使用的模型完整路径。</summary>
    public string? LastModelPath { get; set; }

    /// <summary>是否在应用启动时自动加载上次的模型。</summary>
    public bool AutoLoadLastModel { get; set; } = true;

    /// <summary>推理引擎偏好：Auto（有 N 卡自动用 CUDA）/ Cuda / Cpu。</summary>
    public string EnginePreference { get; set; } = "Auto";

    /// <summary>
    /// 用户自己指定的 llama.cpp 引擎目录（里面有 llama-server.exe 即可）。
    /// 留空则用随程序分发的 runtime\llama。指定后优先级最高，方便用已有引擎目录的人。
    /// </summary>
    public string? EngineRoot { get; set; }

    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModelXStudio");

    public static string SettingsFilePath => Path.Combine(AppDataDirectory, "settings.json");

    /// <summary>对话记录目录（每个对话一个 json）。</summary>
    public static string ChatsDirectory => Path.Combine(AppDataDirectory, "chats");

    /// <summary>旧版引擎目录（早期版本在线下载 llama.cpp 的地方），仍作为备选存放点，老用户不用搬文件。</summary>
    public static string LegacyEngineDirectory => Path.Combine(AppDataDirectory, "engine");

    /// <summary>
    /// 推理引擎（llama.cpp）根目录，按优先级：随程序分发的 runtime\llama → 旧版 %LOCALAPPDATA% 目录。
    /// 每次调用都重新算：用户往目录里补文件后无需重启程序。
    /// </summary>
    public IEnumerable<string> EngineRootCandidates()
    {
        if (!string.IsNullOrWhiteSpace(EngineRoot)) yield return EngineRoot;
        yield return BundledEngineRoot();
        yield return LegacyEngineDirectory;
    }

    /// <summary>静态回退（没有设置实例时）：只查随包与旧版两个约定目录。</summary>
    public static IEnumerable<string> DefaultEngineRootCandidates()
    {
        yield return BundledEngineRoot();
        yield return LegacyEngineDirectory;
    }

    /// <summary>
    /// 随程序分发的引擎根目录，约定 &lt;程序目录&gt;\runtime\llama。
    /// 从程序所在目录往上逐级找（开发时 exe 在 bin\Debug\net8.0-windows，引擎在仓库根），
    /// 找到含 runtime\llama 的那一层就用它；走到仓库根（有 .sln）还没找到就按约定路径返回。
    /// </summary>
    public static string BundledEngineRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "runtime", "llama");
            if (Directory.Exists(candidate)) return candidate;
            if (File.Exists(Path.Combine(dir.FullName, "ModelXStudio.sln"))) break;
        }
        return Path.Combine(AppContext.BaseDirectory, "runtime", "llama");
    }

    public static string DefaultModelsDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModelXStudio", "models");

    /// <summary>只建自己需要写的目录；引擎目录不建（引擎是随包只读文件，不存在时提示用户）。</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(ChatsDirectory);
    }
}
