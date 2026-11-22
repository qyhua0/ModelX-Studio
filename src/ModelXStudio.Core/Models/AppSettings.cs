// =====================================================================
// 作者：zhenhua
// 功能：应用设置数据模型与本机路径约定：%LOCALAPPDATA%\ModelXStudio 下的模型目录、推理引擎目录、
//       对话目录与 settings.json；含各项默认值（端口 8108、hf-mirror 镜像、上下文 4096 等）、
//       生成/微调参数（Generation，见 GenerationParams.cs）和目录创建。
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

    /// <summary>默认生成风格：Fast / Balanced / Quality（改了它会把 Generation 里前三项刷成该档的值）。</summary>
    public string GenerationPreset { get; set; } = "Balanced";

    /// <summary>
    /// 生成/微调参数（温度、top-p、top-k、min-p、重复惩罚、最大长度、种子、系统提示词、模型标识）。
    /// 老版本 settings.json 里没这个节点，反序列化时自动用默认值，不会报错。
    /// </summary>
    public GenerationParams Generation { get; set; } = new();

    /// <summary>上下文长度（token）。</summary>
    public int ContextSize { get; set; } = 4096;

    /// <summary>
    /// 上下文装不下时的做法：AutoTrim（自动省略最旧的消息，默认）/ AskMe（先告诉我去不去，内容原样发）。
    /// 存字符串而不是枚举：老 settings.json 没这个键时取默认值不会炸（见 ContextBudget.ParseStrategy）。
    /// </summary>
    public string ContextStrategy { get; set; } = "AutoTrim";

    /// <summary>给模型回复预留的 token：0 = 自动（按「最大长度」或窗口的 1/4，取小，见 ContextBudget.ReserveFor）。</summary>
    public int ContextReserveTokens { get; set; }

    /// <summary>用 Markdown 渲染回复（关掉就按纯文本显示，排查渲染问题时用）。代码块高亮跟随本开关。</summary>
    public bool RenderMarkdown { get; set; } = true;

    /// <summary>上次使用的模型完整路径。</summary>
    public string? LastModelPath { get; set; }

    /// <summary>是否在应用启动时自动加载上次的模型。</summary>
    public bool AutoLoadLastModel { get; set; } = true;

    /// <summary>启动时回到上次使用的会话（崩溃/被杀后也能回到那一轮，而不是空白新会话）。</summary>
    public bool RestoreLastChat { get; set; } = true;

    /// <summary>上次正在使用的会话 Id。运行中随时刷新，所以程序被杀也能知道该回到哪个会话。</summary>
    public string? LastConversationId { get; set; }

    /// <summary>推理引擎偏好：Auto（有 N 卡自动用 CUDA）/ Cuda / Cpu。</summary>
    public string EnginePreference { get; set; } = "Auto";

    /// <summary>
    /// 用户自己指定的 llama.cpp 引擎目录（里面有 llama-server.exe 即可）。
    /// 留空则用随程序分发的 runtime\llama。指定后优先级最高，方便用已有引擎目录的人。
    /// </summary>
    public string? EngineRoot { get; set; }

    /// <summary>
    /// 数据目录（设置、对话记录、运行状态、崩溃流水都在这）。默认 %LOCALAPPDATA%\ModelXStudio。
    /// 设了环境变量 MODELX_HOME 就用它（相对路径按当前目录算）：便携模式（整目录拷走）和
    /// 自动化验证都需要它 —— 不然测试脚本只能往用户真实数据目录里写。
    /// </summary>
    public static string AppDataDirectory
    {
        get
        {
            var home = Environment.GetEnvironmentVariable("MODELX_HOME");
            if (!string.IsNullOrWhiteSpace(home))
            {
                try { return Path.GetFullPath(home.Trim()); } catch { /* 路径写坏了就回默认，不能让程序起不来 */ }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModelXStudio");
        }
    }

    public static string SettingsFilePath => Path.Combine(AppDataDirectory, "settings.json");

    /// <summary>对话记录目录（每个对话一个 json）。</summary>
    public static string ChatsDirectory => Path.Combine(AppDataDirectory, "chats");

    /// <summary>
    /// 运行状态文件：记录「本次运行开始于何时、加载了哪个模型、在哪个会话、是否正常退出」。
    /// 启动时读到 CleanExit=false 就说明上次是崩溃/被杀 —— 这是崩溃提示唯一的判据（见 RunStateService）。
    /// </summary>
    public static string RunStateFilePath => Path.Combine(AppDataDirectory, "run-state.json");

    /// <summary>崩溃/引擎意外退出的追加式流水，人可读；设置页有「打开所在文件夹」入口。</summary>
    public static string CrashLogFilePath => Path.Combine(AppDataDirectory, "crash-history.log");

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

    /// <summary>
    /// 默认模型目录跟着 <see cref="AppDataDirectory"/> 走：设了 MODELX_HOME（便携模式 / 自动化验证）
    /// 以后模型目录也一起搬过去，不然只有配置和对话搬了、模型目录还在用户配置目录里，
    /// 便携包就带着用户隐私数据（而且测试脚本会往真实目录里写）。
    /// settings.json 里显式写过的绝对路径仍然优先（字段值就是这个，不走这里）。
    /// </summary>
    public static string DefaultModelsDirectory() => Path.Combine(AppDataDirectory, "models");

    /// <summary>只建自己需要写的目录；引擎目录不建（引擎是随包只读文件，不存在时提示用户）。</summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(ChatsDirectory);
    }
}
