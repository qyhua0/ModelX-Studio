// =====================================================================
// 作者：zhenhua
// 功能：应用设置数据模型与本机路径约定：%LOCALAPPDATA%\ModelXStudio 下的模型目录、推理引擎目录、
//       对话目录与 settings.json；含各项默认值（端口 8108、hf-mirror 镜像、上下文 4096 等）和目录创建。
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

    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModelXStudio");

    public static string SettingsFilePath => Path.Combine(AppDataDirectory, "settings.json");

    public static string EngineDirectory => Path.Combine(AppDataDirectory, "engine");

    public static string ChatsDirectory => Path.Combine(AppDataDirectory, "chats");

    public static string DefaultModelsDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModelXStudio", "models");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(EngineDirectory);
        Directory.CreateDirectory(ChatsDirectory);
    }
}
