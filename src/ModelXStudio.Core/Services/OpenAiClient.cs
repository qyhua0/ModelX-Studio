// =====================================================================
// 作者：zhenhua
// 功能：与 llama-server 的 OpenAI 兼容接口通信：把 GenerationParams 拼成请求体（max_tokens / seed 这类
//       「默认就不发」的字段只在用户真改过才发）、向 /v1/chat/completions 发起流式请求，
//       逐段解析 SSE 增量文本并忽略异常数据行；进程内共用一个 HttpClient 复用连接。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

using System.Text;
using System.Text.Json;
using ModelXStudio.Core.Models;

namespace ModelXStudio.Core.Services;

/// <summary>与 llama-server 的 OpenAI 兼容接口通信（/v1/chat/completions，SSE 流式）。</summary>
public sealed class OpenAiClient
{
    /// <summary>
    /// 进程级共享 HttpClient（连接池复用）。
    /// 对话框每条消息都新建一个客户端的话，TlsConnection 与 socket 会不断堆积，
    /// 长对话下首字延迟会变高；因此只存 base 地址，请求时用绝对 URL。
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly string _base;

    public OpenAiClient(string baseUrl)
    {
        _base = baseUrl.TrimEnd('/');
    }

    /// <summary>探测引擎健康状态（推理服务就绪）。</summary>
    public async Task<bool> IsAliveAsync()
    {
        try
        {
            using var resp = await Http.GetAsync(_base + "/health");
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 本轮请求的 token 用量（引擎回报的真实值）。用可变对象而不改返回类型：
    /// 流式接口得一个个 yield 文本，拿不出第二个位置放这些数字。
    /// </summary>
    public sealed class TokenUsage
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens { get; set; }

        /// <summary>服务端根本没带 usage（老 llama.cpp / 不接受 stream_options 的服务）。</summary>
        public bool IsEmpty => PromptTokens <= 0 && CompletionTokens <= 0;
    }

    /// <summary>
    /// 某个服务不认 <c>stream_options</c>（老版 llama.cpp 会因此返 400）。
    /// 进程内记一次：不能因为一个可选的统计字段把整个对话弄挂，也不能每条消息都再碰一次 400。
    /// </summary>
    private static volatile bool _usageUnsupported;

    /// <summary>测试用：把「服务不认 stream_options」的记忆恢复成默认（不影响产品逻辑）。</summary>
    public static void ResetUsageSupportForTests() => _usageUnsupported = false;

    /// <summary>
    /// 拼 /v1/chat/completions 的请求体。单独拆出来是为了能不开服务直接断言字段（见验证脚本）。
    /// 取值规则：
    /// • 温度 / top_p / top_k / min_p / repeat_penalty —— 每次都发，意图明确，不依赖服务端默认值；
    ///   （llama.cpp 自 b4500 起，/v1/chat/completions 除了 OpenAI 字段也接受 min_p、repeat_penalty 这类原生参数）
    /// • max_tokens —— 只在 &gt; 0 时发（-1 = 不限，发了反而会把回复截断）；
    /// • seed —— 只在 &gt; 0 时发（0 = 每次随机，不想复现就不拘住服务端的随机）；
    /// • model —— 总发（OpenAI 协议里它是必填；本地 llama-server 不关心，接其他服务时不发行不通）。
    /// • stream_options.include_usage —— 让 llama.cpp 在最后一个 SSE 包里给真实 token 用量（上下文进度条靠它校准）。
    /// </summary>
    public static Dictionary<string, object> BuildPayload(
        IReadOnlyList<ChatMessage> history,
        GenerationParams? generation,
        string? modelFallback = null,
        bool includeUsage = true)
    {
        var g = (generation ?? new GenerationParams()).Clone();
        g.Clamp();

        var model = !string.IsNullOrWhiteSpace(g.ModelId) ? g.ModelId
            : !string.IsNullOrWhiteSpace(modelFallback) ? modelFallback
            : "modelx";

        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = history.Select(m => new { role = m.Role.ToString().ToLowerInvariant(), content = m.Content }).ToList(),
            ["stream"] = true,
            ["temperature"] = g.Temperature,
            ["top_p"] = g.TopP,
            ["top_k"] = g.TopK,
            ["min_p"] = g.MinP,
            ["repeat_penalty"] = g.RepeatPenalty,
        };

        if (g.MaxTokens > 0) payload["max_tokens"] = g.MaxTokens;
        if (g.Seed > 0) payload["seed"] = g.Seed;
        if (includeUsage) payload["stream_options"] = new Dictionary<string, object> { ["include_usage"] = true };

        return payload;
    }

