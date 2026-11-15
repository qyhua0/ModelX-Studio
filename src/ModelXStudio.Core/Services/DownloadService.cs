// =====================================================================
// 作者：zhenhua
// 功能：下载服务。为模型文件与推理引擎提供带断点续传、失败重试、完整性校验的
//       HTTP 下载，并向界面回报实时进度与瞬时速度。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

namespace ModelXStudio.Core.Services;

/// <summary>一次下载进度快照（接收字节数 / 总字节数 / 瞬时速度）。</summary>
public sealed record DownloadProgress(long ReceivedBytes, long TotalBytes, double BytesPerSecond)
{
    /// <summary>完成百分比；服务器未给出总长度时为 null。</summary>
    public double? Percent => TotalBytes > 0 ? Math.Clamp(ReceivedBytes * 100.0 / TotalBytes, 0, 100) : null;

    /// <summary>给普通用户看的速度文案。</summary>
    public string SpeedText => BytesPerSecond >= 1024 * 1024
        ? $"{BytesPerSecond / 1024 / 1024:0.0} MB/s"
        : $"{BytesPerSecond / 1024:0} KB/s";
}

/// <summary>
/// 支持断点续传的 HTTP 文件下载。
/// 先写 &lt;目标文件&gt;.part，全部完成且长度校验通过后再改名，避免半个文件被当成模型加载。
/// </summary>
public sealed class DownloadService
{
    /// <summary>共用的 HttpClient（进程级连接池，避免每次下载新建连接）。</summary>
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(30)
    };

    /// <summary>单次下载最多尝试次数（含首次）。</summary>
    private const int MaxAttempts = 3;

    /// <summary>下载文件到 targetPath；取消时抛 OperationCanceledException 并保留 .part 以便续传。</summary>
    public async Task DownloadAsync(string url, string targetPath,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var partPath = targetPath + ".part";

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            // 每次尝试都重新读取已下载的字节数（上一次中断留下的 .part 用于续传）
            var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

            try
            {
                var completed = await TryDownloadOnceAsync(url, partPath, existing, progress, ct);
                if (!completed) continue;   // 服务器不支持续传，已清空 .part，重头再来

                // 下载完成，原子改名；目标已存在则覆盖
                File.Move(partPath, targetPath, overwrite: true);

                var finalSize = new FileInfo(targetPath).Length;
                progress?.Report(new DownloadProgress(finalSize, finalSize, 0));
                return;
            }
            catch (OperationCanceledException)
            {
                throw;      // 用户取消：保留 .part，向上传播
            }
            catch (Exception) when (attempt < MaxAttempts - 1 && !ct.IsCancellationRequested)
            {
                await Task.Delay(1500 * (attempt + 1), ct);     // 退避后重试
            }
        }

        // 走到这里说明重试次数用尽（或循环开始前就已取消）
        ct.ThrowIfCancellationRequested();
        throw new IOException($"下载失败：已尝试 {MaxAttempts} 次仍未完成。请检查网络，或在设置中更换下载镜像。");
    }

    /// <summary>
    /// 单次下载尝试。返回 false 表示「服务器不支持 Range，需删除 .part 从头下载」。
    /// 长度不足或连接中断时抛异常，由上层重试续传。
    /// </summary>
    private static async Task<bool> TryDownloadOnceAsync(string url, string partPath, long existing,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // 必须先判状态码：否则 404/403 的错误页会被当成模型文件写进磁盘
        if (!response.IsSuccessStatusCode)
        {
            var detail = await SafeReadBodyAsync(response, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
            {
                // 区间请求不被接受（部分镜像实现不规范），删掉残片整包重下，保证结果正确
                TryDelete(partPath);
                return false;
            }

            throw new IOException($"服务器返回 {(int)response.StatusCode}。{Truncate(detail, 200)}");
        }

        // 请求了续传但服务器返回 200（不支持 Range）→ 从头下载
        if (existing > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            TryDelete(partPath);
            return false;
        }

        // 总长度 = 本次剩余长度 + 已有长度
        long total = response.Content.Headers.ContentLength ?? -1;
        if (total > 0) total += existing;

        await using (var fs = new FileStream(partPath,
            existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        {
            var buffer = new byte[1024 * 256];
            long received = existing;
            var lastReport = existing;
            var lastReportAt = DateTime.UtcNow;

            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;

                if (received - lastReport >= 1024 * 512 ||
                    (DateTime.UtcNow - lastReportAt).TotalMilliseconds > 500)
                {
                    // 用区间差值算瞬时速度：累计平均在续传时会虚高
                    var seconds = (DateTime.UtcNow - lastReportAt).TotalSeconds;
                    var speed = seconds > 0.05 ? (received - lastReport) / seconds : 0;
                    lastReport = received;
                    lastReportAt = DateTime.UtcNow;
                    progress?.Report(new DownloadProgress(received, total, speed));
                }
            }
        }

        // 流正常结束但长度不够 = 连接被静默截断，不能当成下载完成
        if (total > 0 && existing < total)
        {
            var actual = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (actual < total)
                throw new IOException($"下载不完整（{actual}/{total} 字节），正在续传…");
        }

        return true;
    }

    /// <summary>读取错误响应体（失败时返回空串，不影响主异常）。</summary>
    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct); }
        catch { return ""; }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 残片删除失败不影响重试 */ }
    }
}
