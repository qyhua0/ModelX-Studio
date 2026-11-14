// =====================================================================
// 作者：zhenhua
// 功能：模型库页逻辑：推理引擎安装与 GPU(CUDA) 升级提示、精选模型卡片（选量化档、下载/取消/续传、
//       按本机硬件给适配标签）、本地模型加载/删除/定位目录、按直链导入模型。
// =====================================================================

using System.IO;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModelXStudio.Core.Models;
using ModelXStudio.Core.Services;

namespace ModelXStudio.App.ViewModels;

public sealed class QuantOptionVm
{
    public QuantOption Model { get; }

    public QuantOptionVm(QuantOption model) => Model = model;

    public string DisplayText
    {
        get
        {
            var gb = Model.ApproxBytes / 1024.0 / 1024 / 1024;
            var note = string.IsNullOrEmpty(Model.Note) ? "" : $" · {Model.Note}";
            return $"{Model.Filename} · 约 {gb:0.#} GB{note}";
        }
    }
}

public partial class CatalogCardVm : ObservableObject
{
    private readonly CatalogEntry _entry;

    public CatalogEntry Entry => _entry;
    public string TagsText => string.Join(" · ", _entry.Tags);
    public ObservableCollection<QuantOptionVm> Options { get; } = new();

    [ObservableProperty]
    private QuantOptionVm? _selectedOption;

    [ObservableProperty]
    private string _stateText = "未下载";

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private bool _isDownloading;

    [ObservableProperty]
    private bool _isDownloaded;

    private CancellationTokenSource? _cts;
    private readonly Action _onFinished;

    public CatalogCardVm(CatalogEntry entry, Action onFinished)
    {
        _entry = entry;
        _onFinished = onFinished;

        foreach (var f in entry.Files) Options.Add(new QuantOptionVm(f));
        SelectedOption = Options.FirstOrDefault(o =>
                             o.Model.Filename == entry.RecommendedFile)
                         ?? Options.FirstOrDefault();
        UpdateStateForSelection();
        UpdateFit();
    }

    [ObservableProperty]
    private string _fitText = "";

    /// <summary>根据本机显卡/内存给当前选中量化档打适配标签。</summary>
    public void UpdateFit()
    {
        var size = SelectedOption?.Model.ApproxBytes ?? 0;
        FitText = ModelRecommender.Evaluate(size, App.SystemInfo.Hardware).Text;
    }

    partial void OnSelectedOptionChanged(QuantOptionVm? value)
    {
        UpdateStateForSelection();
        UpdateFit();
    }

    public string TargetPath
    {
        get
        {
            var file = SelectedOption?.Model;
            if (file == null) return "";
            return Path.Combine(App.SettingsService.Current.ModelsDirectory, "catalog", _entry.Id, file.Filename);
        }
    }

    private void UpdateStateForSelection()
    {
        if (IsDownloading) return;
        IsDownloaded = TargetPath.Length > 0 && File.Exists(TargetPath);
        StateText = IsDownloaded ? "✓ 已下载" : "未下载";
        Percent = 0;
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        var opt = SelectedOption?.Model;
        if (opt == null || IsDownloading) return;

        var settings = App.SettingsService.Current;
        settings.EnsureDirectories();
        var url = App.CatalogService.BuildDownloadUrl(_entry, opt, settings);
        var target = TargetPath;

        IsDownloading = true;
        StateText = "下载中…";
        Percent = 0;
        _cts = new CancellationTokenSource();

        // Progress<T> 自带「创建时所在线程（界面线程）」的调度，回调里不需要再 Dispatcher.Invoke
        var progress = new Progress<DownloadProgress>(p =>
        {
            Percent = p.Percent ?? 0;
            StateText = p.TotalBytes > 0
                ? $"下载中 {p.Percent:0}% · {p.SpeedText}"
                : $"下载中 {p.ReceivedBytes / 1024.0 / 1024:0} MB · {p.SpeedText}";
        });

        try
        {
            await App.DownloadService.DownloadAsync(url, target, progress, _cts.Token);
            StateText = "✓ 已下载";
            Percent = 100;
            IsDownloaded = true;
        }
        catch (OperationCanceledException)
        {
            StateText = "已取消（再次下载可续传）";
        }
        catch (Exception ex)
        {
            StateText = $"下载失败：{ex.Message}";
        }
        finally
        {
            IsDownloading = false;
            _cts?.Dispose();
            _cts = null;
            _onFinished();
        }
    }

    [RelayCommand]
    private void CancelDownload() => _cts?.Cancel();
}

public partial class LocalModelVm : ObservableObject
{
    public LocalModel Model { get; }

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _fitText = "";

    public LocalModelVm(LocalModel model)
    {
        Model = model;
        UpdateFit();
    }

    public void UpdateFit() =>
        FitText = ModelRecommender.Evaluate(Model.SizeBytes, App.SystemInfo.Hardware).Text;

    [RelayCommand]
    private Task Load() => App.Models.LoadModelAsync(Model.FilePath);

