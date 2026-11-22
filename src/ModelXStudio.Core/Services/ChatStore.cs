// =====================================================================
// 作者：zhenhua
// 功能：对话记录持久化：一条对话一个 JSON 文件，原子写入（先 .tmp 再改名），
//       读取时跳过损坏文件；写盘失败只记调试日志，不掩盖界面上的真实错误。
//       加载时顺便做崩溃修复：把上次没写完的回复（InProgress）标成 Interrupted，见 Normalize。
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
        // 读的时候不区分字段大小写：本程序写的是 PascalCase，但用户会手改、别的工具会写 camelCase。
        // 大小写对不上时 System.Text.Json 默认能「读通」但每个字段都是空值 —— 见 LoadAll 里那个
        // 空壳判定，两者配合才能做到「读不出就跳过」而不是「读出一个空会话然盖回去」。
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>加载全部对话。被中断的回复条数累加到 <paramref name="interruptedMessages"/>，界面用它告诉用户「上次哪条没说完」。</summary>
    public List<Conversation> LoadAll(out int interruptedMessages)
    {
        var result = new List<Conversation>();
        interruptedMessages = 0;
        try
        {
            Directory.CreateDirectory(AppSettings.ChatsDirectory);
            CleanTempFiles();
            foreach (var file in Directory.EnumerateFiles(AppSettings.ChatsDirectory, "*.json"))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var convo = JsonSerializer.Deserialize<Conversation>(text, JsonOpts);
                    if (convo == null) continue;
                    // 「文件里根本没有 id 字段、又一条消息都没有」= 字段名一个也没对上（文件被手改坏 /
                    // 不是本程序写的 / 以后改了命名）。这种必须当损坏跳过：让它进场就会顶替原文件
                    // 显示成空对话，用户随手一保存原内容就没了。
                    // 注意别看 convo.Id —— 它在声明处就有 Guid 初始值，永远不会是空，只能看原文。
                    if (convo.Messages.Count == 0 && !text.Contains("\"id\"", StringComparison.OrdinalIgnoreCase))
                    {
                        System.Diagnostics.Debug.WriteLine($"[ChatStore] 跳过（无 id 字段且无消息）：{file}");
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(convo.Id)) convo.Id = Guid.NewGuid().ToString("N");
                    // 中断修复（InProgress → Interrupted）当场写回：只在内存里改的话，下次启动又是
                    // InProgress，「上次有 N 条没写完」这句话会无限重复提示。
                    var fixedUp = Normalize(convo);   // 返回转了几条，别当成 bool 用
                    if (fixedUp > 0)
                    {
                        interruptedMessages += fixedUp;
                        try { Save(convo); } catch { /* 写不回去不影响本次读取，下次启动再补 */ }
                    }
                    result.Add(convo);
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

    /// <summary>不要中断信息的重载。</summary>
    public List<Conversation> LoadAll() => LoadAll(out _);

    /// <summary>
    /// 崩溃修复：把上次落盘时还带 InProgress 的回复转成 Interrupted。
    /// 不这么做的话，重启后那条半截回复会被当成「已完成的回复」继续往后接（重生成时会把半句话当原文），
    /// 而且用户看不出这段是断的。返回转换了几条。
    /// </summary>
    public static int Normalize(Conversation conversation)
    {
        var n = 0;
        foreach (var m in conversation.Messages)
        {
            if (!m.InProgress) continue;
            m.InProgress = false;
            m.Interrupted = true;
            n++;
        }
        return n;
    }

    /// <summary>
    /// 清掉上次崩在写入途中留下的 .tmp（改名之前就被杀了）。留着没人读，只是垃圾。
    /// 只删 1 小时以前的：并发改同一个对话时不该把别人正在写的临时文件抢了。
    /// </summary>
    private static void CleanTempFiles()
    {
        try
        {
            foreach (var tmp in Directory.EnumerateFiles(AppSettings.ChatsDirectory, "*.json.tmp"))
            {
                try
                {
                    if (DateTime.Now - File.GetLastWriteTime(tmp) > TimeSpan.FromHours(1)) File.Delete(tmp);
                }
                catch { /* 正被人占用就下次再说 */ }
            }
        }
        catch { /* 目录取不到就算了，不影响加载 */ }
    }

    /// <summary>
    /// 保存对话：先写 .tmp 再改名（原子替换），避免写一半时崩溃把原有记录变成半个 JSON。
    /// 磁盘/权限异常只记调试日志不外抛：调用方常在 finally 里保存，抛出会掩盖真正的发送错误。
    /// </summary>
    public void Save(Conversation conversation)
    {
        try
        {
            // Id 会被直接拼成文件名：手写/改坏的 json 里放个 "..\\x" 就能写到目录外面去，这里拦住
            if (string.IsNullOrWhiteSpace(conversation.Id) ||
                conversation.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                conversation.Id = Guid.NewGuid().ToString("N");
            }

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
            if (string.IsNullOrWhiteSpace(conversationId)) return;
            var path = Path.Combine(AppSettings.ChatsDirectory, conversationId + ".json");
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ChatStore] 对话删除失败：{ex.Message}");
        }
    }
}
