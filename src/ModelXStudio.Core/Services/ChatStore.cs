// =====================================================================
// 作者：zhenhua
// 功能：对话记录持久化：一条对话一个 JSON 文件，原子写入（先 .tmp 再改名），
//       读取时跳过损坏文件；写盘失败只记调试日志，不掩盖界面上的真实错误。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

using System.Text.Json;
using ModelXStudio.Core.Models;

namespace ModelXStudio.Core.Services;

/// <summary>对话记录持久化：每条对话一个 JSON 文件。</summary>
public sealed class ChatStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public List<Conversation> LoadAll()
    {
        var result = new List<Conversation>();
        try
        {
            Directory.CreateDirectory(AppSettings.ChatsDirectory);
            foreach (var file in Directory.EnumerateFiles(AppSettings.ChatsDirectory, "*.json"))
            {
                try
                {
                    var convo = JsonSerializer.Deserialize<Conversation>(File.ReadAllText(file), JsonOpts);
                    if (convo != null) result.Add(convo);
                }
                catch
                {
                    // 损坏的单条对话跳过
                }
            }
        }
        catch
        {
            // 无法读取对话目录时返回空
        }

        return result.OrderByDescending(c => c.UpdatedAt).ToList();
    }

    /// <summary>
    /// 保存对话：先写 .tmp 再改名（原子替换），避免写一半时崩溃把原有记录变成半个 JSON。
    /// 磁盘/权限异常只记调试日志不外抛：调用方常在 finally 里保存，抛出会掩盖真正的发送错误。
    /// </summary>
    public void Save(Conversation conversation)
    {
        try
        {
            conversation.UpdatedAt = DateTime.Now;
            Directory.CreateDirectory(AppSettings.ChatsDirectory);
            var path = Path.Combine(AppSettings.ChatsDirectory, conversation.Id + ".json");
            var tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, JsonSerializer.Serialize(conversation, JsonOpts));
            File.Move(tmpPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatStore] 对话保存失败：{ex.Message}");
        }
    }

    /// <summary>删除对话文件；文件不存在或被占用时静默返回。</summary>
    public void Delete(string conversationId)
    {
        try
        {
            var path = Path.Combine(AppSettings.ChatsDirectory, conversationId + ".json");
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatStore] 对话删除失败：{ex.Message}");
        }
    }
}