    [RelayCommand]
    private void Delete()
    {
        var result = MessageBox.Show($"删除模型文件「{Model.DisplayName}」（{Model.SizeText}）？",
            "删除模型", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;

        try
        {
            File.Delete(Model.FilePath);
            App.Models.Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除失败：{ex.Message}", "删除模型", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void ShowInFolder()
    {
        try
        {
            Process.Start("explorer.exe", $"/select,\"{Model.FilePath}\"");
        }
        catch
        {
            // 忽略打开资源管理器失败
        }
    }
}

public partial class ModelsViewModel : ObservableObject
{
    public ObservableCollection<CatalogCardVm> Cards { get; } = new();
    public ObservableCollection<LocalModelVm> Locals { get; } = new();

    [ObservableProperty]
    private string _engineStatusText = "";

    [ObservableProperty]
    private bool _showEngineInstall;

    [ObservableProperty]
    private double _enginePercent;

    [ObservableProperty]
    private bool _engineBusy;

    [ObservableProperty]
    private string _importUrl = "";

    [ObservableProperty]
    private bool _showGpuUpgrade;

    [ObservableProperty]
    private bool _hasHardware;

    [ObservableProperty]
    private string _hardwareLine1 = "";

    [ObservableProperty]
    private string _hardwareLine2 = "";

    [ObservableProperty]
    private string _hardwareAdvice = "";

    public ModelsViewModel()
    {
        foreach (var entry in App.CatalogService.Entries)
            Cards.Add(new CatalogCardVm(entry, Refresh));

        App.Engine.StateChanged += state =>
            Application.Current?.Dispatcher.Invoke(() => OnEngineState(state));

        OnEngineState(App.Engine.State);
        Refresh();

        App.SystemInfo.Changed += hw =>
            Application.Current?.Dispatcher.Invoke(() =>
            {
                ApplyHardware(hw);
                ShowGpuUpgrade = App.Engine.GpuUpgradeAvailable;
            });
        ApplyHardware(App.SystemInfo.Hardware);
        ShowGpuUpgrade = App.Engine.GpuUpgradeAvailable;
    }

    /// <summary>检测到 N 卡且只装了 CPU 引擎时，一键升级到 CUDA 版。</summary>
    [RelayCommand]
    private async Task UpgradeToGpuAsync()
    {
        ShowGpuUpgrade = false;
        App.SettingsService.Current.EnginePreference = "Cuda";
        App.SettingsService.Save();

        try
        {
            await App.Engine.EnsureEngineAsync(EngineVariant.Cuda, null, CancellationToken.None);

            // 若当前正用 CPU 版跑着模型，切换到 GPU 版重新加载
            var running = App.Engine.State.ModelPath;
            if (running != null && File.Exists(running))
                await App.Engine.StartModelAsync(running, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // 升级失败要把偏好改回 CPU，否则下次启动还会反复撞同一个失败
            App.SettingsService.Current.EnginePreference = "Cpu";
            App.SettingsService.Save();

            MessageBox.Show($"GPU 引擎升级失败：{ex.Message}\n将继续使用 CPU 版。",
                "GPU 加速", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ApplyHardware(SystemHardware hw)
    {
        var (line1, line2, advice) = ModelRecommender.Summarize(hw);
        HasHardware = hw.Detected;
        HardwareLine1 = line1;
        HardwareLine2 = line2;
        HardwareAdvice = advice;
        foreach (var card in Cards) card.UpdateFit();
        foreach (var vm in Locals) vm.UpdateFit();
    }

    private void OnEngineState(EngineState state)
    {
        EngineStatusText = state.Message;
        ShowEngineInstall = state.Status is EngineStatus.NotInstalled or EngineStatus.Failed;
        EngineBusy = state.Status is EngineStatus.DownloadingEngine or EngineStatus.Loading;
        if (state.Status != EngineStatus.DownloadingEngine) EnginePercent = 0;
        UpdateRunningBadges();
    }

    public void Refresh()
    {
        Locals.Clear();
        foreach (var m in App.Scanner.Scan(App.SettingsService.Current.ModelsDirectory))
            Locals.Add(new LocalModelVm(m));
        UpdateRunningBadges();
    }

    private void UpdateRunningBadges()
    {
        var running = App.Engine.State.ModelPath;
        foreach (var vm in Locals) vm.IsRunning = vm.Model.FilePath == running;
    }

    /// <summary>加载模型（供聊天页 / 服务页 / 自动恢复调用）。引擎缺失时自动安装推荐变体。</summary>
    public async Task LoadModelAsync(string path)
    {
        await App.Engine.StartModelAsync(path, CancellationToken.None);
        App.Chat.RefreshModelOptions();
    }

    [RelayCommand]
    private async Task InstallEngineAsync()
    {
        var progress = new Progress<DownloadProgress>(p =>
        {
            EnginePercent = p.Percent ?? 0;
            EngineStatusText = $"正在下载推理引擎… {p.SpeedText}";
        });

        try
        {
            await App.Engine.EnsureEngineAsync(null, progress, CancellationToken.None);
            ShowGpuUpgrade = App.Engine.GpuUpgradeAvailable;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"引擎安装失败：{ex.Message}\n\n请检查网络后重试。",
                "安装引擎", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void RefreshList()
    {
        Refresh();
        _ = App.SystemInfo.DetectAsync();
    }

    [RelayCommand]
    private async Task ImportFromUrlAsync()
    {
        var url = ImportUrl.Trim();
        if (url.Length == 0) return;

        if (!url.Contains(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("请输入以 .gguf 结尾的模型直链。", "导入模型", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string fileName;
        try
        {
            fileName = Path.GetFileName(new Uri(url.Split('?')[0]).LocalPath);
        }
        catch (UriFormatException)
        {
            MessageBox.Show("这个链接格式不对，请粘贴以 http:// 或 https:// 开头的 .gguf 直链。",
                "导入模型", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 清掉链接里可能带来的非法文件名字符，避免 Path.Combine / 写盘直接抛异常
        foreach (var bad in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(bad, '_');
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "imported.gguf";

        var target = Path.Combine(App.SettingsService.Current.ModelsDirectory, "imports", fileName);
        ImportUrl = "";

        var progress = new Progress<DownloadProgress>(p =>
            EngineStatusText = $"导入中 {p.Percent ?? 0:0}% · {p.SpeedText}");

        try
        {
            await App.DownloadService.DownloadAsync(url, target, progress, CancellationToken.None);
            Refresh();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导入失败：{ex.Message}", "导入模型", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
