// =====================================================================
// 作者：zhenhua
// 功能：聊天页逻辑：会话新建/删除、消息气泡视图模型、把历史（截断到最近若干条）+ system 提示词
//       发给本地引擎并流式追加回复、停止生成、模型与「快速/均衡/高质量」预设的联动。
// =====================================================================

using System.IO;
using System.Text;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModelXStudio.Core.Models;
using ModelXStudio.Core.Services;

namespace ModelXStudio.App.ViewModels;

public partial class ChatMessageVm : ObservableObject
{
    public ChatRole Role { get; init; }
    public bool IsUser => Role == ChatRole.User;
    public string RoleLabel => IsUser ? "你" : "ModelX";
    [ObservableProperty]
    private string _content = "";
}

public partial class ChatViewModel : ObservableObject
{
    public ObservableCollection<Conversation> Conversations { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasConversation))]
    private Conversation? _selectedConversation;

    public bool HasConversation => SelectedConversation != null;

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

    public List<string> PresetOptions { get; } = new() { "快速", "均衡", "高质量" };

    [ObservableProperty]
    private string _selectedPreset = "均衡";

    /// <summary>发给模型的历史消息条数上限（不含 system 与本轮输入）。
    /// 引擎以 -c 指定的上下文长度运行，全量历史迟早超限，届时服务直接报错，
    /// 用户只看到「聊着聊着就不能用了」，所以这里主动截断。</summary>
    private const int MaxHistoryMessages = 20;

    private CancellationTokenSource? _cts;
    private bool _syncing;

    /// <summary>视图订阅：新内容需要滚动到底部。</summary>
    public event Action? ScrollRequested;

    public ChatViewModel()
    {
        foreach (var c in App.Store.LoadAll()) Conversations.Add(c);
        SelectedConversation = Conversations.FirstOrDefault();
        ReloadMessages();
        RefreshModelOptions();

        SelectedPreset = KeyToLabel(App.SettingsService.Current.GenerationPreset);

        App.Engine.StateChanged += state =>
            Application.Current?.Dispatcher.Invoke(() => OnEngineState(state));
    }

    partial void OnSelectedConversationChanged(Conversation? value) => ReloadMessages();

    partial void OnSelectedPresetChanged(string value)
    {
        App.SettingsService.Current.GenerationPreset = LabelToKey(value);
        App.SettingsService.Save();
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
        _ => "Balanced"
    };

    private static string KeyToLabel(string key) => key switch
    {
        "Fast" => "快速",
        "Quality" => "高质量",
        _ => "均衡"
    };

    private void OnEngineState(EngineState state)
    {
        EngineReady = state.Status == EngineStatus.Running;
        EngineStatusLine = state.Status switch
        {
            EngineStatus.Running => "● " + Path.GetFileName(state.ModelPath ?? "") + " 已就绪",
            EngineStatus.Loading => "● 模型加载中…",
            EngineStatus.DownloadingEngine => "● " + state.Message,
            _ => state.Message
        };

        var runningPath = state.ModelPath;
        _syncing = true;
        SelectedModel = ModelOptions.FirstOrDefault(m => m.FilePath == runningPath);
        _syncing = false;
    }

    public void RefreshModelOptions()
    {
        var models = App.Scanner.Scan(App.SettingsService.Current.ModelsDirectory);
        ModelOptions.Clear();
        foreach (var m in models) ModelOptions.Add(m);

        _syncing = true;
        SelectedModel = ModelOptions.FirstOrDefault(m => m.FilePath == App.Engine.State.ModelPath);
        _syncing = false;
    }

    private void ReloadMessages()
    {
        Messages.Clear();
        if (SelectedConversation == null) return;
        foreach (var msg in SelectedConversation.Messages)
        {
            Messages.Add(new ChatMessageVm { Role = msg.Role, Content = msg.Content });
        }
        ScrollRequested?.Invoke();
    }

    [RelayCommand]
    private void NewChat()
    {
        var conv = new Conversation();
        App.Store.Save(conv);
        Conversations.Insert(0, conv);
        SelectedConversation = conv;
    }

    [RelayCommand]
    private void DeleteChat()
    {
        var conv = SelectedConversation;
        if (conv == null) return;

        var result = MessageBox.Show($"删除对话「{conv.Title}」？此操作不可恢复。",
            "删除对话", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (result != MessageBoxResult.OK) return;

        App.Store.Delete(conv.Id);
        Conversations.Remove(conv);
        SelectedConversation = Conversations.FirstOrDefault();
    }

    [RelayCommand]
    private void StopGeneration() => _cts?.Cancel();

    [RelayCommand]
    private async Task SendAsync()
    {
        if (IsBusy) return;

        var text = InputText.Trim();
        if (text.Length == 0) return;

        if (!EngineReady)
        {
            EngineStatusLine = "请先在上方或「模型库」选择一个模型并等待加载完成";
            return;
        }

        if (SelectedConversation == null) NewChat();
        var conv = SelectedConversation!;

        if (conv.Messages.Count == 0)
            conv.Title = text.Length > 18 ? text[..18] + "…" : text;

        var userMsg = new ChatMessage { Role = ChatRole.User, Content = text };
        conv.Messages.Add(userMsg);
        Messages.Add(new ChatMessageVm { Role = ChatRole.User, Content = text });
        InputText = "";

        var assistantMsg = new ChatMessage { Role = ChatRole.Assistant, Content = "" };
        conv.Messages.Add(assistantMsg);
        var assistantVm = new ChatMessageVm { Role = ChatRole.Assistant, Content = "" };
        Messages.Add(assistantVm);
        ScrollRequested?.Invoke();

        IsBusy = true;
        _cts = new CancellationTokenSource();

        // 去掉末尾正在生成的空 assistant 占位，再只保留最近 MaxHistoryMessages 条
        var history = conv.Messages.Take(conv.Messages.Count - 1).ToList();
        if (history.Count > MaxHistoryMessages)
            history = history.Skip(history.Count - MaxHistoryMessages).ToList();
        history.Insert(0, new ChatMessage
        {
            Role = ChatRole.System,
            Content = AppInfo.DefaultSystemPrompt
        });

        // 只累积模型真正的输出；错误提示、「已停止」标记留在界面上，不写进对话记录，
        // 否则下一轮会把「⚠️ 请求失败…」当成 assistant 的历史内容发给模型
        var generated = new StringBuilder();

        try
        {
            var client = new OpenAiClient(App.Engine.BaseUrl);
            var sampling = EngineManager.PresetFor(App.SettingsService.Current.GenerationPreset);

            await foreach (var delta in client.StreamChatAsync(history, sampling, _cts.Token))
            {
                generated.Append(delta);
                assistantVm.Content += delta;
                ScrollRequested?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            if (generated.Length > 0) assistantVm.Content += "…（已停止）";
        }
        catch (Exception ex)
        {
            assistantVm.Content += $"\n⚠️ 请求失败：{ex.Message}";
        }
        finally
        {
            assistantMsg.Content = generated.ToString();
            if (assistantMsg.Content.Length == 0)
                conv.Messages.Remove(assistantMsg);     // 失败或立刻停止：不留空回复

            IsBusy = false;
            _cts.Dispose();
            _cts = null;
            App.Store.Save(conv);
        }
    }
}
