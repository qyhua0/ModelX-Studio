// =====================================================================
// 作者：zhenhua
// 功能：模型库页逻辑：推理引擎（随包分发的 llama.cpp）缺失与只有 CPU 版时的指引（打开目录 /
//       复制官方下载地址 / 重新扫描，全程不在程序里下载）、精选模型卡片（选量化档、下载/取消/续传、
//       按本机硬件给适配标签）、本地模型加载/删除/定位目录、按直链导入模型。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
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

    /// <summary>下载百分比文字（如「57%」，拿不到总大小时写「下载中」）。与 Percent 分开绑：
    /// 界面给这一格固定宽度，数字位数变化就不会推着整行左右抖。</summary>
    [ObservableProperty]
    private string _percentText = "";

    /// <summary>下载速度文字（如「12.4 MB/s」），同上单独绑、单独占固定宽度。</summary>
    [ObservableProperty]
    private string _speedText = "";

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

    /// <summary>适配等级，界面用它画指示灯（绿/蓝/黄/红/灰），Text 只负责说原因。</summary>
    [ObservableProperty]
    private ModelFit _fitLevel = ModelFit.Unknown;

    /// <summary>根据本机显卡/内存给当前选中量化档打适配标签。</summary>
    public void UpdateFit()
    {
        var size = SelectedOption?.Model.ApproxBytes ?? 0;
        var (fit, text) = ModelRecommender.Evaluate(size, App.SystemInfo.Hardware);
        FitLevel = fit;
        FitText = text;
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
        PercentText = "";
        SpeedText = "";
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

        // Progress<T> 自带「创建时所在线程（界面线程）」的调度，回调里不需要再 Dispatcher.Invoke。
        // 百分比和速度各绑各的（不再拼成一句 StateText）：界面给它们各自固定宽度，
        // 数字位数变了只动自己那一格的左边缘，不会整行左右跳。
        var progress = new Progress<DownloadProgress>(p =>
        {
            Percent = p.Percent ?? 0;
            PercentText = p.Percent is double pc ? $"{pc:0}%" : "下载中";
            SpeedText = p.SpeedText;
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
            PercentText = "";
            SpeedText = "";
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

    /// <summary>适配等级，界面用它画指示灯（与精选模型卡片同一表示）。</summary>
    [ObservableProperty]
    private ModelFit _fitLevel = ModelFit.Unknown;

    public LocalModelVm(LocalModel model)
    {
        Model = model;
        UpdateFit();
    }

    public void UpdateFit()
    {
        var (fit, text) = ModelRecommender.Evaluate(Model.SizeBytes, App.SystemInfo.Hardware);
        FitLevel = fit;
        FitText = text;
    }

    [RelayCommand]
    private Task Load() => App.Models.LoadModelAsync(Model.FilePath);

    [RelayCommand]
    private void Delete()
    {
        if (!ThemedDialog.Confirm($"删除模型文件「{Model.DisplayName}」（{Model.SizeText}）？", "删除模型", "删除"))
            return;

        try
        {
            File.Delete(Model.FilePath);
            App.Models.Refresh();
        }
        catch (Exception ex)
        {
            ThemedDialog.Error($"删除失败：{ex.Message}", "删除模型");
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
    private bool _showEngineMissing;

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
        // 引擎是随包文件：只有“找不到”才需要横幅，模型加载失败应该看服务页/日志
        ShowEngineMissing = !App.Engine.AnyInstalled;
        EngineBusy = state.Status == EngineStatus.Loading;
        UpdateRunningBadges();
    }

    /// <summary>打开应当存放 llama.cpp 的目录（不存在时会先建好）。</summary>
    [RelayCommand]
    private void OpenEngineFolder() => App.Engine.OpenEngineFolder(App.Engine.RecommendedVariant);

    /// <summary>把 llama.cpp 官方发布页复制到剪贴板，并说明要哪个包、放到哪一层。</summary>
    [RelayCommand]
    private void CopyEngineLink()
    {
        var v = App.Engine.RecommendedVariant;
        var page = EngineManager.ReleasesPage;
        try
        {
            Clipboard.SetText(page);
            ThemedDialog.Info(
                "下载地址已复制到剪贴板。\n" +
                $"需要的是 {EngineManager.AssetHint(v)}。\n" +
                $"解压后把 llama-server.exe 和全部 .dll 放在这一层：\n{App.Engine.FolderFor(v)}\n" +
                "放好后点「我已放好，重新检查」即可，不用重启程序。",
                "获取引擎文件");
        }
        catch (Exception ex)
        {
            ThemedDialog.Warn($"复制失败（{ex.Message}），请手动访问：\n{page}", "获取引擎文件");
        }
    }

    /// <summary>按提示把引擎文件放好后重新扇描，不用重启程序。</summary>
    [RelayCommand]
    private void RescanEngine()
    {
        App.Engine.RefreshInstalledState();
        ShowGpuUpgrade = App.Engine.GpuUpgradeAvailable;
        EngineStatusText = App.Engine.AnyInstalled
            ? $"找到引擎了（{App.Engine.EngineSummary}），现在可以加载模型"
            : App.Engine.MissingEngineMessage;
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

    /// <summary>加载模型（供聊天页 / 服务页 / 自动恢复调用）。引擎文件缺失时会拿到放置指引而非报错代码。</summary>
    public async Task LoadModelAsync(string path)
    {
        await App.Engine.StartModelAsync(path, CancellationToken.None);
        App.Chat.RefreshModelOptions();
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
            ThemedDialog.Warn("请输入以 .gguf 结尾的模型直链。", "导入模型");
            return;
        }

        string fileName;
        try
        {
            fileName = Path.GetFileName(new Uri(url.Split('?')[0]).LocalPath);
        }
        catch (UriFormatException)
        {
            ThemedDialog.Warn("这个链接格式不对，请粘贴以 http:// 或 https:// 开头的 .gguf 直链。", "导入模型");
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
            ThemedDialog.Error($"导入失败：{ex.Message}", "导入模型");
        }
    }
}
