// =====================================================================
// 作者：zhenhua
// 功能：模型库页逻辑：推理引擎（随包分发的 llama.cpp）缺失与只有 CPU 版时的指引（打开目录 /
//       复制官方下载地址 / 重新扫描，全程不在程序里下载）、精选模型卡片（选量化档、下载/取消/续传、
//       按本机硬件给适配标签、下完把按钮换成「打开目录/重新下载」、覆盖前先确认、本地已有就不再重复下载、
//       空间不够提前说）、本地模型加载/删除/定位目录、按直链导入模型。
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

/// <summary>卡片状态文字的语义等级，界面用它选颜色（灰＝还没开始 / 绿＝下好了 / 橙＝要拿主意 / 红＝出错）。</summary>
public enum CardStateLevel
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>
/// 进度文案的防抖门槛。服务那边已经把速度平均过了，但平均值仍在慢慢漂，
/// 每半秒改一位小数看上去还是「在跳」；变化不到这个比例就当没变，沿用上一版文案。
/// 卡片与导入进度共用同一个门槛。
/// </summary>
internal static class SpeedDisplay
{
    public const double Step = 0.10;
}

public partial class CatalogCardVm : ObservableObject
{
    /// <summary>已下载文件小于预计大小这个比例就当作「没下完的残品」，提示重新下载。
    /// catalog 里的 ApproxBytes 与真实体积差在 ±5% 以内，取 0.7 只抓真正截断的文件，不拿四舍五入误报。</summary>
    private const double SuspiciousRatio = 0.7;

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

    /// <summary>已下载且大小看起来完整。为 true 时界面上不会再出现「下载」两个字。</summary>
    [ObservableProperty]
    private bool _isDownloaded;

    /// <summary>本地有 .part 残片，可以接着下（按钮写「继续下载」）。</summary>
    [ObservableProperty]
    private bool _isResumable;

    /// <summary>主按钮文字：下载 / 继续下载 / 重试 / 重新下载。背后始终是同一个 DownloadCommand。</summary>
    [ObservableProperty]
    private string _actionText = "下载";

    /// <summary>状态文字颜色等级（与 StateText 一起变，绿/橙/红一眼能分「下完了」和「失败了」）。</summary>
    [ObservableProperty]
    private CardStateLevel _stateLevel = CardStateLevel.Info;

    // 磁盘事实与上一次结果，用来拼状态文字与按钮文字；不直接绑，改完都走 RefreshState()
    private long _targetBytes;
    private long _partBytes;
    private long _expectedBytes;
    private DateTime _targetModifiedAt;
    private string? _failMessage;
    private bool _cancelled;

    private double _shownSpeed;

    private CancellationTokenSource? _cts;
    private readonly Action _onFinished;

    /// <summary>是否显示主按钮（下载/继续/重试）。下载中只给「取消」，已下好只给「打开目录 / 重新下载」。</summary>
    public bool HasPrimaryAction => !IsDownloading && !IsDownloaded;

    partial void OnIsDownloadingChanged(bool value) => OnPropertyChanged(nameof(HasPrimaryAction));
    partial void OnIsDownloadedChanged(bool value) => OnPropertyChanged(nameof(HasPrimaryAction));

