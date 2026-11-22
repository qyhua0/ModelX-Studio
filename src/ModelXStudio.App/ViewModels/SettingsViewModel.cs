// =====================================================================
// 作者：zhenhua
// 功能：设置页逻辑：载入当前设置、保存前做范围校验（端口 1024–65535、上下文长度、镜像地址），
//       选择模型存放目录、展示并打开随程序分发的 llama.cpp 引擎目录（含已装变体与构建号），
//       并提示哪些设置要下次加载模型才生效。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModelXStudio.Core.Models;
using ModelXStudio.Core.Services;

namespace ModelXStudio.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private string _modelsDirectory = "";

    [ObservableProperty]
    private int _serverPort = 8108;

    [ObservableProperty]
    private string _hfMirror = "";

    [ObservableProperty]
    private string _contextSizeOption = "4096";

    [ObservableProperty]
    private string _defaultPreset = "均衡";

    [ObservableProperty]
    private bool _autoLoadLastModel = true;

    [ObservableProperty]
    private string _enginePreference = "自动（推荐）";

    /// <summary>用户指定的 llama.cpp 目录；空表示用随程序分发的 runtime\llama。</summary>
    [ObservableProperty]
    private string _engineRoot = "";

    public List<string> EnginePrefLabels { get; } = new()
    {
        "自动（推荐）",
        "GPU 加速（NVIDIA 显卡）",
        "Vulkan 加速（A 卡 / 核显）",
        "仅 CPU",
    };

    [ObservableProperty]
    private string _savedHint = "";

    public List<string> ContextOptions { get; } = new() { "2048", "4096", "8192", "16384", "32768" };

    // 上下文策略：界面上说人话，存盘只存枚举名（AutoTrim / AskMe）。标签写成常量而不是
    // StartsWith 去猜，改字不会把设置读成另一种策略。
    private const string TrimLabel = "自动省略最旧的几轮（默认）";
    private const string AskLabel = "先问我，别自己删";

    public List<string> ContextStrategyOptions { get; } = new() { TrimLabel, AskLabel };

    [ObservableProperty]
    private string _contextStrategyOption = TrimLabel;

    /// <summary>给回复预留的 token。空着 = 自动（按「最大长度」与窗口 1/4 取小的）。</summary>
    [ObservableProperty]
    private string _contextReserveTokens = "";

    [ObservableProperty]
    private bool _renderMarkdown = true;

    [ObservableProperty]
    private bool _restoreLastChat = true;

    public List<string> MirrorOptions { get; } = new()
    {
        "https://hf-mirror.com",
        "https://huggingface.co"
    };

    /// <summary>选了「自定义」就保留聊天页里调好的单项参数，不强行盖回去。</summary>
    public List<string> PresetLabels { get; } = new() { "快速", "均衡", "高质量", "自定义" };

    /// <summary>当前使用的引擎目录（随程序分发的 runtime\llama\…，没找到时是应当放入的目录）。</summary>
    public string EngineDirectory => App.Engine.ActiveEngineFolder;

    /// <summary>引擎情况一行，例如「CPU 版 + GPU 版 · b5102」或「未找到引擎文件」。</summary>
    public string EngineSummary => App.Engine.EngineSummary;

    public SettingsViewModel()
    {
        var s = App.SettingsService.Current;
        ModelsDirectory = s.ModelsDirectory;
        ServerPort = s.ServerPort;
        HfMirror = s.HfMirror;
        ContextSizeOption = s.ContextSize.ToString();
        ContextStrategyOption = ContextBudget.ParseStrategy(s.ContextStrategy) == ContextStrategy.AskMe ? AskLabel : TrimLabel;
        ContextReserveTokens = s.ContextReserveTokens > 0 ? s.ContextReserveTokens.ToString() : "";
        RenderMarkdown = s.RenderMarkdown;
        RestoreLastChat = s.RestoreLastChat;
        AutoLoadLastModel = s.AutoLoadLastModel;
        EnginePreference = s.EnginePreference switch
        {
            "Cuda" => "GPU 加速（NVIDIA 显卡）",
            "Vulkan" => "Vulkan 加速（A 卡 / 核显）",
            "Cpu" => "仅 CPU",
            _ => "自动（推荐）"
        };
        EngineRoot = s.EngineRoot ?? "";
        DefaultPreset = s.GenerationPreset switch
        {
            "Fast" => "快速",
            "Quality" => "高质量",
            "Custom" => "自定义",
            _ => "均衡"
        };
    }

    [RelayCommand]
    private void Save()
    {
        var s = App.SettingsService.Current;
        s.ModelsDirectory = string.IsNullOrWhiteSpace(ModelsDirectory)
            ? AppSettings.DefaultModelsDirectory()
            : ModelsDirectory.Trim();
        s.ServerPort = ServerPort is >= 1024 and <= 65535 ? ServerPort : 8108;
        s.HfMirror = string.IsNullOrWhiteSpace(HfMirror) ? "https://hf-mirror.com" : HfMirror.Trim();
        s.ContextSize = int.TryParse(ContextSizeOption, out var ctx) ? ctx : 4096;
        s.ContextStrategy = ContextStrategyOption == AskLabel ? "AskMe" : "AutoTrim";
        // 留空 / 写乱码都当「自动」，而不是干脆不给存：否则用户改错了会永远卡在上一个值
        s.ContextReserveTokens = int.TryParse(ContextReserveTokens, out var rsv) && rsv > 0 ? rsv : 0;
        s.RenderMarkdown = RenderMarkdown;
        s.RestoreLastChat = RestoreLastChat;
        s.AutoLoadLastModel = AutoLoadLastModel;
        s.EnginePreference = EnginePreference switch
        {
            "GPU 加速（NVIDIA 显卡）" => "Cuda",
            "Vulkan 加速（A 卡 / 核显）" => "Vulkan",
            "仅 CPU" => "Cpu",
            _ => "Auto"
        };
        s.GenerationPreset = DefaultPreset switch
        {
            "快速" => "Fast",
            "高质量" => "Quality",
            "自定义" => "Custom",
            _ => "Balanced"
        };
        // 这里只重盖温度/top-p/top-k 三项；min-p、重复惩罚、系统提示词这些单项设置归聊天页的「参数」面板
        if (s.GenerationPreset != "Custom") s.Generation.ApplyPreset(s.GenerationPreset);

        s.EngineRoot = string.IsNullOrWhiteSpace(EngineRoot) ? null : EngineRoot.Trim();

        s.EnsureDirectories();     // 换了模型目录就立刻建好，免得下载时报「目录不存在」

        var ok = App.SettingsService.Save();
        App.Engine.RefreshInstalledState();   // 引擎位置可能改了，重新扫一遍
        RefreshEngineInfo();
        SavedHint = ok
            ? "已保存 ✓（端口 / 上下文 / 引擎偏好在下次加载模型时生效）"
            : "保存失败：请检查磁盘空间与该文件夹的写入权限";
    }

    /// <summary>选一个已有的 llama.cpp 目录（里面有 llama-server.exe）。</summary>
    [RelayCommand]
    private void BrowseEngineRoot()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择 llama.cpp 引擎目录（里面要有 llama-server.exe）",
            InitialDirectory = Directory.Exists(EngineRoot) ? EngineRoot : App.Engine.ActiveEngineFolder
        };
        if (dialog.ShowDialog() != true) return;

        EngineRoot = dialog.FolderName;
        App.Engine.RefreshInstalledState();
        RefreshEngineInfo();
        SavedHint = App.Engine.AnyInstalled
            ? $"已选到引擎（{App.Engine.EngineSummary}），记得点「保存设置」"
            : "这个目录里没看到 llama-server.exe，请选 llama.cpp 解压后的那一层";
    }

    /// <summary>回到随程序分发的引擎目录。</summary>
    [RelayCommand]
    private void ClearEngineRoot()
    {
        EngineRoot = "";
        App.Engine.RefreshInstalledState();
        RefreshEngineInfo();
    }

    /// <summary>设置页显示时刷新引擎那一行（切页不会重建 ViewModel）。</summary>
    public void RefreshEngineInfo()
    {
        OnPropertyChanged(nameof(EngineDirectory));
        OnPropertyChanged(nameof(EngineSummary));
        OnPropertyChanged(nameof(UsingBundledEngine));
    }

    /// <summary>没改过引擎位置时显示随包目录，改了则显示用户选的目录。</summary>
    public bool UsingBundledEngine => string.IsNullOrWhiteSpace(EngineRoot);

    partial void OnEngineRootChanged(string value) => OnPropertyChanged(nameof(UsingBundledEngine));

    [RelayCommand]
    private void BrowseModels()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择模型存放目录",
            InitialDirectory = Directory.Exists(ModelsDirectory) ? ModelsDirectory : null
        };
        if (dialog.ShowDialog() == true)
            ModelsDirectory = dialog.FolderName;
    }

    [RelayCommand]
    private void OpenEngineFolder()
    {
        var folder = App.Engine.OpenEngineFolder(App.Engine.RecommendedVariant);
        if (!Directory.Exists(folder))
            ThemedDialog.Warn($"没能打开这个目录，请手动定位后把 llama.cpp 的文件放进去：\n{folder}", "引擎目录");
    }
}
