// =====================================================================
// 作者：zhenhua
// 功能：聊天页逻辑。四块：
//       ① 会话列表：新建 / 搜索（标题 + 正文）/ 重命名 / 置顶 / 删除 / 导出 Markdown，重启后回到上次那个会话；
//       ② 发送与流式回复：历史交给 ContextBudget 按上下文窗口算账（装不下就按策略丢最旧的或先问用户），
//          流式途中每 2 秒落一次盘（崩溃只能丢两秒），结束后用引擎回报的真实 token 校准估算系数；
//       ③ 上下文可视：底部那根进度条 + 「已省略最旧 N 条」的说明，数字来源与误差都写清楚；
//       ④ 崩溃恢复：启动时读 run-state 回到上次的会话，上次没写完的回复标成「中断」可以原地重新生成，
//          引擎中途死掉（OOM / abort）时把 CrashReporter 归类出的真实原因贴在气泡里。
//       另有「微调参数」面板：温度 / top-p / top-k / min-p / 重复惩罚 / 最大长度 / 随机种子 /
//       系统提示词 / 模型标识，与「快速·均衡·高质量」三档预设联动（手动改任何一项就落到「自定义」），
//       改动自动存进 settings.json。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModelXStudio.Core.Models;
using ModelXStudio.Core.Services;

namespace ModelXStudio.App.ViewModels;

/// <summary>
/// 一条消息的视图模型。只放界面要的派生信息（时间、耗时、速度），
/// 真正要存盘的内容始终在 <see cref="Model" />（Core 的 ChatMessage）上，避免出现两份对不上的文本。
/// </summary>
public partial class ChatMessageVm : ObservableObject
{
    public ChatRole Role { get; init; }
    public bool IsUser => Role == ChatRole.User;
    public bool IsAssistant => Role != ChatRole.User;
    public string RoleLabel => IsUser ? "你" : "ModelX";

    /// <summary>对应的持久化消息（复制、重新生成要靠它定位）。</summary>
    public ChatMessage? Model { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContent))]
    [NotifyPropertyChangedFor(nameof(MarkdownText))]
    private string _content = "";

    public bool HasContent => Content.Length > 0;

    /// <summary>
    /// 交给 Markdown 控件的内容。用户自己打的字不走 markdown（里面可能有路径、下划线、
    /// 代码片段，渲染完反而看不出他原本写了什么），所以气泡上用户那侧用纯文本 TextBlock，
    /// 这里给空串——气泡仍然挂着，但不会白拼一份没人看的文档。
    /// </summary>
    public string MarkdownText => IsUser ? "" : Content;

    /// <summary>这条正在流式输出：气泡尾部显示光标、底下显示实时字数与速度。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    private bool _busy;

    public bool IsFinished => !Busy;

    /// <summary>上次程序/引擎在这条上断掉了（只写了一半）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private bool _interrupted;

    /// <summary>中断时给这一条看的那句话。</summary>
    public string NoticeText => Interrupted ? "这条回复上次没写完（程序被关或内存不够）—— 可以再点一次重新生成" : "";

    public bool HasNotice => Interrupted;

    /// <summary>生成中的实时状态，例如「312 字 · 24 字/秒」。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLive))]
    private string _liveText = "";

    public bool HasLive => LiveText.Length > 0;

    /// <summary>底部那行小字：07-19 14:22 · 3.2 秒 · 186 tok · 58 tok/s。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMeta))]
    private string _meta = "";

    public bool HasMeta => Meta.Length > 0;
}

public partial class ChatViewModel : ObservableObject
{
    /// <summary>全部会话（按创建时的顺序，新建的插最前）。界面上绑的是 <see cref="ShownConversations"/>。</summary>
    public ObservableCollection<Conversation> Conversations { get; } = new();

    /// <summary>
    /// 列表真正显示的那一份：搜索时是子集。不直接筛原集合是为了不丢用户手里的会话，
    /// 也方便写「共 24 个 · 显示 3 个」这种话。
    /// </summary>
    public ObservableCollection<Conversation> ShownConversations { get; } = new();

    /// <summary>搜索框内容：标题或任意一条消息正文命中就算。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChatCountText))]
    [NotifyPropertyChangedFor(nameof(ShowSearchHint))]
    private string _chatSearch = "";

    public string ChatCountText => ChatSearch.Trim().Length == 0
        ? $"{Conversations.Count} 个会话"
        : $"共 {Conversations.Count} 个 · 命中 {ShownConversations.Count} 个";

    /// <summary>WPF 的 TextBox 没有占位符，搜索框空着时靠这面盖一层灰字。</summary>
    public bool ShowSearchHint => ChatSearch.Trim().Length == 0;

    /// <summary>列表空着（一个会话都没有，或搜索没命中）：得说清楚下面为什么是白的。</summary>
    [ObservableProperty]
    private bool _hasNoChats;

    /// <summary>空列表上那句提示（区分「还没建会话」和「搜索没命中」）。</summary>
    public string NoChatsHint => ShowSearchHint ? "还没有对话，点上面的「＋ 新建」开始" : "没有会话命中这个搜索词";

    /// <summary>上次有 N 条回复没写完（启动时统计），在会话区顶部显示一次，点一下就收掉。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRestoreNotice))]
    private string _restoreNotice = "";

    public bool HasRestoreNotice => RestoreNotice.Length > 0;

    /// <summary>本轮真的丢过历史时那行常驻提示（不能只弹一次就没了，否则用户还是不知道它没看见上文）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrimNotice))]
    private string _trimNotice = "";

    public bool HasTrimNotice => TrimNotice.Length > 0;