    public CatalogCardVm(CatalogEntry entry, Action onFinished)
    {
        _entry = entry;
        _onFinished = onFinished;

        foreach (var f in entry.Files) Options.Add(new QuantOptionVm(f));
        SelectedOption = Options.FirstOrDefault(o =>
                             o.Model.Filename == entry.RecommendedFile)
                         ?? Options.FirstOrDefault();
        RefreshState();
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
        // 换了量化档就是另一个文件：上一个档的失败原因、取消状态不该跟着混到新档上
        _failMessage = null;
        _cancelled = false;
        Percent = 0;
        PercentText = "";
        SpeedText = "";
        _shownSpeed = 0;
        RefreshState();
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

    /// <summary>
    /// 重读磁盘，刷新「已下载 / 可续传 / 按钮文字 / 状态文字」。
    /// 下载途中什么都不动（进度每秒变好几次，这里插手会打架）。
    /// 构造、切量化档、下载完成/取消/失败、刷新列表、切回本页都会走到这里。
    /// </summary>
    public void RefreshState()
    {
        if (IsDownloading) return;
        RefreshDiskFacts();
        ApplyIdleText();
    }

    /// <summary>只重读磁盘（目标多大、残片多大、预计多大）并推导两个 bool 与按钮文字，不碰状态文字。</summary>
    private void RefreshDiskFacts()
    {
        var target = TargetPath;
        _expectedBytes = SelectedOption?.Model.ApproxBytes ?? 0;
        _targetBytes = 0;
        _partBytes = 0;
        _targetModifiedAt = DateTime.MinValue;

        if (target.Length > 0)
        {
            try
            {
                if (File.Exists(target))
                {
                    var fi = new FileInfo(target);
                    _targetBytes = fi.Length;
                    _targetModifiedAt = fi.LastWriteTime;
                }
            }
            catch { /* 目标被占用/无权限：当看不见，下面 .part 照读 */ }

            try
            {
                var part = DownloadService.PartPathOf(target);
                if (File.Exists(part)) _partBytes = new FileInfo(part).Length;
            }
            catch { /* 残片读不到不影响主结论 */ }
        }

        // 文件名对了但只下了几百 MB：这就是「看着像下完了其实没下完」。当作没下完，
        // 主按钮变「重新下载」；因为目标确实存在，DownloadCommand 会先弹覆盖确认，不会默默重下。
        IsDownloaded = _targetBytes > 0 &&
                       (_expectedBytes <= 0 || _targetBytes >= _expectedBytes * SuspiciousRatio);
        IsResumable = !IsDownloaded && _partBytes > 0;
        ActionText = ComputeActionText();
    }

    private string ComputeActionText()
    {
        if (_failMessage != null) return "重试";
        if (_partBytes > 0) return "继续下载";
        if (_targetBytes > 0) return "重新下载";
        return "下载";
    }

    /// <summary>按磁盘事实拼「没在下」时的状态文字：大小、时间、残片都写出来，用户才能确认真的下完了。</summary>
    private void ApplyIdleText()
    {
        if (_failMessage != null)
        {
            StateText = "下载失败：" + _failMessage;
            StateLevel = CardStateLevel.Error;
            return;
        }
        if (_cancelled)
        {
            StateText = _partBytes > 0
                ? $"已取消 · 已存 {DownloadService.HumanSize(_partBytes)}，点「继续下载」接着来"
                : "已取消";
            StateLevel = CardStateLevel.Warning;
            return;
        }
        if (IsDownloaded)
        {
            StateText = $"✓ 已下载 {DownloadService.HumanSize(_targetBytes)} · {_targetModifiedAt:yyyy-MM-dd HH:mm}";
            StateLevel = CardStateLevel.Success;
            return;
        }
        if (_targetBytes > 0 && _expectedBytes > 0)
        {
            StateText = $"文件可能不完整（实际 {DownloadService.HumanSize(_targetBytes)} / 预计 " +
                        $"{DownloadService.HumanSize(_expectedBytes)}），建议重新下载";
            StateLevel = CardStateLevel.Warning;
            return;
        }
        if (IsResumable)
        {
            StateText = _expectedBytes > 0
                ? $"上次没下完 · 已存 {DownloadService.HumanSize(_partBytes)} / 预计 " +
                  $"{DownloadService.HumanSize(_expectedBytes)}"
                : $"上次没下完 · 已存 {DownloadService.HumanSize(_partBytes)}";
            StateLevel = CardStateLevel.Warning;
            return;
        }

        StateText = _expectedBytes > 0
            ? $"未下载 · 约 {DownloadService.HumanSize(_expectedBytes)}"
            : "未下载";
        StateLevel = CardStateLevel.Info;
    }

    /// <summary>当前模型是否已被 llama-server 加载（覆盖会因文件占用失败，要提前提醒而不是等报错）。</summary>
    private bool IsInUse() =>
        _targetBytes > 0 &&
        string.Equals(App.Engine.State.ModelPath, TargetPath, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private async Task DownloadAsync()
    {
        var opt = SelectedOption?.Model;
        if (opt == null || IsDownloading) return;

        RefreshDiskFacts();
        var target = TargetPath;
        if (target.Length == 0) return;

        // 本地同名文件已存在（不管是完整还是截断的）都必须先问过用户，不默认覆盖
        var overwrite = _targetBytes > 0;
        if (overwrite && !ConfirmOverwrite(opt)) return;

        var settings = App.SettingsService.Current;
        settings.EnsureDirectories();
        var url = App.CatalogService.BuildDownloadUrl(_entry, opt, settings);

        _failMessage = null;
        _cancelled = false;
        IsDownloading = true;
        StateText = _partBytes > 0 ? "继续下载中…" : "下载中…";
        StateLevel = CardStateLevel.Info;
        Percent = 0;
        PercentText = "";
        SpeedText = "";
        _shownSpeed = 0;
        // 下载中不卖「清除残片」——那个按钮会删正在写的文件
        IsResumable = false;
        _cts = new CancellationTokenSource();

        // Progress<T> 自带「创建时所在线程（界面线程）」的调度，回调里不需要再 Dispatcher.Invoke。
        // 百分比和速度各绑各的（不再拼成一句 StateText）：界面给它们各自固定宽度，
        // 数字位数变了只动自己那一格的左边缘，不会整行左右跳。
        var progress = new Progress<DownloadProgress>(p =>
        {
            Percent = p.Percent ?? 0;
            PercentText = p.Percent is double pc ? $"{pc:0}%" : "下载中";

            // 平均后的速度仍然会慢慢漂；不到门槛就维持原文案，界面上的数字不会每半秒改一位
            if (_shownSpeed <= 0 ||
                Math.Abs(p.BytesPerSecond - _shownSpeed) / _shownSpeed >= SpeedDisplay.Step)
            {
                _shownSpeed = p.BytesPerSecond;
                SpeedText = p.SpeedText;
            }
        });

        try
        {
            await App.DownloadService.DownloadAsync(url, target, progress, _cts.Token, overwrite);
            RefreshDiskFacts();
            StateText = $"✓ 已下载 {DownloadService.HumanSize(_targetBytes)} · {DateTime.Now:yyyy-MM-dd HH:mm}";
            StateLevel = CardStateLevel.Success;
            Percent = 100;
        }
        catch (OperationCanceledException)
        {
            _cancelled = true;
            RefreshDiskFacts();
            ApplyIdleText();
        }
        catch (Exception ex)
        {
            _failMessage = ex.Message;
            RefreshDiskFacts();
            ApplyIdleText();
        }
        finally
        {
            IsDownloading = false;
            Percent = 0;
            PercentText = "";
            SpeedText = "";
            _shownSpeed = 0;
            _cts?.Dispose();
            _cts = null;
            // 以磁盘为准再算一次按钮：下完了就只剩「打开目录 / 重新下载」，不会再看见「下载」
            RefreshDiskFacts();
            _onFinished();
        }
    }

    /// <summary>
    /// 覆盖前的确认：先说清楚本地已有什么、多大、是不是还在跑，
    /// 把「静默替掉用户几个 GB 的文件」这条路堵死。不同意就一个字节也写不下去。
    /// </summary>
    private bool ConfirmOverwrite(QuantOption opt)
    {
        var lines = new List<string>
        {
            $"本地已经有一份「{opt.Filename}」：",
            "  " + DownloadService.HumanSize(_targetBytes)
                + (_expectedBytes > 0 ? $"（预计 {DownloadService.HumanSize(_expectedBytes)}）" : "")
                + (_targetModifiedAt > DateTime.MinValue ? $"・{_targetModifiedAt:yyyy-MM-dd HH:mm}" : ""),
            "",
        };

        if (_expectedBytes > 0 && _targetBytes < _expectedBytes * SuspiciousRatio)
            lines.Add("这份比预期小很多，很可能上次没下完，重新下载是安全的选择。");
        else if (IsInUse())
            lines.Add("这个模型正在运行中，覆盖很可能失败（建议先到「本地服务」页停止）。");
        else
            lines.Add("重新下载会把它替掉；下载期间它仍可正常使用，全部下完才会覆盖。");

        if (_partBytes > 0)
            lines.Add($"另有一份未完成的残片（{DownloadService.HumanSize(_partBytes)}），这次会接着用它续传。");

        lines.Add("");
        lines.Add("确定要覆盖下载吗？");
        return ThemedDialog.Confirm(string.Join("\n", lines), "已有同名模型", "覆盖下载", "先不下载");
    }

    [RelayCommand]
    private void CancelDownload() => _cts?.Cancel();

    /// <summary>在资源管理器里选中这个模型文件（下完之后「它到底存哪了」的一键答案）。</summary>
    [RelayCommand]
    private void ShowFile()
    {
        var target = TargetPath;
        if (target.Length == 0 || !File.Exists(target))
        {
            ThemedDialog.Warn("这个文件本地还不存在，先下载。", "打开目录");
            return;
        }
        try
        {
            Process.Start("explorer.exe", $"/select,\"{target}\"");
        }
        catch
        {
            // 忽略打开资源管理器失败
        }
    }

    /// <summary>删掉 .part 残片（不想继续一个下了一半的文件时），下次就得从头下。</summary>
    [RelayCommand]
    private void ClearPartial()
    {
        RefreshDiskFacts();
        if (_partBytes <= 0)
        {
            RefreshState();
            return;
        }

        var part = DownloadService.PartPathOf(TargetPath);
        if (!ThemedDialog.Confirm(
                $"删除未完成的下载残片？\n{DownloadService.HumanSize(_partBytes)}・{Path.GetFileName(part)}\n\n" +
                "删了之后下次要从头下载。", "清除残片", "删除", "保留"))
            return;

        try
        {
            File.Delete(part);
        }
        catch (Exception ex)
        {
            ThemedDialog.Error($"删除失败：{ex.Message}", "清除残片");
        }
        RefreshState();
    }
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

    /// <summary>卡片上的「加载」是明确意图，不再同一道塞一个「要不要加载」；但内存不够得先说，
    /// 不能让人等两三分钟再看它崩。</summary>
    [RelayCommand]
    private void Load()
    {
        if (!ModelLoadGuard.Confirm(Model.FilePath, ModelLoadAsking.PreflightOnly)) return;
        _ = App.Models.LoadModelAsync(Model.FilePath);
    }

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

    /// <summary>直链导入进行中（界面把「导入链接」按钮换成「取消导入」）。</summary>
    [ObservableProperty]
    private bool _isImporting;

    /// <summary>
    /// 导入进度/结果单独占一行，不再写进 EngineStatusText：
    /// 那个字段同时被「缺引擎」横幅用，导完不清掉就会一直挂个「导入中 100%」像还没下完。
    /// </summary>
    [ObservableProperty]
    private string _importStatusText = "";

    private CancellationTokenSource? _importCts;

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

        // 卡片也重读一遍磁盘：用户在本地模型列表里删了文件、或者手动拷进来一份同名的，
        // 上面的「已下载 / 未下载」必须跟着变，不然就会出现「明明删了还写已下载」。
        foreach (var card in Cards) card.RefreshState();

        // 刚下完的模型要立刻能在聊天页选到（构造时 Chat 还没建，所以用 ?.）
        App.Chat?.RefreshModelOptions();
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
        if (url.Length == 0 || IsImporting) return;

        if (!url.Contains(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            ThemedDialog.Warn("请输入以 .gguf 结尾的模型直链。", "导入模型");
            return;
        }

        string fileName;
        try
        {
            var uri = new Uri(url.Split('?')[0]);
            if (!uri.IsAbsoluteUri ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                ThemedDialog.Warn("请粘贴以 http:// 或 https:// 开头的 .gguf 直链。", "导入模型");
                return;
            }
            fileName = Path.GetFileName(uri.LocalPath);
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

        // 同名模型不默默盖：先报多大、什么时候下的，用户不同意就不动那个文件
        var overwrite = File.Exists(target);
        if (overwrite)
        {
            long len = 0;
            DateTime at = DateTime.MinValue;
            try
            {
                var fi = new FileInfo(target);
                len = fi.Length;
                at = fi.LastWriteTime;
            }
            catch { /* 读不到大小不影响结论，照样要确认 */ }

            if (!ThemedDialog.Confirm(
                    $"导入目录里已经有「{fileName}」：\n  {DownloadService.HumanSize(len)}"
                    + (at > DateTime.MinValue ? $"・{at:yyyy-MM-dd HH:mm}" : "")
                    + "\n\n继续导入会覆盖这个文件。要覆盖吗？",
                    "已有同名模型", "覆盖导入", "先不导入"))
                return;
        }

        ImportUrl = "";
        IsImporting = true;
        _importCts = new CancellationTokenSource();
        ImportStatusText = $"导入中 {fileName}";

        // 百分比该实时（它在往上涨，不实时反而像卡住了），但速度那段照旧要门槛：
        // 两个都不门槛会漏百分比，都门槛会让整行字每半抽一下。
        var shownPct = "";
        var shownSpeed = 0d;
        var shownSpeedText = "";
        var progress = new Progress<DownloadProgress>(p =>
        {
            var pct = $"{p.Percent ?? 0:0}%";
            var speedMoved = shownSpeed <= 0 ||
                             Math.Abs(p.BytesPerSecond - shownSpeed) / shownSpeed >= SpeedDisplay.Step;
            if (!speedMoved && pct == shownPct) return;

            if (speedMoved)
            {
                shownSpeed = p.BytesPerSecond;
                shownSpeedText = p.SpeedText;
            }
            shownPct = pct;
            ImportStatusText = $"导入中 {shownPct} · {shownSpeedText}";
        });

        try
        {
            await App.DownloadService.DownloadAsync(url, target, progress, _importCts.Token, overwrite);
            ImportStatusText = $"已导入 {fileName}";
            Refresh();
        }
        catch (OperationCanceledException)
        {
            ImportStatusText = $"已取消导入（{fileName} 的残片已保留，再次导入可续传）";
        }
        catch (Exception ex)
        {
            ImportStatusText = $"导入失败：{ex.Message}";
            ThemedDialog.Error($"导入失败：{ex.Message}", "导入模型");
        }
        finally
        {
            IsImporting = false;
            _importCts?.Dispose();
            _importCts = null;
        }
    }

    [RelayCommand]
    private void CancelImport() => _importCts?.Cancel();
}
