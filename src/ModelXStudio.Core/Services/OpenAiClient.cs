// =====================================================================
// 作者：zhenhua
// 功能：与 llama-server 的 OpenAI 兼容接口通信：向 /v1/chat/completions 发起流式请求，
//       逐段解析 SSE 增量文本并忽略异常数据行；进程内共用一个 HttpClient 复用连接。
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

    /// <summary>流式对话，逐段返回增量文本。</summary>
    public async IAsyncEnumerable<string> StreamChatAsync(
        IReadOnlyList<ChatMessage> history,
        (double Temp, double TopP, int TopK) sampling,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var payload = new
        {
            messages = history.Select(m => new { role = m.Role.ToString().ToLowerInvariant(), content = m.Content }),
            stream = true,
            temperature = sampling.Temp,
            top_p = sampling.TopP,
            top_k = sampling.TopK
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _base + "/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"服务返回错误 {(int)response.StatusCode}：{Truncate(err, 300)}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

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
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("choices", out var choices) &&
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

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
