// =====================================================================
// 作者：zhenhua
// 功能：崩溃恢复的两块底座。① RunStateService：在 %LOCALAPPDATA%\ModelXStudio\run-state.json 里记「本次运行
//         开始/是否正常退出/加载了哪个模型/在哪个会话」，启动时读到没退干净就说明上次崩了或被系统杀掉；
//       ② CrashReporter：追加式人类可读的 crash-history.log（引擎退出码、可用内存、日志尾部），
//         外加对引擎退出原因的归类（内存不足 / 上下文超了 / 模型文件坏了 / 不明），给出可操作的下一步。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

using System.Text;
using System.Text.Json;
using ModelXStudio.Core.Models;

namespace ModelXStudio.Core.Services;

/// <summary>一次运行的状态快照（写盘用，也是下次启动判断「上次是否退干净」的依据）。</summary>
public sealed class RunState
{
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? FinishedAt { get; set; }

    /// <summary>只有走 OnExit 正常收尾才会置 true。读到 false = 上次是崩溃/断电/被任务管理器杀掉。</summary>
    public bool CleanExit { get; set; }

    public string? ModelPath { get; set; }
    public string? ConversationId { get; set; }
    public string? ConversationTitle { get; set; }
    public int SessionCount { get; set; }

    /// <summary>正常退出时补充的说明（例如「用户手动关闭」），异常退出用不上它 —— 那种情况看 crash-history.log。</summary>
    public string? Note { get; set; }

    /// <summary>上次没退干净。</summary>
    public bool WasAbnormal => FinishedAt == null || !CleanExit;

    /// <summary>上次运行了多久（异常时说明「撑了多久」，便于对上是哪一步）。</summary>
    public string DurationText
    {
        get
        {
            var end = FinishedAt ?? DateTime.Now;
            var d = end - StartedAt;
            if (d.TotalSeconds < 90) return $"{Math.Max(1, d.Seconds)} 秒";
            if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} 分钟";
            return $"{(int)d.TotalHours} 小时 {d.Minutes} 分";
        }
    }
}

/// <summary>run-state.json 的读写。所有方法都吞掉 IO 异常：状态文件坏了不能影响程序启动。</summary>
public sealed class RunStateService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>
    /// 程序启动时调用：先读上次状态（没退干净就返回它给界面弹提示），然后写一条「本次运行中」的记录。
    /// 必须在写新记录之前读 —— 写下去就把上次的痕迹覆盖了。
    /// </summary>
    public RunState? BeginRun(string? modelPath, int sessionCount)
    {
        var previous = Read();
        Write(new RunState
        {
            StartedAt = DateTime.Now,
            CleanExit = false,
            ModelPath = modelPath,
            SessionCount = sessionCount,
        });
        return previous is { WasAbnormal: true } ? previous : null;
    }

    /// <summary>
    /// 运行中刷新「现在在哪个会话、加载了哪个模型」。被系统杀掉时靠它决定下次回到哪儿，
    /// 所以调用方要节流（跟着自动保存的节奏走，别每个 token 写一次盘）。
    /// </summary>
    public void UpdateProgress(string? modelPath, string? conversationId, string? conversationTitle, int sessionCount)
    {
        var state = Read() ?? new RunState();
        state.ModelPath = modelPath ?? state.ModelPath;
        state.ConversationId = conversationId ?? state.ConversationId;
        state.ConversationTitle = conversationTitle ?? state.ConversationTitle;
        state.SessionCount = sessionCount;
        state.CleanExit = false;
        state.FinishedAt = null;
        Write(state);
    }

    /// <summary>正常退出（OnExit）时盖章。下次启动读到 CleanExit=true 就不再打扰用户。</summary>
    public void EndRun(string? note)
    {
        var state = Read() ?? new RunState();
        state.FinishedAt = DateTime.Now;
        state.CleanExit = true;
        state.Note = note;
        Write(state);
    }

    public RunState? Read()
    {
        try
        {
            var path = AppSettings.RunStateFilePath;
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<RunState>(File.ReadAllText(path), JsonOpts);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RunState] 读取失败：{ex.Message}");
            return null;
        }
    }

    private static void Write(RunState state)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.AppDataDirectory);
            var path = AppSettings.RunStateFilePath;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOpts));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RunState] 写入失败：{ex.Message}");
        }
    }
}

