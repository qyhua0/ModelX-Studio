// =====================================================================
// 作者：zhenhua
// 功能：下载服务。为模型文件与推理引擎提供带断点续传、失败重试、完整性校验的
//       HTTP 下载，并向界面回报进度与平滑后的平均速度（SpeedMeter）。
//       另含三条「别把用户的东西弄坏」的护栏：目标文件已存在时默认拒绝覆盖（要显式 overwrite）、
//       动手前先查磁盘剩余空间、下完改名失败时说明是不是模型正被占用。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

using System.Diagnostics;

namespace ModelXStudio.Core.Services;

/// <summary>
/// 一次下载进度快照（已收字节 / 总字节 / 平均速度）。
/// BytesPerSecond 是经过 <see cref="SpeedMeter"/> 平滑的平均值，不是两次读取之间的瞬时值：
/// 界面直接把它绑成一个数字看，瞬时值会跳得根本读不出数。
/// </summary>
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
/// 目标文件已存在且调用方没有明确要求覆盖。
/// 单独一个类型是为了让界面能区分「你还没问用户要不要覆盖」和「网络断了」：
/// 前者应该弹确认框，后者应该提示换镜像重试。
/// </summary>
public sealed class FileAlreadyExistsException : IOException
{
    public FileAlreadyExistsException(string message) : base(message) { }
}

/// <summary>
/// 文件已经下完，但放不到最终位置（多半是目标正被 llama-server 开着）。重试不会自己好：
/// 得让用户先去「本地服务」页停止模型，所以不能被重试循环当成普通网络错误吞掉再重下几 GB。
/// </summary>
public sealed class FinalizeFailedException : IOException
{
    public FinalizeFailedException(string message) : base(message) { }

    public FinalizeFailedException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>磁盘剩余空间不够。重试没有意义（空间不会自己长出来），所以不参与重试退避。</summary>
public sealed class InsufficientDiskSpaceException : IOException
{
    public InsufficientDiskSpaceException(string message) : base(message) { }
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

    /// <summary>
    /// 除文件本身之外还要留给系统和其它程序的余量（字节）。
    /// 模型动辄几个 GB，写到最后一刻才「磁盘已满」等于白等，所以提前留出余量判死。
    /// </summary>
    public long ReserveBytes { get; set; } = 128L * 1024 * 1024;