    /// <summary>会话列表空着 / 引擎没起来时写在中间的引导语。</summary>
    [ObservableProperty]
    private string _awaitLoadHint = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConversation))]
    private Conversation? _selectedConversation;

    public bool HasConversation => SelectedConversation != null;

    /// <summary>当前会话的消息数（列表头部显示，用来判断这个会话有多长）。</summary>
    [ObservableProperty]
    private string _messageCountText = "";

    // ---------- 上下文窗口可视（③） ----------

    /// <summary>本轮发送量占窗口的百分比，进度条用。</summary>
    [ObservableProperty]
    private double _contextPercent;

    /// <summary>「6.2k / 8k（78%）」。</summary>
    [ObservableProperty]
    private string _contextShort = "";

    /// <summary>鼠标停在进度条上的完整解释。</summary>
    [ObservableProperty]
    private string _contextDetail = "";

    /// <summary>八分以上：进度条变黄，提示「可以开新会话了」。</summary>
    [ObservableProperty]
    private bool _contextWarn;

    /// <summary>已经超出额度（或引擎跑的长度比设置里的小）。</summary>
    [ObservableProperty]
    private bool _contextOver;

    public ObservableCollection<ChatMessageVm> Messages { get; } = new();

    [ObservableProperty]
    private string _inputText = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _engineStatusLine = "引擎未就绪";

    [ObservableProperty]
    private bool _engineReady;

    public ObservableCollection<LocalModel> ModelOptions { get; } = new();

    [ObservableProperty]
    private LocalModel? _selectedModel;

    /// <summary>三档预设 + 「自定义」：手动调过任何一个参数就落到「自定义」。</summary>
    public List<string> PresetOptions { get; } = new() { "快速", "均衡", "高质量", "自定义" };

    [ObservableProperty]
    private string _selectedPreset = "均衡";

    private CancellationTokenSource? _cts;
    private bool _syncing;

    /// <summary>
    /// token 估算的校准系数（实际 / 估算）。起一轮用引擎回报的 prompt_tokens 调一次，
    /// 只活在本次运行、也不存进设置：分词器跟着模型走，换个模型这个系数就该重算。
    /// </summary>
    private double _tokenFactor = 1.0;

    /// <summary>本轮流式期间引擎死过一次的信息（socket 只会报「连接关闭」，真实原因在这儿）。</summary>
    private EngineExitInfo? _engineDied;

    /// <summary>视图订阅：新内容需要滚动到底部。</summary>
    public event Action? ScrollRequested;

    public ChatViewModel()
    {
        // out var 写在 foreach 的集合表达式里，作用域只到那一句（CS0103），先接一下再遍历
        var loaded = App.Store.LoadAll(out var interrupted);
        foreach (var c in loaded) Conversations.Add(c);
        RebuildShown();

        if (interrupted > 0)
            RestoreNotice = $"上次有 {interrupted} 条回复没写完（程序被关或内存不够）。标了「没写完」的那几条可以原地重新生成。";

        // 回到上次的会话。App 启动时已经把 run-state 里那个会话写进设置（崩溃时以它为准）；
        // 关掉「回到上次的会话」则开一个新的空会话，不让人重启后还得手动点新建。
        var prefer = App.SettingsService.Current.RestoreLastChat
            ? App.SettingsService.Current.LastConversationId
            : null;
        SelectedConversation = ShownConversations.FirstOrDefault(c => c.Id == prefer)
                               ?? ShownConversations.FirstOrDefault();
        if (SelectedConversation == null) AddConversation();

        RefreshModelOptions();

        // 存量设置里的档位不一定还等于它的默认值（用户手调过），对不上就显示「自定义」，不骗人
        _syncingPreset = true;
        SelectedPreset = ResolvePresetLabel(App.SettingsService.Current.GenerationPreset);
        _syncingPreset = false;
        RaiseParamProps();

        App.Engine.StateChanged += state =>
            Application.Current?.Dispatcher.Invoke(() => OnEngineState(state));
        App.Engine.UnexpectedExit += info =>
            Application.Current?.Dispatcher.Invoke(() => OnEngineUnexpectedExit(info));
    }

    partial void OnSelectedConversationChanged(Conversation? value)
    {
        ReloadMessages();
        TrimNotice = "";
        if (value == null) return;

        // 记住「现在在哪个会话」：被系统杀掉时下次启动就回到这里（run-state.json，跟着自动保存的节奏写）
        App.SettingsService.Current.LastConversationId = value.Id;
        App.SettingsService.Save();
        RememberRunState();
    }

    partial void OnChatSearchChanged(string value) => RebuildShown();

    partial void OnSelectedPresetChanged(string value)
    {
        if (!_syncingPreset)
        {
            var key = LabelToKey(value);
            // 选预设只重盖温度/top-p/top-k 三项，min-p、重复惩罚、系统提示词这些保持用户自己设的
            if (key != "Custom") Gen.ApplyPreset(key);
        }
        RaiseParamProps();
        SaveParamsSoon();
    }

    partial void OnSelectedModelChanged(LocalModel? value)
    {
        if (_syncing || value == null) return;
        if (App.Engine.State.ModelPath == value.FilePath) return;
        _ = App.Models.LoadModelAsync(value.FilePath);
    }

    private static string LabelToKey(string label) => label switch
    {
        "快速" => "Fast",
        "高质量" => "Quality",
        "自定义" => "Custom",
        _ => "Balanced"
    };

    private static string KeyToLabel(string key) => key switch
    {
        "Fast" => "快速",
        "Quality" => "高质量",
        _ => "均衡"
    };

    // ==================== 微调参数（存在 settings.json 的 Generation 节点） ====================

    /// <summary>
    /// 直接读写设置对象本身：引擎启动参数、每次请求的请求体、界面三处取同一份值，
    /// 不会出现「界面写着 0.8、实际跑 0.6」。
    /// </summary>
    private GenerationParams Gen => App.SettingsService.Current.Generation;

    /// <summary>参数面板是否展开。默认收起：不想让普通用户一进来就看见十个旋钮。</summary>
    [ObservableProperty]
    private bool _showParams;

    /// <summary>「参数」按钮上的摘要，面板收起时也能看见现在跑的是什么值。</summary>
    public string ParamsSummary => $"参数 {Gen.Temperature:0.##} · {Gen.TopP:0.##} · {Gen.TopK}";

    /// <summary>参数是自动存的，给一句确认（1.8 秒后消失），免得用户满界面找「保存」按钮。</summary>
    [ObservableProperty]
    private string _paramsSavedHint = "";

    /// <summary>true = 正在由代码同步档位：不要反过来把档位改成「自定义」，也别多写一次盘。</summary>
    private bool _syncingPreset;

    private DispatcherTimer? _saveTimer;
    private DispatcherTimer? _hintTimer;
    private static readonly Random SeedRandom = new();

    /// <summary>温度：越大越发散，0 = 近乎确定。滑块与输入框共用这一个值。</summary>
    public double Temperature
    {
        get => Gen.Temperature;
        set => WriteParam(() => Gen.Temperature = GenerationParams.ClampValue(
            value, GenerationParams.MinTemperature, GenerationParams.MaxTemperature));
    }

    public string TemperatureText
    {
        get => Numb(Gen.Temperature);
        set { if (TryNumb(value, out var v)) Temperature = v; else OnPropertyChanged(); }
    }

    /// <summary>top-p（核采样）。</summary>
    public double TopP
    {
        get => Gen.TopP;
        set => WriteParam(() => Gen.TopP = GenerationParams.ClampValue(value, GenerationParams.MinTopP, GenerationParams.MaxTopP));
    }

    public string TopPText
    {
        get => Numb(Gen.TopP);
        set { if (TryNumb(value, out var v)) TopP = v; else OnPropertyChanged(); }
    }

    /// <summary>top-k：0 = 不限制候选个数。</summary>
    public int TopK
    {
        get => Gen.TopK;
        set => WriteParam(() => Gen.TopK = (int)GenerationParams.ClampValue(value, GenerationParams.MinTopK, GenerationParams.MaxTopK));
    }

    public string TopKText
    {
        get => Gen.TopK.ToString(CultureInfo.InvariantCulture);
        set { if (TryNumb(value, out var v)) TopK = (int)Math.Round(v); else OnPropertyChanged(); }
    }

    /// <summary>min-p：比 top-p 更能抖掉低概率胡话，默认 0.05。</summary>
    public double MinP
    {
        get => Gen.MinP;
        set => WriteParam(() => Gen.MinP = GenerationParams.ClampValue(value, GenerationParams.MinMinP, GenerationParams.MaxMinP));
    }

    public string MinPText
    {
        get => Numb(Gen.MinP);
        set { if (TryNumb(value, out var v)) MinP = v; else OnPropertyChanged(); }
    }

    /// <summary>重复惩罚：1 = 不惩罚。</summary>
    public double RepeatPenalty
    {
        get => Gen.RepeatPenalty;
        set => WriteParam(() => Gen.RepeatPenalty = GenerationParams.ClampValue(
            value, GenerationParams.MinRepeatPenalty, GenerationParams.MaxRepeatPenalty));
    }

    public string RepeatPenaltyText
    {
        get => Numb(Gen.RepeatPenalty);
        set { if (TryNumb(value, out var v)) RepeatPenalty = v; else OnPropertyChanged(); }
    }

    /// <summary>单次回复长度上限，-1 = 不限。</summary>
    public int MaxTokens
    {
        get => Gen.MaxTokens;
        set => WriteParam(() => Gen.MaxTokens = (int)GenerationParams.ClampValue(
            value, GenerationParams.MinMaxTokens, GenerationParams.MaxMaxTokens));
    }

    /// <summary>这一项写成「不限」比 -1 好读，所以输入框显示中文，存的时候还是 -1。</summary>
    public string MaxTokensText
    {
        get => Gen.MaxTokens < 0 ? "不限" : Gen.MaxTokens.ToString(CultureInfo.InvariantCulture);
        set
        {
            var t = (value ?? "").Trim();
            if (t.Length == 0 || t == "不限" || t == "-1") { MaxTokens = -1; return; }
            if (TryNumb(t, out var v)) MaxTokens = (int)Math.Round(v);
            else OnPropertyChanged();
        }
    }

    /// <summary>随机种子，0 = 每次随机（此时不把 seed 发给服务）。</summary>
    public int Seed
    {
        get => Gen.Seed;
        set => WriteParam(() => Gen.Seed = (int)GenerationParams.ClampValue(value, GenerationParams.MinSeed, GenerationParams.MaxSeed));
    }

    public string SeedText
    {
        get => Gen.Seed == 0 ? "随机" : Gen.Seed.ToString(CultureInfo.InvariantCulture);
        set
        {
            var t = (value ?? "").Trim();
            if (t.Length == 0 || t == "随机" || t == "0") { Seed = 0; return; }
            if (TryNumb(t, out var v)) Seed = (int)Math.Round(v);
            else OnPropertyChanged();
        }
    }

    /// <summary>系统提示词；留空用内置的那句。</summary>
    public string SystemPrompt
    {
        get => Gen.SystemPrompt;
        set => WriteParam(() => Gen.SystemPrompt = value ?? "");
    }

    /// <summary>输入框空着时，把真正会用的内置提示词写给用户看，免得他以为现在是空的。</summary>
    public string SystemPromptHint => "留空则用：" + AppInfo.DefaultSystemPrompt;

    /// <summary>请求体里的 model 字段；本地用不着，接别的 OpenAI 兼容服务时必填。</summary>
    public string ModelId
    {
        get => Gen.ModelId;
        set => WriteParam(() => Gen.ModelId = value ?? "");
    }

    public string ModelIdHint => "留空则用当前加载的模型文件名";

    /// <summary>写一个参数：落到「自定义」→ 刷新整串属性 → 排队存盘。</summary>
    private void WriteParam(Action apply)
    {
        apply();
        if (!_syncingPreset && SelectedPreset != "自定义")
        {
            _syncingPreset = true;
            SelectedPreset = "自定义";
            _syncingPreset = false;
        }
        RaiseParamProps();
        SaveParamsSoon();
    }

    /// <summary>档位和参数是两个入口一份数据：任一变化都要把这串属性一起刷新，否则滑块和输入框会各说各话。</summary>
    private void RaiseParamProps()
    {
        foreach (var name in new[]
        {
            nameof(Temperature), nameof(TemperatureText),
            nameof(TopP), nameof(TopPText),
            nameof(TopK), nameof(TopKText),
            nameof(MinP), nameof(MinPText),
            nameof(RepeatPenalty), nameof(RepeatPenaltyText),
            nameof(MaxTokens), nameof(MaxTokensText),
            nameof(Seed), nameof(SeedText),
            nameof(SystemPrompt), nameof(ModelId),
            nameof(ParamsSummary),
        }) OnPropertyChanged(name);
    }

    /// <summary>
    /// 拖滑块时每动一下都写盘会把 settings.json 刷爆，所以防抖 600 ms：手停下来才存一次。
    /// </summary>
    private void SaveParamsSoon()
    {
        _saveTimer ??= BuildTimer(600, () =>
        {
            var s = App.SettingsService.Current;
            s.GenerationPreset = LabelToKey(SelectedPreset);
            s.Generation.Clamp();
            ParamsSavedHint = App.SettingsService.Save() ? "已自动保存 ✓" : "保存失败：这个文件写不进去";
            _hintTimer ??= BuildTimer(1800, () => ParamsSavedHint = "");
            _hintTimer.Stop();
            _hintTimer.Start();
            OnPropertyChanged(nameof(ParamsSummary));
        });
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private static DispatcherTimer BuildTimer(int ms, Action onTick)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => onTick();
        return t;
    }

    /// <summary>设置页可能改了默认档位，回到聊天页时同步一次（切页调用）。</summary>
    public void RefreshParamUi()
    {
        _syncingPreset = true;
        SelectedPreset = ResolvePresetLabel(App.SettingsService.Current.GenerationPreset);
        _syncingPreset = false;
        RaiseParamProps();
    }

    private string ResolvePresetLabel(string key) =>
        key != "Custom" && Gen.MatchesPreset(key) ? KeyToLabel(key) : "自定义";

    private static string Numb(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool TryNumb(string? s, out double v) =>
        double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    /// <summary>展开/收起参数面板。</summary>
    [RelayCommand]
    private void ToggleParams() => ShowParams = !ShowParams;

    /// <summary>一键回到出厂值（含系统提示词与模型标识）。</summary>
    [RelayCommand]
    private void ResetParams()
    {
        App.SettingsService.Current.Generation = new GenerationParams();
        _syncingPreset = true;
        SelectedPreset = "均衡";
        _syncingPreset = false;
        RaiseParamProps();
        SaveParamsSoon();
    }

    /// <summary>随机种子「换一个」：比不同种子的效果时不用自己现编数字。</summary>
    [RelayCommand]
    private void RollSeed() => Seed = SeedRandom.Next(1, 999_999);

    // ==================== 引擎状态 / 手动加载与卸载 ====================

    /// <summary>模型加载中（加载大模型要几十秒，这期间按钮必须钉住）。</summary>
    [ObservableProperty]
    private bool _isModelBusy;

    /// <summary>上次见过的模型路径，用它判断「是不是换了一个模型」。</summary>
    private string? _lastModelPath;

    private void OnEngineState(EngineState state)
    {
        EngineReady = state.Status == EngineStatus.Running;
        EngineStatusLine = state.Status switch
        {
            EngineStatus.Running => "● " + Path.GetFileName(state.ModelPath ?? "") + " 已就绪",
            EngineStatus.Loading => "● 模型加载中…",
            EngineStatus.NotInstalled => "⚠ 推理引擎文件缺失，去「模型库」页看怎么补上",
            _ => state.Message
        };
        IsModelBusy = state.Status == EngineStatus.Loading;

        // 换模型 = 换一个分词器，上次的校准系数作废（不同系模型每字 token 数能差四成）
        if (state.ModelPath != _lastModelPath)
        {
            _tokenFactor = 1.0;
            _lastModelPath = state.ModelPath;
        }
        if (state.Status == EngineStatus.Running) AwaitLoadHint = "";

        var runningPath = state.ModelPath;
        _syncing = true;
        SelectedModel = ModelOptions.FirstOrDefault(m => m.FilePath == runningPath);
        _syncing = false;

        // 引擎起/停会改变「按哪个上下文长度算」，进度条要跟着重算
        UpdateContextPreview();
    }

    /// <summary>
    /// 引擎自己死了（内存不够被系统关掉 / abort）。生成中的那一轮由 SendCoreAsync 把原因贴到气泡里，
    /// 这里只处【没在生成】的情况：那时界面上一点异常都没有，不弹这一下用户只会觉得「模型变笨了」。
    /// </summary>
    private void OnEngineUnexpectedExit(EngineExitInfo info)
    {
        _engineDied = info;
        if (IsBusy) return;

        var advice = CrashReporter.ClassifyEngineExit(info.ExitCode, info.LogTail, info.AvailableMb);
        EngineStatusLine = "⚠ " + advice.Headline;
        var go = ThemedDialog.Confirm(advice.Headline + "\n\n" + advice.Detail,
            "推理引擎退出了", advice.SuggestSmallerModel ? "去模型库换小的" : "打开问题记录", "知道了");
        if (!go) return;
        if (advice.SuggestSmallerModel) App.NavigateRequested?.Invoke("models");
        else App.OpenCrashLog();
    }

    /// <summary>手动卸载：把模型从内存里放掉。大模型挂着几个 G，不卸掉浏览器都要卡。</summary>
    [RelayCommand]
    private void UnloadModel()
    {
        if (IsBusy)
        {
            ThemedDialog.Warn("正在生成回复，先点「停止」。", "暂时不能卸载");
            return;
        }
        var name = Path.GetFileName(App.Engine.State.ModelPath ?? "");
        if (!ThemedDialog.Confirm(
                $"卸载 {name}？\n内存立刻还回来；下次要用的时候在上方下拉里再选它一次就行。\n" +
                "（想每次自己选，去「设置」把「启动时自动加载上次的模型」关掉。）", "卸载模型", "卸载"))
            return;

        App.Engine.Stop();
        AwaitLoadHint = "模型已卸载，内存已释放。在上方下拉里选一个再继续。";
    }

    /// <summary>重新加载当前模型：改了上下文长度必须重载才生效，这个按钮就是那条路。</summary>
    [RelayCommand]
    private async Task ReloadModelAsync()
    {
        var path = App.Engine.State.ModelPath ?? App.SettingsService.Current.LastModelPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ThemedDialog.Warn("还不知道要加载哪个模型：先在上方下拉里选一个。", "重新加载");
            return;
        }
        if (IsBusy)
        {
            ThemedDialog.Warn("正在生成回复，先点「停止」。", "暂时不能重新加载");
            return;
        }

        IsModelBusy = true;
        try
        {
            await App.Models.LoadModelAsync(path);
        }
        finally
        {
            IsModelBusy = false;
        }
    }

    public void RefreshModelOptions()
    {
        var models = App.Scanner.Scan(App.SettingsService.Current.ModelsDirectory);
        ModelOptions.Clear();
        foreach (var m in models) ModelOptions.Add(m);

        _syncing = true;
        SelectedModel = ModelOptions.FirstOrDefault(m => m.FilePath == App.Engine.State.ModelPath);
        _syncing = false;

        // 设置页也可能改了默认档位，切回聊天页时一并同步，不只能靠重启生效
        RefreshParamUi();
        OnPropertyChanged(nameof(RenderMarkdown));
    }

    // ==================== 会话列表（①） ====================

    /// <summary>按搜索词重建列表；置顶的排前。搜索同时看标题和正文：用户只记得「哪次问过 XX」。</summary>
    private void RebuildShown()
    {
        var q = (ChatSearch ?? "").Trim();
        ShownConversations.Clear();
        foreach (var c in Conversations.OrderByDescending(c => c.Pinned))
            if (q.Length == 0 || Matches(c, q)) ShownConversations.Add(c);
        OnPropertyChanged(nameof(ChatCountText));
        HasNoChats = ShownConversations.Count == 0;
        OnPropertyChanged(nameof(NoChatsHint));

        // 两种情要一起处理：① 搜索把当前会话筛掉了 → 跟着选第一个命中的，别让用户对着
        // 列表里看不见的会话打字；② 之前筛成 null 了、现在把搜索清空 → 也得补一个回来，
        // 不然列表恢复了右边还是空白输入区（没会话可选）。
        if (SelectedConversation == null || !ShownConversations.Contains(SelectedConversation))
            SelectedConversation = ShownConversations.FirstOrDefault();

        RefreshHints();
    }

    /// <summary>
    /// 刷新列表上每个会话的副标题（多少条 · 最近用了多少上下文 · 有没有断掉的回复）。
    /// 这些字全部现算，不进 json：会话文件只存内容，显示用的东西存一份就多一份对不上的可能。
    /// </summary>
    private void RefreshHints()
    {
        foreach (var c in Conversations)
        {
            var sb = new StringBuilder(c.Messages.Count + " 条");
            if (c.LastPromptTokens > 0) sb.Append(" · 上文 ").Append(ContextBudget.K(c.LastPromptTokens));
            // 两种「模型少看了内容」的情况分开写：一个是上次崩了，一个是这一轮主动丢了旧消息
            if (c.Messages.Any(m => m.Interrupted)) sb.Append(" · 有没写完的");
            else if (c.TrimmedOnce) sb.Append(" · 省略过旧消息");
            c.ListHint = sb.ToString();
        }
    }

    private static bool Matches(Conversation c, string q) =>
        c.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
        c.Messages.Any(m => m.Content.Contains(q, StringComparison.OrdinalIgnoreCase));

    /// <summary>建一个会话并选中。启动时一个会话都没有也要走它，免得上面那块地方是白的。</summary>
    private Conversation AddConversation()
    {
        var conv = new Conversation { ModelName = CurrentModelName() };
        App.Store.Save(conv);
        Conversations.Insert(0, conv);
        RebuildShown();
        SelectedConversation = conv;
        return conv;
    }

    private static string? CurrentModelName()
    {
        var p = App.Engine.State.ModelPath ?? App.SettingsService.Current.LastModelPath;
        return string.IsNullOrWhiteSpace(p) ? null : Path.GetFileNameWithoutExtension(p);
    }

    [RelayCommand]
    private void NewChat()
    {
        // 搜索框里还有字的时候点「新建」：不清掉搜索的话新建的那个会被筛掉，看起来像「点了没反应」
        ChatSearch = "";
        AddConversation();
        TrimNotice = "";
        RestoreNotice = "";
    }

    /// <summary>删除指定会话（右键菜单与列表上的×都用它）。生成中的会话不给删。</summary>
    [RelayCommand]
    private void DeleteChat(Conversation? conv)
    {
        conv ??= SelectedConversation;
        if (conv == null) return;
        if (IsBusy && ReferenceEquals(conv, SelectedConversation))
        {
            ThemedDialog.Warn("正在生成回复，先点「停止」再删除。", "暂时不能删除");
            return;
        }
        if (!ThemedDialog.Confirm(
                $"删除对话「{conv.Title}」？\n共 {conv.Messages.Count} 条消息，删了以后来找不回来。\n" +
                "想留住内容的话，先用右键菜单里的「导出为 Markdown」。", "删除对话", "删除"))
            return;

        var wasSelected = ReferenceEquals(conv, SelectedConversation);
        var at = Conversations.IndexOf(conv);
        App.Store.Delete(conv.Id);
        Conversations.Remove(conv);
        RebuildShown();
        if (wasSelected)
            SelectedConversation = ShownConversations.Count == 0
                ? AddConversation()      // 删干净了：给一个新的空会话，右边不至于只能看一个空框
                : ShownConversations[Math.Min(at, ShownConversations.Count - 1)];
    }

    /// <summary>重命名。手改过名字以后，第一条消息不再自动抢名字（见 <see cref="ApplyTitle"/>）。</summary>
    [RelayCommand]
    private void RenameChat(Conversation? conv)
    {
        conv ??= SelectedConversation;
        if (conv == null) return;

        var input = ThemedDialog.AskText("给这个会话起个名字（列表里显示得下大约 20 个字）",
            "重命名会话", conv.Title);
        if (input == null) return;
        ApplyTitle(conv, input);
        RebuildShown();     // 搜索框里有字时，改完名字可能就不再命中了，得重筛一次
    }

    /// <summary>置顶/取消置顶。列表只按「置顶 → 新建顺序」排，不按时改来改去（位置乱跳比不能置顶更烦人）。</summary>
    [RelayCommand]
    private void TogglePin(Conversation? conv)
    {
        conv ??= SelectedConversation;
        if (conv == null) return;
        conv.Pinned = !conv.Pinned;
        App.Store.Save(conv);
        RebuildShown();
    }

    /// <summary>
    /// 改标题：截短 + 上锁（用户自己起的名字不再被「第一条消息」覆盖）+ 落盘。
    /// 走 Conversation 自带的 PropertyChanged，列表里那一行会自己刷新，不用重建集合。
    /// </summary>
    private static void ApplyTitle(Conversation conv, string raw)
    {
        var title = raw.Trim();
        if (title.Length == 0) return;
        if (title.Length > 40) title = title[..40];
        conv.Title = title;
        conv.TitleLocked = true;
        conv.UpdatedAt = DateTime.Now;
        App.Store.Save(conv);
    }

    /// <summary>导出成 Markdown：聊天内容要能带走（发给人、贴进笔记），不能锁在软件里。</summary>
    [RelayCommand]
    private void ExportChat(Conversation? conv)
    {
        conv ??= SelectedConversation;
        if (conv == null || conv.Messages.Count == 0)
        {
            ThemedDialog.Info("这个会话还没有内容。", "导出会话");
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出会话",
            FileName = SafeFileName(conv.Title) + ".md",
            Filter = "Markdown 文件 (*.md)|*.md|文本文件 (*.txt)|*.txt",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dlg.FileName, ToMarkdown(conv), new UTF8Encoding(false));
            ThemedDialog.Success($"已导出 {conv.Messages.Count} 条消息：\n{dlg.FileName}", "导出会话");
        }
        catch (Exception ex)
        {
            App.LogError("导出会话", ex);
            ThemedDialog.Error($"导出失败：{ex.Message}", "导出会话");
        }
    }

    private static string ToMarkdown(Conversation c)
    {
        var sb = new StringBuilder();
        sb.Append($"# {c.Title}\n\n");
        var model = c.ModelName ?? CurrentModelName();
        if (!string.IsNullOrWhiteSpace(model)) sb.Append($"模型：{model}  \n");
        sb.Append($"时间：{c.CreatedAt:yyyy-MM-dd HH:mm} ~ {c.UpdatedAt:yyyy-MM-dd HH:mm}  \n");
        sb.Append($"消息：{c.Messages.Count} 条\n\n");
        foreach (var m in c.Messages)
        {
            sb.Append($"## {RoleName(m.Role)} · {m.Timestamp:MM-dd HH:mm}\n\n");
            if (m.Interrupted) sb.Append("> ⚠️ 这条回复当时被中断，内容是半截的。\n\n");
            sb.Append(m.Content.Trim()).Append("\n\n");
        }
        return sb.ToString();
    }

    private static string RoleName(ChatRole r) => r == ChatRole.User ? "你" : r == ChatRole.Assistant ? "ModelX" : "系统";

    /// <summary>把标题里不能当文件名的字符换成下划线（用户常把「怎么做?/为什么」这类句子当标题）。</summary>
    private static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var ch in name)
        {
            if (sb.Length >= 40) break;
            sb.Append(char.IsControl(ch) || bad.Contains(ch) ? '_' : ch);
        }
        return sb.Length == 0 ? "会话" : sb.ToString();
    }

    /// <summary>复制一条消息的原文（右键菜单用）。</summary>
    [RelayCommand]
    private void CopyMessage(ChatMessageVm? item)
    {
        var text = item?.Content;
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            // 剪贴板可能被其他进程锁住；说一声就行，不能把整个聊天页带倒
            App.LogError("复制到剪贴板", ex);
            ThemedDialog.Warn("复制失败：剪贴板正被别的程序占用，稍后再试。", "复制");
        }
    }

    // ==================== 上下文账本（③） ====================

    /// <summary>
    /// 进度条按「引擎实际在跑的长度」算。没在跑时用设置里的值，并在 tooltip 里说明这只是预设值。
    /// </summary>
    private int EffectiveContextSize
    {
        get
        {
            var applied = App.Engine.LastContextArg;
            return applied > 0 ? applied : Math.Max(256, App.SettingsService.Current.ContextSize);
        }
    }

    private static ContextStrategy Strategy => ContextBudget.ParseStrategy(App.SettingsService.Current.ContextStrategy);

    /// <summary>真正会用的系统提示词（输入框留空时用内置那句）。</summary>
    private string EffectiveSystemPrompt
    {
        get
        {
            var p = App.SettingsService.Current.Generation.SystemPrompt;
            return string.IsNullOrWhiteSpace(p) ? AppInfo.DefaultSystemPrompt : p;
        }
    }

    /// <summary>给回复预留多少：设了「回复预留」用它，没设就拿「最大长度」当依据。</summary>
    private int ReserveFor(int ctx)
    {
        var s = App.SettingsService.Current;
        var want = s.ContextReserveTokens > 0 ? s.ContextReserveTokens : s.Generation.MaxTokens;
        return ContextBudget.ReserveFor(ctx, want);
    }

    /// <summary>可发送的历史：不含 system、也不含空白条（上次崩在半路可能留下一条空回复）。</summary>
    private static List<ChatMessage> HistoryOf(Conversation conv) =>
        conv.Messages.Where(m => m.Role != ChatRole.System && !string.IsNullOrWhiteSpace(m.Content)).ToList();

    /// <summary>刷新底部那根进度条（切会话、切页、每轮结束后都要重算）。</summary>
    private void UpdateContextPreview()
    {
        var conv = SelectedConversation;
        var ctx = EffectiveContextSize;
        var history = conv == null ? new List<ChatMessage>() : HistoryOf(conv);

        // 预览一律按「全发」算：要的是这个会话到底多重，而不是被裁剩多少
        var plan = ContextBudget.Preview(EffectiveSystemPrompt, history, ctx, ReserveFor(ctx),
            ContextStrategy.AskMe, _tokenFactor);

        ContextOver = plan.OverLimit;
        ContextWarn = plan.Percent >= 80 || plan.OverLimit;
        ContextPercent = plan.Percent;
        ContextShort = ContextBudget.K(plan.EstTokens) + " / " + ContextBudget.K(ctx) +
                       (plan.OverLimit ? "（已超出）" : $"（{plan.Percent:0}%）");

        var sb = new StringBuilder();
        var applied = App.Engine.LastContextArg;
        if (applied > 0 && applied != App.SettingsService.Current.ContextSize)
            sb.Append($"注意：引擎现在跑的是 {ContextBudget.K(applied)}，设置里已经改成 " +
                      $"{ContextBudget.K(App.SettingsService.Current.ContextSize)} 了，要重新加载模型才生效。\n");
        else if (applied <= 0)
            sb.Append("引擎还没起，下面按设置里的上下文长度估算。\n");
        sb.Append(plan.Detail);
        if (conv is { LastPromptTokens: > 0 })
            sb.Append($"\n最近一轮引擎实际用了 {ContextBudget.K(conv.LastPromptTokens)} token（这是真实值，比估算准）。");
        ContextDetail = sb.ToString();
    }

    /// <summary>「上下文不够用」的把关。返回 false = 这一轮不发。</summary>
    private static bool ShouldProceed(ContextPlan plan, ContextStrategy strategy)
    {
        // 连系统提示词和这一句都塞不下：发出去也一定被引擎拒，直接告诉用户怎么办，不做无意义的请求
        if (plan.OverLimit)
        {
            ThemedDialog.Error(
                $"上下文窗口只有 {ContextBudget.K(plan.ContextSize)} token，而系统提示词 + 你这句话就要 " +
                $"{plan.SystemTokens + plan.InputTokens} token，装不下。\n\n" +
                "可以：\n① 新建一个会话（推荐）；\n② 去「设置」把上下文长度调大（会多占内存，超过模型本身的训练上限会报错）；\n③ 把这句话缩短一点。",
                "上下文不够用");
            return false;
        }

        // 「先问我」策略：内容原样发，但超出额度得让用户知情（有人宁可换会话，也不要模型漏看上文）
        if (strategy == ContextStrategy.AskMe && plan.EstTokens > plan.BudgetTokens)
            return ThemedDialog.Confirm(
                $"这段会话预计要 {plan.EstTokens} token，已经超过了窗口能给的 {plan.BudgetTokens}" +
                $"（{ContextBudget.K(plan.ContextSize)} 减掉给回复预留的 {plan.ReserveTokens}）。\n\n" +
                "点「仍然发送」会把全部内容发给引擎，可能被它拒绝并报 context length exceeded。\n" +
                "想让它自动丢掉最早的消息：去「设置 → 上下文不够时」选「自动省略最旧消息」。",
                "上下文快满了", "仍然发送", "先不发送");

        return true;
    }

    /// <summary>
    /// 气泡要不要按 Markdown 排版（设置里的「渲染 Markdown 与代码高亮」）。关掉就是纯文本：
    /// 有人调提示词就是要看原始星号。流式期间控件自己降频重排，不会每个 token 抖一次。
    /// </summary>
    public bool RenderMarkdown => App.SettingsService.Current.RenderMarkdown;

    /// <summary>提示条上「去设置」：跳到设置页。导航没挂上时什么都不做，不能因为找不到按钮把提示弄挂。</summary>
    [RelayCommand]
    private static void GoSettings() => App.NavigateRequested?.Invoke("settings");

    /// <summary>把「现在在哪个会话、用的哪个模型」记到 run-state.json（被杀掉时靠它下次回到这里）。</summary>
    private void RememberRunState()
    {
        var conv = SelectedConversation;
        App.RunState.UpdateProgress(App.Engine.State.ModelPath ?? App.SettingsService.Current.LastModelPath,
            conv?.Id, conv?.Title, Conversations.Count);
    }

    private void ReloadMessages()
    {
        Messages.Clear();
        var conv = SelectedConversation;
        if (conv == null)
        {
            MessageCountText = "";
            UpdateContextPreview();
            return;
        }

        foreach (var msg in conv.Messages)
        {
            Messages.Add(new ChatMessageVm
            {
                Role = msg.Role,
                Content = msg.Content,
                Model = msg,
                Interrupted = msg.Interrupted,
                Meta = MetaOf(msg),
            });
        }
        MessageCountText = conv.Messages.Count == 0 ? "" : $"{conv.Messages.Count} 条消息";
        UpdateContextPreview();
        ScrollRequested?.Invoke();
    }

    /// <summary>气泡底部那行小字：时间 · 耗时 · token · 速度。没数据的项直接不写，不摆一排 0。</summary>
    private static string MetaOf(ChatMessage m)
    {
        var parts = new List<string> { m.Timestamp.ToString("MM-dd HH:mm") };
        if (m.ElapsedMs > 0) parts.Add($"{m.ElapsedMs / 1000.0:0.#} 秒");
        if (m.CompletionTokens > 0)
        {
            parts.Add($"{m.CompletionTokens} tok");
            if (m.ElapsedMs > 0) parts.Add($"{m.CompletionTokens * 1000.0 / m.ElapsedMs:0.#} tok/s");
        }
        if (m.PromptTokens > 0) parts.Add($"上文 {ContextBudget.K(m.PromptTokens)}");
        return string.Join(" · ", parts);
    }

    [RelayCommand]
    private void StopGeneration() => _cts?.Cancel();

    /// <summary>
    /// 重新生成最后一条回复：把最后一条用户消息之后的内容丢掉重发。
    /// 「上次没写完」和「这句答得不好」都靠它，不用把问题再抄一遍。
    /// </summary>
    [RelayCommand]
    private async Task RegenerateAsync()
    {
        if (IsBusy) return;
        var conv = SelectedConversation;
        if (conv == null) return;

        var lastUser = -1;
        for (var i = conv.Messages.Count - 1; i >= 0; i--)
            if (conv.Messages[i].Role == ChatRole.User) { lastUser = i; break; }
        if (lastUser < 0)
        {
            ThemedDialog.Info("这个会话里还没有你的提问。", "重新生成");
            return;
        }

        var text = conv.Messages[lastUser].Content;
        while (conv.Messages.Count - 1 > lastUser)
        {
            conv.Messages.RemoveAt(conv.Messages.Count - 1);
            if (Messages.Count > 0) Messages.RemoveAt(Messages.Count - 1);
        }
        App.Store.Save(conv);

        await SendCoreAsync(text, addNewUserMessage: false);
    }

    [RelayCommand]
    private Task SendAsync() => SendCoreAsync(InputText.Trim(), addNewUserMessage: true);

    /// <summary>
    /// 发一轮。两条路进来：正常发送（先把用户那句话加进会话）与重新生成（那句话已经在会话里了）。
    /// </summary>
    private async Task SendCoreAsync(string text, bool addNewUserMessage)
    {
        if (IsBusy) return;
        if (text.Length == 0) return;

        if (!EngineReady)
        {
            EngineStatusLine = "请先在上方或「模型库」选择一个模型并等待加载完成";
            return;
        }

        if (SelectedConversation == null) AddConversation();
        var conv = SelectedConversation!;

        // 每次发送现取一份参数（不是进页面时缓存）：在参数面板上改完，下一条消息就生效
        var gen = App.SettingsService.Current.Generation;
        gen.Clamp();

        var sys = EffectiveSystemPrompt;
        var ctx = EffectiveContextSize;
        var reserve = ReserveFor(ctx);
        var strategy = Strategy;

        var history = HistoryOf(conv);
        var (kept, plan) = ContextBudget.Apply(sys, history, text, ctx, reserve, strategy, _tokenFactor);
        if (!ShouldProceed(plan, strategy)) return;

        if (addNewUserMessage)
        {
            var userMsg = new ChatMessage { Role = ChatRole.User, Content = text };
            conv.Messages.Add(userMsg);
            Messages.Add(new ChatMessageVm { Role = ChatRole.User, Content = text, Model = userMsg });
            InputText = "";
        }

        // InProgress = true 是崩溃恢复的记号：ChatStore 下次加载时看到它就把这条转成「没写完」
        var assistantMsg = new ChatMessage { Role = ChatRole.Assistant, Content = "", InProgress = true };
        conv.Messages.Add(assistantMsg);
        var bubble = new ChatMessageVm
        {
            Role = ChatRole.Assistant,
            Content = "",
            Model = assistantMsg,
            Busy = true,
            LiveText = "生成中…",
        };
        Messages.Add(bubble);

        // 标题：用户没自己起过名字时拿第一句话当标题（起了名字就不再覆盖，见 ApplyTitle）
        if (!conv.TitleLocked && conv.Title == "新对话")
            conv.Title = text.Length > 18 ? text[..18] + "…" : text;
        conv.ModelName ??= CurrentModelName();
        conv.UpdatedAt = DateTime.Now;
        conv.TrimmedOnce |= plan.Trimmed;

        TrimNotice = plan.Trimmed
            ? $"这一轮没把最早的 {plan.DroppedCount} 条消息发给模型（{plan.ShortLabel}）—— 它看不到那部分内容了。" +
              "重要信息请重新说一遍，或者新建一个会话。想改成每次先问你再发：「设置 → 上下文不够时」。"
            : "";
        ContextPercent = plan.Percent;
        ContextOver = plan.OverLimit;
        ContextWarn = plan.Percent >= 80 || plan.OverLimit;
        ContextShort = ContextBudget.K(plan.SentTokens) + " / " + ContextBudget.K(ctx) +
                       $"（{plan.Percent:0}%）" + (plan.Trimmed ? $" · 已省略 {plan.DroppedCount} 条" : "");
        ContextDetail = plan.Detail;

        // 先落盘再发请求：这一轮崩了也留得下「你问了什么」，重启后能看到断在哪一步
        App.Store.Save(conv);
        RememberRunState();
        ScrollRequested?.Invoke();

        IsBusy = true;
        _cts = new CancellationTokenSource();
        _engineDied = null;

        // 只累积模型真正的输出；错误提示、「已停止」标记留在界面上，不写进对话记录，
        // 否则下一轮会把「⚠️ 请求失败…」当成 assistant 的历史内容发给模型
        var generated = new StringBuilder();
        var sw = Stopwatch.StartNew();
        var lastFlush = TimeSpan.Zero;
        var usage = new OpenAiClient.TokenUsage();

        try
        {
            var client = new OpenAiClient(App.Engine.BaseUrl);

            // model 字段拿当前加载的文件名兜底；接外部 OpenAI 兼容服务时自己在「模型标识」里填
            var modelFallback = Path.GetFileNameWithoutExtension(App.Engine.State.ModelPath ?? "");

            var request = new List<ChatMessage>(kept.Count + 1)
            {
                new ChatMessage { Role = ChatRole.System, Content = sys }
            };
            request.AddRange(kept);

            await foreach (var delta in client.StreamChatAsync(request, gen, modelFallback, usage, _cts.Token))
            {
                generated.Append(delta);
                bubble.Content += delta;

                // 每 2 秒落一次盘：被 OOM 干掉时最多丢两秒内容，而且下次启动能看出这条是断的，
                // 而不是「模型本来就只说了这么点」
                var el = sw.Elapsed;
                if (el - lastFlush >= TimeSpan.FromSeconds(2))
                {
                    lastFlush = el;
                    assistantMsg.Content = generated.ToString();
                    assistantMsg.InProgress = true;
                    App.Store.Save(conv);
                }

                var secs = Math.Max(0.5, el.TotalSeconds);
                bubble.LiveText = $"{generated.Length} 字 · {generated.Length / secs:0} 字/秒";
                ScrollRequested?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            if (generated.Length > 0) bubble.Content += "…（已停止）";
        }
        catch (Exception ex)
        {
            // 引擎中途死了的时候 socket 只会报「连接意外关闭」，真实原因（OOM / KV 申请不到）在引擎那边
            var died = _engineDied;
            if (died != null)
            {
                var advice = CrashReporter.ClassifyEngineExit(died.ExitCode, died.LogTail, died.AvailableMb);
                bubble.Content += $"\n⚠️ {advice.Headline}\n{advice.Detail}";
            }
            else bubble.Content += $"\n⚠️ 请求失败：{ex.Message}";
        }
        finally
        {
            sw.Stop();

            assistantMsg.Content = generated.ToString();
            assistantMsg.InProgress = false;
            assistantMsg.Interrupted = false;
            assistantMsg.ElapsedMs = (int)sw.ElapsedMilliseconds;
            if (!usage.IsEmpty)
            {
                assistantMsg.PromptTokens = usage.PromptTokens;
                assistantMsg.CompletionTokens = usage.CompletionTokens;

                // 用引擎回报的真实值校准估算系数：下一轮的进度条和「该丢哪几条」都更准
                _tokenFactor = TokenEstimator.Calibrate(_tokenFactor, plan.SentTokens, usage.PromptTokens);
            }
            conv.LastPromptTokens = usage.IsEmpty ? plan.SentTokens : usage.PromptTokens;

            bubble.Busy = false;
            bubble.LiveText = "";
            bubble.Meta = MetaOf(assistantMsg);

            if (assistantMsg.Content.Length == 0)
            {
                // 失败或一上来就点停止：不留空回复（视图里那条也一起撤，免得留个空气泡）
                conv.Messages.Remove(assistantMsg);
                Messages.Remove(bubble);
            }

            conv.UpdatedAt = DateTime.Now;
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
            App.Store.Save(conv);
            UpdateContextPreview();
            RefreshHints();
            RememberRunState();
        }
    }
}
