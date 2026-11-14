// =====================================================================
// 作者：zhenhua
// 功能：设置页逻辑：载入当前设置、保存前做范围校验（端口 1024–65535、上下文长度、镜像地址），
//       选择模型存放目录、打开引擎目录，并提示哪些设置要下次加载模型才生效。
// =====================================================================

using System.IO;
using System.Diagnostics;
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

    public List<string> EnginePrefLabels { get; } = new() { "自动（推荐）", "GPU 加速（NVIDIA 显卡）", "仅 CPU" };

    [ObservableProperty]
    private string _savedHint = "";

    public List<string> ContextOptions { get; } = new() { "2048", "4096", "8192", "16384", "32768" };

    public List<string> MirrorOptions { get; } = new()
    {
        "https://hf-mirror.com",
        "https://huggingface.co"
    };

    public List<string> PresetLabels { get; } = new() { "快速", "均衡", "高质量" };

    public string EngineDirectory => AppSettings.EngineDirectory;

    public SettingsViewModel()
    {
        var s = App.SettingsService.Current;
        ModelsDirectory = s.ModelsDirectory;
        ServerPort = s.ServerPort;
        HfMirror = s.HfMirror;
        ContextSizeOption = s.ContextSize.ToString();
        AutoLoadLastModel = s.AutoLoadLastModel;
        EnginePreference = s.EnginePreference switch
        {
            "Cuda" => "GPU 加速（NVIDIA 显卡）",
            "Cpu" => "仅 CPU",
            _ => "自动（推荐）"
        };
        DefaultPreset = s.GenerationPreset switch
        {
            "Fast" => "快速",
            "Quality" => "高质量",
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
        s.AutoLoadLastModel = AutoLoadLastModel;
        s.EnginePreference = EnginePreference switch
        {
            "GPU 加速（NVIDIA 显卡）" => "Cuda",
            "仅 CPU" => "Cpu",
            _ => "Auto"
        };
        s.GenerationPreset = DefaultPreset switch
        {
            "快速" => "Fast",
            "高质量" => "Quality",
            _ => "Balanced"
        };

        s.EnsureDirectories();     // 换了模型目录就立刻建好，免得下载时报「目录不存在」

        SavedHint = App.SettingsService.Save()
            ? "已保存 ✓（端口 / 上下文 / 引擎偏好在下次加载模型时生效）"
            : "保存失败：请检查磁盘空间与该文件夹的写入权限";
    }

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
        try
        {
            Directory.CreateDirectory(AppSettings.EngineDirectory);
            Process.Start("explorer.exe", AppSettings.EngineDirectory);
        }
        catch
        {
            // 忽略打开资源管理器失败
        }
    }
}