    /// <summary>
    /// 流式对话，逐段返回增量文本。
    /// <paramref name="modelFallback"/> 用于 GenerationParams.ModelId 为空时填 model 字段（一般传当前加载的模型文件名）。
    /// <paramref name="usage"/> 传一个 <see cref="TokenUsage"/> 进去，流结束后里面就是引擎回报的真实 token 数
    /// （调用方要等枚举完再读，中途读可能是 0）。
    /// </summary>
    public async IAsyncEnumerable<string> StreamChatAsync(
        IReadOnlyList<ChatMessage> history,
        GenerationParams? generation = null,
        string? modelFallback = null,
        TokenUsage? usage = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var (response, reader) = await OpenStreamAsync(history, generation, modelFallback, ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                if (!line.StartsWith("data:")) continue;

                var data = line[5..].Trim();
                if (data == "[DONE]") yield break;

                // 字段逐项 TryGetProperty：服务异常时也会发 {"error":…} 这种没有 choices 的行，
                // 直接 GetProperty / [0] 会抛 KeyNotFoundException、IndexOutOfRangeException 把整个对话打断
                string? delta = null;
                try
                {
                    using var doc = JsonDocument.Parse(data);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;

                    // usage 包：choices 是空数组，只有 usage 字段（llama.cpp 与 OpenAI 都是这个形状）
                    if (usage != null &&
                        doc.RootElement.TryGetProperty("usage", out var u) &&
                        u.ValueKind == JsonValueKind.Object)
                    {
                        usage.PromptTokens = ReadInt(u, "prompt_tokens");
                        usage.CompletionTokens = ReadInt(u, "completion_tokens");
                        usage.TotalTokens = ReadInt(u, "total_tokens");
                        if (usage.TotalTokens <= 0) usage.TotalTokens = usage.PromptTokens + usage.CompletionTokens;
                    }

                    if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                        choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                        choices[0].TryGetProperty("delta", out var deltaEl) &&
                        deltaEl.ValueKind == JsonValueKind.Object &&
                        deltaEl.TryGetProperty("content", out var c) &&
                        c.ValueKind == JsonValueKind.String)
                    {
                        delta = c.GetString();
                    }
                }
                catch (JsonException)
                {
                    continue;   // 心跳、注释、非 JSON 行，一律忽略
                }

                if (!string.IsNullOrEmpty(delta)) yield return delta;
            }
        }
        finally
        {
            reader.Dispose();
            response.Dispose();
        }
    }

    private static int ReadInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    /// <summary>
    /// 发请求并拿到 SSE 读流。带 <c>stream_options</c> 被返 400 时去掉头再试一次：
    /// 老版 llama-server（约 b4300 之前）不认这个字段，不能为了统计 token 让人聊不了天。
    /// </summary>
    private async Task<(HttpResponseMessage Response, StreamReader Reader)> OpenStreamAsync(
        IReadOnlyList<ChatMessage> history, GenerationParams? generation, string? modelFallback, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var wantUsage = !_usageUnsupported && attempt == 0;
            var payload = BuildPayload(history, generation, modelFallback, includeUsage: wantUsage);

            using var request = new HttpRequestMessage(HttpMethod.Post, _base + "/v1/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.IsSuccessStatusCode)
            {
                var stream = await response.Content.ReadAsStreamAsync(ct);
                return (response, new StreamReader(stream, Encoding.UTF8));
            }

            var err = await response.Content.ReadAsStringAsync(ct);

            if ((int)response.StatusCode == 400 && wantUsage &&
                err.Contains("stream_options", StringComparison.OrdinalIgnoreCase))
            {
                response.Dispose();
                _usageUnsupported = true;
                System.Diagnostics.Debug.WriteLine("[OpenAi] 该服务不认 stream_options，已停用 token 统计");
                continue;
            }

            response.Dispose();
            // 400 大多是参数名不被识别（接的不是 llama-server，而是一个只认 OpenAI 标准字段的服务），
            // 不提示这一层用户只能去猜是哪个滑块惹的祸
            var hint = (int)response.StatusCode == 400
                ? "\n如果这不是本程序启动的 llama-server，把「微调参数」里的 min-p / 重复惩罚恢复成默认再试。"
                : "";
            throw new InvalidOperationException($"服务返回错误 {(int)response.StatusCode}：{Truncate(err, 300)}{hint}");
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
