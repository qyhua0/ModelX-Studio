// =====================================================================
// 作者：zhenhua
// 功能：对话数据模型：消息角色、单条消息、一次会话（标题 + 消息列表 + 创建/更新时间），
//       由 ChatStore 以 JSON 持久化到本地。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

namespace ModelXStudio.Core.Models;

public enum ChatRole { System, User, Assistant }

public sealed class ChatMessage
{
    public ChatRole Role { get; set; }
    public string Content { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public sealed class Conversation : System.ComponentModel.INotifyPropertyChanged
{
    private string _title = "新对话";
    private DateTime _updatedAt = DateTime.Now;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; OnPropertyChanged(nameof(Title)); } }
    }

    public string? ModelPath { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt
    {
        get => _updatedAt;
        set { if (_updatedAt != value) { _updatedAt = value; OnPropertyChanged(nameof(UpdatedAt)); } }
    }

    public List<ChatMessage> Messages { get; set; } = new();

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}