    /// <summary>
    /// 向界面回报进度的最小间隔。不靠「攒够多少字节」触发：链路快的时候那是每几毫秒刷一次，
    /// 量的还是那一小段的瞬时速度。固定按时间上报，界面才稳得住。
    /// </summary>
    public TimeSpan ProgressReportInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>测速的滑动窗口长度（越长越平稳、反应越慢），转给 <see cref="SpeedMeter.Window"/>。</summary>
    public TimeSpan SpeedWindow { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>速度指数平滑系数（0～1，越小越平），转给 <see cref="SpeedMeter.Smoothing"/>。</summary>
    public double SpeedSmoothing { get; set; } = 0.3;

    /// <summary>断点续传的临时文件路径（界面按它是否存在来显示「上次没下完，可继续」）。</summary>
    public static string PartPathOf(string targetPath) => targetPath + ".part";

    /// <summary>把字节数写成用户看得懂的大小（下载提示、确认框、状态文字共用一套口径）。</summary>
    public static string HumanSize(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024.0 / 1024 / 1024:0.##} GB"
            : bytes >= 1024L * 1024
                ? $"{bytes / 1024.0 / 1024:0.#} MB"
                : $"{bytes / 1024:0} KB";

    /// <summary>
    /// 下载文件到 targetPath；取消时抛 OperationCanceledException 并保留 .part 以便续传。
    /// <paramref name="overwrite"/> 为 false 时，目标文件已存在会直接抛
    /// <see cref="FileAlreadyExistsException"/> 且一个字节都不写 —— 覆盖必须先由界面问过用户。
    /// </summary>
    public async Task DownloadAsync(string url, string targetPath,
        IProgress<DownloadProgress>? progress, CancellationToken ct, bool overwrite = false)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var partPath = PartPathOf(targetPath);

        if (!overwrite && File.Exists(targetPath))
        {
            long len = 0;
            try { len = new FileInfo(targetPath).Length; } catch { /* 并发删除，报 0 不影响结论 */ }
            throw new FileAlreadyExistsException(
                $"「{Path.GetFileName(targetPath)}」本地已经有一份（{HumanSize(len)}），本次没有覆盖任何文件。");
        }

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            // 每次尝试都重新读取已下载的字节数（上一次中断留下的 .part 用于续传）
            var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

            try
            {
                var (completed, lastSpeed) = await TryDownloadOnceAsync(url, partPath, existing, progress, ct);
                if (!completed) continue;   // 服务器不支持续传，已清空 .part，重头再来

                // 下载完成，原子改名。目标正被 llama-server 读着时 Move 会失败，
                // 这里翻译成用户能照做的中文（不然只剩一句「used by another process」）。
                try
                {
                    File.Move(partPath, targetPath, overwrite: overwrite);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new FinalizeFailedException(
                        $"文件已经下完，但放不到最终位置：{ex.Message}\n" +
                        "如果这个模型正在运行，先到「本地服务」页停止，再重新下载一次（残片已保留，不会重头下载）。",
                        ex);
                }

                // 把百分比补到 100%（上一次定时上报最多落后一拍）。速度用最后一次平滑值，
                // 不能填 0：那会在下载收尾时闪一下「0 KB/s」，看上去像刚断网。
                var finalSize = new FileInfo(targetPath).Length;
                progress?.Report(new DownloadProgress(finalSize, finalSize, lastSpeed));
                return;
            }
            catch (OperationCanceledException)
            {
                throw;      // 用户取消：保留 .part，向上传播
            }
            catch (InsufficientDiskSpaceException)
            {
                throw;      // 空间不够：重试三次只会浪费三次时间，直接报给用户
            }
            catch (FinalizeFailedException)
            {
                throw;      // 字节都已经在盘上了，只是放不进去：重试等于把几个 GB 重下一遍
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
    // 不是 static：里面要读本实例的 ReserveBytes（测试把它放大就能验证空间不足那条路径）
    /// <returns>Completed = false 表示「服务器不支持续传，已删残片，请重头再来」；
    /// LastSpeed 是本次最后测到的平滑速度，供收尾上报用（不要拿 0 去填）。</returns>
    private async Task<(bool Completed, double LastSpeed)> TryDownloadOnceAsync(string url, string partPath, long existing,
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
                return (false, 0);
            }

            throw new IOException($"服务器返回 {(int)response.StatusCode}。{Truncate(detail, 200)}");
        }

        // 请求了续传但服务器返回 200（不支持 Range）→ 从头下载
        if (existing > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            TryDelete(partPath);
            return (false, 0);
        }

        // 总长度 = 本次剩余长度 + 已有长度
        long total = response.Content.Headers.ContentLength ?? -1;
        if (total > 0) total += existing;

        // 拿到真实长度就先查一次空间，别下到最后才失败；长度未知时至少要求留出余量
        EnsureDiskSpace(partPath, total > 0 ? (total - existing) + ReserveBytes : ReserveBytes);

        double lastSpeed = 0;
        await using (var fs = new FileStream(partPath,
            existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        {
            var buffer = new byte[1024 * 256];
            long received = existing;

            // 测速与上报：时钟用 Stopwatch（单调、粒度极细）。DateTime.UtcNow 的粒度只有约 15 ms，
            // 半秒的窗口里它自己就能带来成倍的抖动。上报改成固定按时间走，
            // 速度交给 SpeedMeter 做「滑动窗口平均 + 指数平滑」（为什么不能拿相邻两次 ReadAsync 算速度，见那个文件）。
            var meter = new SpeedMeter { Window = SpeedWindow, Smoothing = SpeedSmoothing };
            var clock = Stopwatch.StartNew();
            // 先交一个「起点」，这样第一次上报算的是「开头 0.5 秒的平均」，而不是前 0.5 秒白等。
            meter.Add(existing, 0);
            var nextReportAt = clock.Elapsed + ProgressReportInterval;

            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;

                var now = clock.Elapsed;
                if (now < nextReportAt) continue;
                nextReportAt = now + ProgressReportInterval;

                var speed = meter.Add(received, now.TotalSeconds);

                // 还算不出可信速度时（刚开始那一拍正好没收到字节）别把 0 报上去，界面会闪「0 KB/s」
                if (meter.HasSpeed)
                    progress?.Report(new DownloadProgress(received, total, speed));

                // 边下边复查：磁盘可能在下载途中被别的东西写满
                if (total > 0) EnsureDiskSpace(partPath, (total - received) + ReserveBytes);
            }

            lastSpeed = meter.BytesPerSecond;
        }

        // 流正常结束但长度不够 = 连接被静默截断，不能当成下载完成
        if (total > 0 && existing < total)
        {
            var actual = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (actual < total)
                throw new IOException($"下载不完整（{actual}/{total} 字节），正在续传…");
        }

        return (true, lastSpeed);
    }

    /// <summary>
    /// 检查目标所在磁盘还放得下 neededBytes 与否；放不下抛
    /// <see cref="InsufficientDiskSpaceException"/>。取不到磁盘信息（网络盘、光驱等）时放行，
    /// 让真正写盘那一步去报错，不要在这里拦死正常的网络盘用户。
    /// </summary>
    private void EnsureDiskSpace(string path, long neededBytes)
    {
        if (neededBytes <= 0) return;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return;

            var drive = new DriveInfo(root);
            if (!drive.IsReady) return;

            var free = drive.AvailableFreeSpace;
            if (free >= neededBytes) return;

            throw new InsufficientDiskSpaceException(
                $"磁盘空间不足：还需要 {HumanSize(neededBytes - free)}（{root} 只剩 {HumanSize(free)}）。" +
                "清理点空间，或在「设置」里把模型目录换到大一点的盘。");
        }
        catch (InsufficientDiskSpaceException) { throw; }
        catch { /* 见方法说明 */ }
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
