// =====================================================================
// 作者：zhenhua
// 功能：推理引擎生命周期管理：按需下载 llama.cpp 的 CPU / CUDA 版 llama-server 并解压到引擎目录，
//       启动子进程加载 GGUF 模型（端口固定只监听 127.0.0.1），轮询 /health 判断就绪，
//       处理端口占用与上次残留进程，负责停止、状态事件与引擎日志转发。
// =====================================================================

using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelXStudio.Core.Models;

namespace ModelXStudio.Core.Services;

public enum EngineStatus
{
    /// <summary>推理引擎（llama-server）尚未安装。</summary>
    NotInstalled,
    /// <summary>正在下载引擎。</summary>
    DownloadingEngine,
    /// <summary>引擎就绪，未启动。</summary>
    Ready,
    /// <summary>正在加载模型。</summary>
    Loading,
    /// <summary>模型已加载，可对话。</summary>
    Running,
    /// <summary>正在停止。</summary>
    Stopping,
    /// <summary>启动失败。</summary>
    Failed
}

/// <summary>引擎变体：CPU 版或 CUDA（N卡）加速版。</summary>
public enum EngineVariant { Cpu, Cuda }

public sealed record EngineState(EngineStatus Status, string? ModelPath, string Message);

/// <summary>
/// 推理引擎管理器：自动下载 llama.cpp 的 llama-server 二进制（Windows x64，CPU 或 CUDA 变体），
/// 以子进程方式运行，并通过其 HTTP 接口提供服务（与 LM Studio 同思路）。
/// 检测到 NVIDIA 显卡时推荐使用 CUDA 版，生成速度可提升数倍。
/// </summary>
public sealed class EngineManager : IAsyncDisposable
{
    private const string ReleasesApi = "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=10";
    private static readonly Regex CpuAsset = new(@"llama-b\d+-bin-win-cpu-x64\.zip$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CudaAsset = new(@"llama-b\d+-bin-win-cuda[-_.a-z0-9]*-x64\.zip$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly SettingsService _settings;
    private readonly DownloadService _downloads;
    private readonly SystemInfoService? _systemInfo;
    /// <summary>
    /// GitHub REST API 要求必须带 User-Agent，缺头会被直接 403（HttpClient 默认不发 UA），
    /// 所以这里显式声明客户端名与版本，并固定 API 版本、Accept 类型。
    /// </summary>
    private readonly HttpClient _http = CreateGitHubClient();

    private static HttpClient CreateGitHubClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("ModelXStudio", "0.1.0"));
        client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }
    private Process? _process;
    private readonly object _sync = new();

    /// <summary>串行化「加载/切换模型」，避免两次点击同时拉起两个 llama-server 抢同一端口。</summary>
    private readonly SemaphoreSlim _startLock = new(1, 1);

    /// <summary>当前运行实例实际占用的端口；未运行时为 null（设置里改端口不会让地址串台）。</summary>
    private int? _runningPort;

    public event Action<EngineState>? StateChanged;
    public event Action<string>? LogReceived;

    public EngineState State { get; private set; } = new(EngineStatus.NotInstalled, null, "引擎未安装");

    /// <summary>当前运行实例使用的引擎变体。</summary>
    public EngineVariant? RunningVariant { get; private set; }

    public EngineManager(SettingsService settings, DownloadService downloads, SystemInfoService? systemInfo = null)
    {
        _settings = settings;
        _downloads = downloads;
        _systemInfo = systemInfo;
        RefreshInstalledState();
    }

    public static string ExeDirFor(EngineVariant v) =>
        Path.Combine(AppSettings.EngineDirectory, v == EngineVariant.Cuda ? "cuda" : "cpu");

    public static string ExePathFor(EngineVariant v) => Path.Combine(ExeDirFor(v), "llama-server.exe");

    /// <summary>旧版单目录布局，兼容为 CPU 版。</summary>
    private static string LegacyExePath => Path.Combine(AppSettings.EngineDirectory, "llama-server.exe");

    public bool IsInstalled(EngineVariant v) =>
        File.Exists(ExePathFor(v)) || (v == EngineVariant.Cpu && File.Exists(LegacyExePath));

    private static string ResolveExePath(EngineVariant v) =>
        File.Exists(ExePathFor(v)) ? ExePathFor(v) : LegacyExePath;

    public static string VariantName(EngineVariant v) => v == EngineVariant.Cuda ? "GPU 加速版" : "CPU 版";

    /// <summary>根据用户偏好与显卡检测给出应推荐的引擎变体。</summary>
    public EngineVariant RecommendedVariant
    {
        get
        {
            var pref = _settings.Current.EnginePreference;
            if (pref == "Cpu") return EngineVariant.Cpu;
            if (pref == "Cuda") return EngineVariant.Cuda;

            var nvidia = _systemInfo?.Hardware.Gpus
                .FirstOrDefault(g => g.Vendor == GpuVendor.Nvidia && g.IsDiscrete)
                ?? _systemInfo?.Hardware.Gpus.FirstOrDefault(g => g.Vendor == GpuVendor.Nvidia);
            return nvidia != null && (nvidia.VramGb == null || nvidia.VramGb >= 2)
                ? EngineVariant.Cuda
                : EngineVariant.Cpu;
        }
    }

    /// <summary>已装 CPU 版、检测到 N 卡且缺 CUDA 版 → 提供一键升级。</summary>
    public bool GpuUpgradeAvailable =>
        _settings.Current.EnginePreference != "Cpu"
        && IsInstalled(EngineVariant.Cpu)
        && !IsInstalled(EngineVariant.Cuda)
        && RecommendedVariant == EngineVariant.Cuda;

    private static string UrlFor(int port) => $"http://127.0.0.1:{port}";

    /// <summary>引擎对外地址。运行中用实例真实端口，未运行时用设置里的端口。</summary>
    public string BaseUrl => UrlFor(_runningPort ?? _settings.Current.ServerPort);

    private void SetState(EngineStatus status, string? model, string message)
    {
        State = new EngineState(status, model, message);
        StateChanged?.Invoke(State);
    }

    public void RefreshInstalledState()
    {
        if (!IsInstalled(EngineVariant.Cpu) && !IsInstalled(EngineVariant.Cuda))
            SetState(EngineStatus.NotInstalled, null, "首次使用需要下载推理引擎（自动完成）");
        else if (State.Status is EngineStatus.NotInstalled or EngineStatus.Failed)
            SetState(EngineStatus.Ready, null, "引擎就绪，选择一个模型开始");
    }

    /// <summary>下载并解压指定变体（幂等）。variant=null 表示使用推荐变体。</summary>
    public async Task EnsureEngineAsync(EngineVariant? variant, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var target = variant ?? RecommendedVariant;
        if (IsInstalled(target)) return;

        SetState(EngineStatus.DownloadingEngine, null, $"正在获取 {VariantName(target)} 引擎版本信息…");
        try
        {
            var zipUrl = await ResolveAssetUrlAsync(target, ct);
            LogReceived?.Invoke($"引擎下载地址：{zipUrl}");

            SetState(EngineStatus.DownloadingEngine, null, $"正在下载 {VariantName(target)} 引擎…");
            var zipPath = Path.Combine(Path.GetTempPath(), $"modelx-engine-{target}.zip");

            // 把下载百分比并入状态消息，自动安装（无 UI 传进度）时用户也能看到
            var inner = new Progress<DownloadProgress>(p =>
            {
                var detail = p.Percent is double pc
                    ? $"{pc:0}%"
                    : $"{p.ReceivedBytes / 1024.0 / 1024:0} MB";
                SetState(EngineStatus.DownloadingEngine, null,
                    $"正在下载 {VariantName(target)} 引擎… {detail} · {p.SpeedText}");
                progress?.Report(p);
            });

            await _downloads.DownloadAsync(zipUrl, zipPath, inner, ct);

            SetState(EngineStatus.DownloadingEngine, null, "正在解压引擎…");
            var dir = ExeDirFor(target);
            Directory.CreateDirectory(dir);
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    var name = Path.GetFileName(entry.FullName);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!(name.Equals("llama-server.exe", StringComparison.OrdinalIgnoreCase)
                          || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))) continue;

                    entry.ExtractToFile(Path.Combine(dir, name), overwrite: true);
                }
            }
            TryDelete(zipPath);

            if (!IsInstalled(target))
            {
                SetState(EngineStatus.Failed, null, "引擎解压后未找到 llama-server.exe");
                return;
            }

            SetState(EngineStatus.Ready, null, $"引擎就绪（{VariantName(target)}），选择一个模型开始");
        }
        catch (OperationCanceledException)
        {
            RefreshInstalledState();
            throw;
        }
        catch (Exception ex)
        {
            SetState(EngineStatus.Failed, null, $"引擎下载失败：{ex.Message}。可重试，或到设置中更换网络镜像。");
        }
    }

    /// <summary>在最近的官方 release 中寻找匹配变体的资产包。</summary>
    private async Task<string> ResolveAssetUrlAsync(EngineVariant variant, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(ReleasesApi, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var code = (int)resp.StatusCode;
            throw new InvalidOperationException(code is 403 or 429
                ? $"获取引擎版本信息失败：GitHub 接口访问受限（{code}，未登录时每小时最多 60 次）。请稍后重试，或在「设置」页打开引擎目录后手动放入 llama-server。"
                : $"获取引擎版本信息失败：GitHub 接口返回 {code}。请检查网络后重试。");
        }
        using var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct);
        if (doc == null) throw new InvalidOperationException("无法解析 GitHub release 信息");

        var pattern = variant == EngineVariant.Cuda ? CudaAsset : CpuAsset;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (!release.TryGetProperty("assets", out var assets)) continue;

            var matches = assets.EnumerateArray()
                .Select(a => a.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "")
                .Where(url => pattern.IsMatch(url))
                .ToList();

            if (matches.Count == 0) continue;

            // 同时有 CUDA 12/13 包时选 12：对显卡驱动版本要求更低，老卡兼容更好
            return matches.FirstOrDefault(m => m.Contains("cuda-12", StringComparison.OrdinalIgnoreCase)
                                            || m.Contains("cuda-cu12", StringComparison.OrdinalIgnoreCase))
                   ?? matches[0];
        }

        throw new InvalidOperationException($"未在官方发布中找到 {VariantName(variant)} 的 Windows x64 引擎包");
    }

    /// <summary>加载指定模型（若已有其它模型在运行会先停掉）。引擎缺失时自动安装推荐变体。</summary>
    public async Task StartModelAsync(string modelPath, CancellationToken ct)
    {
        await _startLock.WaitAsync(ct);
        try
        {
            await StartModelCoreAsync(modelPath, ct);
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>真正的启动流程，只允许 StartModelAsync 在持有 _startLock 时调用。</summary>
    private async Task StartModelCoreAsync(string modelPath, CancellationToken ct)
    {
        if (!IsInstalled(EngineVariant.Cpu) && !IsInstalled(EngineVariant.Cuda))
        {
            await EnsureEngineAsync(null, null, ct);
            if (!IsInstalled(EngineVariant.Cpu) && !IsInstalled(EngineVariant.Cuda)) return; // 失败状态已上报
        }

        var variant = PickStartVariant();
        if (!IsInstalled(variant))
        {
            await EnsureEngineAsync(variant, null, ct);
            if (!IsInstalled(variant)) return;
        }

        lock (_sync)
        {
            KillProcessNoLock();
        }

        var s = _settings.Current;
        var (temp, topP, topK) = PresetFor(s.GenerationPreset);
        var port = s.ServerPort;

        // 端口被残留进程占用时必须先报错：否则 /health 会被旧进程秒回，界面误报「模型已就绪」
        if (await PortBusyAsync(port, ct))
        {
            if (KillOrphanInstances() > 0)
                await Task.Delay(800, ct);
            if (await PortBusyAsync(port, ct))
            {
                SetState(EngineStatus.Failed, null,
                    $"端口 {port} 已被占用（可能有一个没退干净的 llama-server）。请在「设置」里换一个端口后重试。");
                return;
            }
        }

        var args = string.Join(' ',
            $"-m \"{modelPath}\"",
            "--host 127.0.0.1",
            $"--port {port}",
            $"-c {s.ContextSize}",
            $"--temp {temp:0.##}",
            $"--top-p {topP:0.##}",
            $"--top-k {topK}",
            $"--n-gpu-layers {(variant == EngineVariant.Cuda ? 999 : 0)}",
            "--jinja",
            "--no-webui");

        SetState(EngineStatus.Loading, modelPath, variant == EngineVariant.Cuda
            ? "正在以 GPU 加速加载模型…"
            : "正在加载模型，首次加载大模型可能需要十几秒…");
        LogReceived?.Invoke($"启动（{VariantName(variant)}）：llama-server {args}");

        var psi = new ProcessStartInfo
        {
            FileName = ResolveExePath(variant),
            Arguments = args,
            WorkingDirectory = Path.GetDirectoryName(ResolveExePath(variant))!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        try
        {
            var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 llama-server");
            lock (_sync) { _process = proc; }
            RunningVariant = variant;

            proc.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) LogReceived?.Invoke(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) LogReceived?.Invoke(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // 轮询 /health 直到就绪（模型加载完成）
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var deadline = DateTime.UtcNow.AddSeconds(300);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                if (proc.HasExited)
                {
                    lock (_sync) { KillProcessNoLock(); }
                    RunningVariant = null;
                    _runningPort = null;
                    SetState(EngineStatus.Failed, null,
                        $"模型加载失败（进程退出，代码 {proc.ExitCode}）。请检查模型文件、减小上下文长度，或在设置中改用 CPU 引擎。");
                    return;
                }

                try
                {
                    using var health = await probe.GetAsync($"{BaseUrl}/health", ct);
                    if (health.IsSuccessStatusCode)
                    {
                        _runningPort = port;
                        s.LastModelPath = modelPath;
                        _settings.Save();
                        SetState(EngineStatus.Running, modelPath,
                            $"模型已就绪（{VariantName(variant)}），可以开始对话");
                        return;
                    }
                }
                catch
                {
                    // 服务尚未监听，继续等待
                }

                await Task.Delay(800, ct);
            }

            // 超时说明模型太大或机器扛不住：必须杀掉进程，否则它会在后台白占内存
            lock (_sync) { KillProcessNoLock(); }
            RunningVariant = null;
            _runningPort = null;
            SetState(EngineStatus.Failed, null,
                "模型加载超时（已等待 5 分钟），引擎已停止。请换更小的量化档，或把上下文长度调小。");
        }
        catch (OperationCanceledException)
        {
            Stop();
            throw;
        }
        catch (Exception ex)
        {
            lock (_sync) { KillProcessNoLock(); }
            RunningVariant = null;
            _runningPort = null;
            SetState(EngineStatus.Failed, null, $"启动失败：{ex.Message}");
        }
    }

    /// <summary>端口上是否已有 HTTP 服务在应答（不区分 200/404，有应答就算被占用）。</summary>
    private static async Task<bool> PortBusyAsync(int port, CancellationToken ct)
    {
        try
        {
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var resp = await probe.GetAsync(UrlFor(port) + "/health", ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 清理本应用遗留的 llama-server 进程。只处理可执行文件位于本应用引擎目录内的进程，
    /// 不碰用户自己手动启动的 llama.cpp。
    /// </summary>
    private static int KillOrphanInstances()
    {
        var killed = 0;
        foreach (var p in Process.GetProcessesByName("llama-server"))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path == null ||
                    !path.StartsWith(AppSettings.EngineDirectory, StringComparison.OrdinalIgnoreCase))
                    continue;

                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
                killed++;
            }
            catch
            {
                // 无权限访问 MainModule，或进程已退出
            }
            finally
            {
                p.Dispose();
            }
        }
        return killed;
    }

    /// <summary>优先用推荐变体；推荐未安装但有已装变体时先用已装的（避免意外大下载）。</summary>
    private EngineVariant PickStartVariant()
    {
        var rec = RecommendedVariant;
        if (IsInstalled(rec)) return rec;
        var other = rec == EngineVariant.Cuda ? EngineVariant.Cpu : EngineVariant.Cuda;
        return IsInstalled(other) ? other : rec;
    }

    public async Task SwitchModelAsync(string modelPath, CancellationToken ct)
    {
        Stop();
        await StartModelAsync(modelPath, ct);
    }

    public void Stop()
    {
        bool wasRunning;
        lock (_sync)
        {
            wasRunning = _process is { HasExited: false };
            if (wasRunning)
            {
                SetState(EngineStatus.Stopping, State.ModelPath, "正在停止服务…");
                KillProcessNoLock();
            }
            RunningVariant = null;
            _runningPort = null;
        }

        // 本来就没在跑时不要伪报一次「服务已停止」，否则会覆盖下载/失败等真实状态
        if (wasRunning)
            SetState(EngineStatus.Ready, null, "服务已停止");
    }

    private void KillProcessNoLock()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch
        {
            // 进程可能已退出
        }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
    }

    public static (double Temp, double TopP, int TopK) PresetFor(string preset) => preset switch
    {
        "Fast" => (0.3, 0.9, 20),
        "Quality" => (0.8, 0.95, 40),
        _ => (0.6, 0.92, 30)
    };

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略临时文件清理失败 */ }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync) { KillProcessNoLock(); }
        _http.Dispose();
        _startLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