/// <summary>引擎退出的原因归类结果。</summary>
public sealed record EngineExitAdvice(bool LikelyOutOfMemory, string Headline, string Detail)
{
    /// <summary>要不要在提示里带上「换小一点的模型」这类按钮。</summary>
    public bool SuggestSmallerModel => LikelyOutOfMemory;
}

/// <summary>
/// 崩溃/意外退出的记录与归类。写成普通文本而不是 JSON：出问题时用户常被引导「把日志发我」，
/// 一个能直接看的 txt 比让人打开 JSON 友好得多。
/// </summary>
public static class CrashReporter
{
    private const long MaxLogBytes = 256 * 1024;

    /// <summary>追加一行（带时间戳）。超过 256KB 就只保留后半部分。</summary>
    public static void Append(string line)
    {
        try
        {
            var path = AppSettings.CrashLogFilePath;
            Directory.CreateDirectory(AppSettings.AppDataDirectory);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");

            var info = new FileInfo(path);
            if (info.Length <= MaxLogBytes) return;

            var text = File.ReadAllText(path);
            // 不能从正中间切：会把一整行劈成两半（看的人拿到一句没头没尾的话）。
            // 也从 tmp 走：在这里崩掉会把「上次崩溃的证据」整个丢掉，而那正是用户最该留的东西。
            var nl = text.IndexOf('\n', text.Length / 2);
            text = nl >= 0 && nl + 1 < text.Length
                ? text.Substring(nl + 1)
                : text.Substring(text.Length / 2);   // 整文件找不出换行（一句超大行）时只能硬切，不然永远缩不下去
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CrashReporter] 写日志失败：{ex.Message}");
        }
    }

    /// <summary>读最后几行（异常启动时用来补充提示内容）。没有文件返回空串。</summary>
    public static string Tail(int lines = 6)
    {
        try
        {
            var path = AppSettings.CrashLogFilePath;
            if (!File.Exists(path)) return "";
            var all = File.ReadAllLines(path);
            return string.Join(" / ", all.Skip(Math.Max(0, all.Length - lines)).Select(l => l.Trim()));
        }
        catch
        {
            return "";
        }
    }

    private static readonly string[] OomPatterns =
    {
        "out of memory", "outofmemory", "cannot allocate", "could not allocate",
        "failed to allocate", "allocation failed", "bad_alloc", "not enough memory",
        "insufficient", "page file", "cudaMalloc", "ggml_backend_cuda", "no buffer memory",
        "failed to reserve", "exceeds available memory",
    };

    private static readonly string[] MmapPatterns =
    {
        "mmap", "MapViewOfFile", "failed to map", "not enough storage",
    };

    private static readonly string[] ContextPatterns =
    {
        "context length", "exceeds the trained", "n_ctx", "no available memory for kv cache", "kv cache",
    };

    private static readonly string[] ModelFilePatterns =
    {
        "not a gguf", "magic", "truncated", "failed to open", "unable to open",
        "does not appear to be", "corrupt", "llama_model_loader: failed",
        "get_tensor_info: failed", "unsupported version",
    };

    /// <summary>文本里是否出现了明显的内存不足字样（大小写不敏感）。</summary>
    public static bool LooksLikeOutOfMemory(string? text) => ContainsAny(text, OomPatterns);

    private static bool ContainsAny(string? text, string[] patterns)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var lower = text.ToLowerInvariant();
        foreach (var p in patterns)
            if (lower.Contains(p)) return true;
        return false;
    }

    /// <summary>
    /// 把引擎的退出信息翻译成人能照着做的一步。
    /// <paramref name="logTail"/> 传引擎最后几十行输出（EngineManager 会留），
    /// <paramref name="availableMb"/> 传退出那一刻的系统可用内存（未知传 -1）。
    /// </summary>
    public static EngineExitAdvice ClassifyEngineExit(int? exitCode, string? logTail, long availableMb)
    {
        var tail = logTail ?? "";

        // KV cache / 上下文申请不到：这类是「上下文设太大」，换小模型也没用，要的是把 -c 调小
        if (!tail.Contains("trained", StringComparison.OrdinalIgnoreCase) && ContainsAny(tail, ContextPatterns))
        {
            return new EngineExitAdvice(false,
                "上下文长度超出了可用内存",
                "模型的 KV 缓存申请不到内存。去「设置」把上下文长度改成现在的一半（比如 8192 → 4096），重新加载模型。");
        }

        if (tail.Contains("trained", StringComparison.OrdinalIgnoreCase))
        {
            return new EngineExitAdvice(false,
                "上下文长度超过模型训练时的上限",
                "这个 GGUF 的原始上下文没那么长。把「设置」里的上下文长度调小（常见 4096），或在下载时选支持长上下文的版本。");
        }

        var oom = LooksLikeOutOfMemory(tail) || (exitCode is not null and not 0 && availableMb >= 0 && availableMb < 512);
        if (oom)
        {
            var mem = availableMb >= 0 ? $"当时系统只剩约 {availableMb / 1024.0:0.#} GB 可用内存。\n" : "";
            return new EngineExitAdvice(true,
                "内存不足，推理引擎被系统关掉了",
                mem + "可以试（按效果排序）：\n" +
                "① 换一个更小的量化档（Q4_K_M 通常比 Q6/Q8 省 30~40% 内存）；\n" +
                "② 把「设置」里的上下文长度减半 —— 上下文越久越吃内存；\n" +
                "③ 关掉浏览器等大户，或重启一次释放内存；\n" +
                "④ 模型实在太大就只能换 3B/1.5B 这类小模型。");
        }

        if (ContainsAny(tail, MmapPatterns))
        {
            return new EngineExitAdvice(false,
                "模型文件映射到内存失败",
                "文件太大或磁盘/内存不够。先关几个占内存的程序；如果反复出现，把模型放到空间充足的 SSD 上再加载。");
        }

        if (ContainsAny(tail, ModelFilePatterns))
        {
            return new EngineExitAdvice(false,
                "模型文件读不了",
                "多半是下载不完整或不是 GGUF。去「模型」页删掉重新下载（或校验一下文件大小）。");
        }

        var code = exitCode is null ? "未知" : exitCode.ToString();
        return new EngineExitAdvice(false,
            $"推理引擎意外退出（代码 {code}）",
            "原因不明确。可以：① 重新加载模型试试；② 换 CPU 版引擎（设置 → 推理引擎）；" +
            "③ 打开 crash-history.log 把最后几行发出来看。");
    }

    /// <summary>把上次异常退出的状态拼成界面上那段话。</summary>
    public static string DescribeAbnormalRun(RunState? state, string crashTail)
    {
        var sb = new StringBuilder();
        if (state != null)
            sb.Append($"上次运行持续 {state.DurationText}");
        if (!string.IsNullOrWhiteSpace(state?.ModelPath))
            sb.Append($"，当时在用模型 {Path.GetFileName(state.ModelPath)}");
        if (!string.IsNullOrWhiteSpace(state?.ConversationTitle))
            sb.Append($"，会话「{state.ConversationTitle}」");
        if (!string.IsNullOrWhiteSpace(crashTail))
            sb.Append($"\n最后的记录：{crashTail}");
        sb.Append("\n已恢复到上次的会话。如果反复出现，先看「设置 → 打开问题记录」。");
        return sb.ToString();
    }
}
