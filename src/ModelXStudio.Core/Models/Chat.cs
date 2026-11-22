// =====================================================================
// 作者：zhenhua
// 功能：对话数据模型：消息角色、单条消息、一次会话（标题 / 置顶 / 消息列表 / 创建更新时间 / 本轮 token 账），
//       由 ChatStore 以 JSON 持久化到本地。字段都带「为什么存」的注释，因为它们决定了重启后能恢复出什么。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

namespace ModelXStudio.Core.Models;

public enum ChatRole { System, User, Assistant }

public sealed class ChatMessage
{
    public ChatRole Role { get; set; }
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>
    /// 这条回复还没写完（流式输出过程中落盘的标记）。
    /// 正常结束或用户点「停止」都会被清掉；重启后还看到 true，就说明上次是崩溃/被杀，
    /// ChatStore 加载时会把它转成 <see cref="Interrupted"/> 并清除本标记，避免下次接着往半截话后面写。
    /// </summary>
    public bool InProgress { get; set; }

    /// <summary>上次程序没写完这条回复（崩溃、被系统杀掉、引擎中途退出）。界面据此显示「回复被中断」。</summary>
    public bool Interrupted { get; set; }

    /// <summary>本条回复的生成耗时（毫秒）。0 = 未知（老数据或非流式）。用来显示 tok/s，让人判断是不是卡住了。</summary>
    public int ElapsedMs { get; set; }

    /// <summary>引擎回报的真实输入 token 数（含系统提示词与本轮历史）。0 = 该引擎/端点不回报。</summary>
    public int PromptTokens { get; set; }

    /// <summary>引擎回报的真实输出 token 数。0 = 同上。</summary>
    public int CompletionTokens { get; set; }
}

public sealed class Conversation : System.ComponentModel.INotifyPropertyChanged
{
    private string _title = "新对话";
    private DateTime _updatedAt = DateTime.Now;
    private bool _pinned;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; OnPropertyChanged(nameof(Title)); } }
    }

    /// <summary>标题是用户手工改的吗。true 之后自动取标题不再覆盖它。</summary>
    public bool TitleLocked { get; set; }

    /// <summary>置顶：会话列表里排在最前，且删除时要额外确认一次。</summary>
    public bool Pinned
    {
        get => _pinned;
        set { if (_pinned != value) { _pinned = value; OnPropertyChanged(nameof(Pinned)); } }
    }

    public string? ModelPath { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt
    {
        get => _updatedAt;
        set { if (_updatedAt != value) { _updatedAt = value; OnPropertyChanged(nameof(UpdatedAt)); } }
    }

    public List<ChatMessage> Messages { get; set; } = new();

    /// <summary>用的哪个模型 id/文件名，只为在会话列表上标注（ModelPath 才是真路径）。</summary>
    public string? ModelName { get; set; }

    /// <summary>
    /// 最近一次请求的输入 token（引擎回报的真实值优先，没有就存估算值）。
    /// 存下来是为了「打开一个老会话就能立刻知道它有多重」，不用先跑一轮才知道快满了。
    /// </summary>
    public int LastPromptTokens { get; set; }

    /// <summary>会话被截断过（丢过历史）。列表上给个小标记，免得用户以为模型「失忆」是 bug。</summary>
    public bool TrimmedOnce { get; set; }

    private string _listHint = "";

    /// <summary>
    /// 会话列表第二行的小字（「12 条 · 上文 6.2k · 有没写完的」）。由 ChatViewModel 现算现写，
    /// 不落盘：这些数从 Messages / LastPromptTokens 都能算出来，存两份迟早对不上。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string ListHint
    {
        get => _listHint;
        set { if (_listHint != value) { _listHint = value; OnPropertyChanged(nameof(ListHint)); } }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}
