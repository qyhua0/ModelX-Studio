// =====================================================================
// 作者：zhenhua
// 功能：推理引擎生命周期管理。引擎（llama.cpp 的 llama-server）随程序一起分发，本类只负责在
//       runtime\llama（以及旧版 %LOCALAPPDATA% 目录）里发现 CPU / CUDA 变体、按偏好与显卡选版本、
//       以子进程方式启动并只监听 127.0.0.1、轮询 /health 判断就绪、处理端口占用与残留进程、
//       停止与状态事件、引擎日志转发；引擎文件缺失时给出可操作的中文指引（不再联网下载）。
// 许可：ModelX Studio 非商业许可 1.0（见仓库根 LICENSE）。上游 llama.cpp 为 MIT 许可，不受本协议约束。
// =====================================================================

using System.Diagnostics;
using System.Text.RegularExpressions;
using ModelXStudio.Core.Models;

namespace ModelXStudio.Core.Services;

public enum EngineStatus
{
    /// <summary>没有找到 llama.cpp 引擎文件。</summary>
    NotInstalled,
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
/// <summary>
/// 引擎变体。llama.cpp 官方 Windows 包分三种后端：CPU、CUDA（N 卡）、Vulkan（A 卡 / Intel 核显 / N 卡皆可）。
/// </summary>
public enum EngineVariant { Cpu, Cuda, Vulkan }

public sealed record EngineState(EngineStatus Status, string? ModelPath, string Message);

/// <summary>引擎一次退出的现场信息（代码、当时可用内存、日志尾部），用于归类原因和写问题记录。</summary>
public sealed record EngineExitInfo(int? ExitCode, long AvailableMb, string LogTail, DateTime At, string? ModelPath, string Reason);

/// <summary>
/// 推理引擎管理器。引擎二进制（llama.cpp 官方 Windows 版里的 llama-server.exe 及其 dll）
/// <b>随程序一起打包分发</b>，程序运行时不再联网下载任何东西；本类负责发现它、选中合适的变体、
/// 以子进程方式跑起来并通过它的 HTTP 接口提供服务（与 LM Studio 同思路）。
/// 检测到 NVIDIA 显卡时优先使用 CUDA 版，生成速度可提升数倍。
/// 另外盯着子进程是不是「自己死了」（内存不足被系统干掉 / 自己 abort）：
/// 以前这种情况只会让界面停在「模型已就绪」然后请求全失败，现在会立刻报错并给出下一步。
/// </summary>
public sealed class EngineManager : IAsyncDisposable
{
    /// <summary>llama.cpp 官方发布页（引擎文件缺失时提示用户去这里取，或由打包脚本一次性拉取）。</summary>
    public const string ReleasesPage = "https://github.com/ggml-org/llama.cpp/releases";

    private const string ExeName = "llama-server.exe";

    private readonly SettingsService _settings;
    private readonly SystemInfoService? _systemInfo;

    private Process? _process;
    private readonly object _sync = new();

    /// <summary>串行化「加载/切换模型」，避免两次点击同时拉起两个 llama-server 抢同一端口。</summary>
    private readonly SemaphoreSlim _startLock = new(1, 1);

    /// <summary>当前运行实例实际占用的端口；未运行时为 null（设置里改端口不会让地址串台）。</summary>
    private int? _runningPort;

    /// <summary>引擎输出最后若干行，崩了的时候拿它判断原因（OOM / KV 申请不到 / 模型文件坏了）。</summary>
    private readonly List<string> _logTail = new();
    private const int LogTailLines = 40;

    /// <summary>正在跑启动流程：这段时间的进程退出由启动循环报，不要和 Exited 事件抢着写状态。</summary>
    private volatile bool _starting;

    public event Action<EngineState>? StateChanged;
    public event Action<string>? LogReceived;

    /// <summary>引擎非主动停止地死掉了（内存不足 / abort）。UI 订阅它把真实原因显示出来。</summary>
    public event Action<EngineExitInfo>? UnexpectedExit;

    public EngineState State { get; private set; } = new(EngineStatus.NotInstalled, null, "正在查找推理引擎…");

    /// <summary>最近一次引擎退出（不管是不是主动停的）。null = 本次运行还没退出过。</summary>
    public EngineExitInfo? LastExit { get; private set; }

    /// <summary>
    /// 当前这次启动传给引擎的 <c>-c</c>（上下文长度），未运行时 0。
    /// 聊天页的上下文进度条必须按这个值算：设置里改了 8192 但没重新加载模型，引擎其实还在跑 4096，
    /// 按设置值显示会让人以为还有余量，然后被引擎拒掉。
    /// </summary>
    public int LastContextArg { get; private set; }

    /// <summary>日志尾部拼成一段文本（设置页/提示里展示用）。</summary>
    public string LogTail { get { lock (_logTail) return string.Join("\n", _logTail); } }

    /// <summary>
    /// 自己死掉的那个 Process 对象。不在 Exited 回调里直接 Dispose（.NET 内部还要接着用它上报事件，
    /// 当场释放偶发会炸 ObjectDisposedException），暂存下来等下次启动/停止时释放。
    /// </summary>
    private Process? _deadProcess;

    private void RememberLog(string line)
    {
        lock (_logTail)
        {
            _logTail.Add(line);
            if (_logTail.Count > LogTailLines) _logTail.RemoveRange(0, _logTail.Count - LogTailLines);
        }
    }

    /// <summary>当前运行实例使用的引擎变体。</summary>
    public EngineVariant? RunningVariant { get; private set; }

    public EngineManager(SettingsService settings, SystemInfoService? systemInfo = null)
    {
        _settings = settings;
        _systemInfo = systemInfo;
        RefreshInstalledState();
    }

    private static string DirName(EngineVariant v) => v switch
    {
        EngineVariant.Cuda => "cuda",
        EngineVariant.Vulkan => "vulkan",
        _ => "cpu",
    };

    /// <summary>
    /// 在候选根目录里扫描 llama-server.exe。允许三种摆放方式：
    /// <c>runtime\llama\cpu\llama-server.exe</c>（规范）、<c>runtime\llama\llama-server.exe</c>（摊平）、
    /// 以及解压时自带一层 <c>llama-b5102-bin-win-cuda-12.4-x64\</c> 子目录。
    /// 路径里含 “cuda” 的算 CUDA 版、含 “vulkan” 的算 Vulkan 版，其余算 CPU 版。
    /// 官方 CUDA 包按 CUDA 版本分发（如 cuda-12.4-x64、cuda-13.5-x64），所以同层可能出现两份 CUDA 版，
    /// 具体用哪一份由 <see cref="FindExecutable"/> 的确定性排序决定。
    /// </summary>
    private IEnumerable<(string Exe, EngineVariant Variant)> ScanExecutables()
    {
        foreach (var root in _settings.Current.EngineRootCandidates())
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            string[] found;
            try
            {
                found = Directory.GetFiles(root, ExeName, SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Engine] 扫描 {root} 失败：{ex.Message}");
                continue;
            }

            foreach (var exe in found)
            {
                var rel = Path.GetRelativePath(root, exe);
                yield return (exe, Classify(rel));
            }
        }
    }

    private static EngineVariant Classify(string relativePath) =>
        relativePath.Contains("vulkan", StringComparison.OrdinalIgnoreCase) ? EngineVariant.Vulkan
        : relativePath.Contains("cuda", StringComparison.OrdinalIgnoreCase) ? EngineVariant.Cuda
        : EngineVariant.Cpu;

    /// <summary>
    /// 同一个变体有多份时的优先级：规范目录（cuda\，不带版本号）最优先，然后按 CUDA 主版本号从小到大。
    /// 比如同时放了 cuda-12.4 与 cuda-13.5，选 12.4 —— 它对显卡驱动版本的要求更宽，能跑的人群更多。
    /// </summary>
    private static int FlavorRank(string exe)
    {
        var dir = Path.GetDirectoryName(exe) ?? "";
        var m = Regex.Match(dir, @"(?:cuda|cublas)[-_ ]?(\d+)", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, out var major) ? major : 0;
    }

    /// <summary>指定变体的可执行文件完整路径；没找到返回 null。排序完全确定：先按 Flavor、再按路径深度。</summary>
    public string? FindExecutable(EngineVariant want) =>
        ScanExecutables()
            .Where(e => e.Variant == want)
            .OrderBy(e => FlavorRank(e.Exe))
            .ThenBy(e => e.Exe.Length)
            .ThenBy(e => e.Exe, StringComparer.OrdinalIgnoreCase)
            .Select(e => e.Exe)
            .FirstOrDefault();

    public bool IsInstalled(EngineVariant v) => FindExecutable(v) != null;

    /// <summary>三个变体里有没有任意一个可用。</summary>
    public bool AnyInstalled => FirstInstalled() != null;

    /// <summary>实际找到的第一个引擎可执行文件（按变体规范目录优先），一个都没有返回 null。</summary>
    public string? FirstInstalled() =>
        FindExecutable(EngineVariant.Cpu) ?? FindExecutable(EngineVariant.Cuda) ?? FindExecutable(EngineVariant.Vulkan);

    /// <summary>同上，但返回变体；一个都没装返回 null。</summary>
    private EngineVariant? FirstInstalledVariant()
    {
        if (IsInstalled(EngineVariant.Cpu)) return EngineVariant.Cpu;
        if (IsInstalled(EngineVariant.Cuda)) return EngineVariant.Cuda;
        if (IsInstalled(EngineVariant.Vulkan)) return EngineVariant.Vulkan;
        return null;
    }

    /// <summary>该变体现在所在的目录；没装则给出应当放入的目录（供“打开文件夹”按钮）。</summary>
    public string FolderFor(EngineVariant v) =>
        FindExecutable(v) is { } exe
            ? Path.GetDirectoryName(exe)!
            : Path.Combine(RootForWrites(), DirName(v));

    /// <summary>用户指定的目录优先，其次随包目录——新建子目录时往这里写。</summary>
    private string RootForWrites() =>
        !string.IsNullOrWhiteSpace(_settings.Current.EngineRoot)
            ? _settings.Current.EngineRoot
            : AppSettings.BundledEngineRoot();

    /// <summary>当前应当使用的引擎目录（用于设置页展示）。</summary>
    public string ActiveEngineFolder =>
        (FindExecutable(RecommendedVariant) ?? FirstInstalled()) is { } found
            ? Path.GetDirectoryName(found)!
            : Path.Combine(AppSettings.BundledEngineRoot(), DirName(RecommendedVariant));

    public static string VariantName(EngineVariant v) => v switch
    {
        EngineVariant.Cuda => "CUDA 加速版",
        EngineVariant.Vulkan => "Vulkan 加速版",
        _ => "CPU 版",
    };

    /// <summary>缺失时提示用户应当找的文件名样式（官方包名带构建号，这里用占位）。</summary>
    public static string AssetHint(EngineVariant v) => v switch
    {
        EngineVariant.Cuda => "llama-b<构建号>-bin-win-cuda-12.x-x64.zip",
        EngineVariant.Vulkan => "llama-b<构建号>-bin-win-vulkan-x64.zip",
        _ => "llama-b<构建号>-bin-win-cpu-x64.zip",
    };

    /// <summary>
    /// 读取 runtime\llama\VERSION.txt 里的构建号（形如 b5102）。该文件由 scripts/get-llama.ps1 写入；
    /// 手动解压的用户没有这个文件也没关系，返回 null，界面只显示变体名。
    /// </summary>
    public string? InstalledEngineBuild()
    {
        foreach (var root in _settings.Current.EngineRootCandidates())
        {
            var file = Path.Combine(root, "VERSION.txt");
            if (!File.Exists(file)) continue;
            try
            {
                var match = Regex.Match(File.ReadAllText(file), @"b\d{3,6}");
                if (match.Success) return match.Value;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Engine] 读取 {file} 失败：{ex.Message}");
            }
        }
        return null;
    }

    /// <summary>给界面用的一行引擎说明，例如「CPU 版 · b5102」。</summary>
    public string EngineSummary
    {
        get
        {
            if (!AnyInstalled) return "未找到引擎文件";
            var build = InstalledEngineBuild();
            var names = new List<string>();
            if (IsInstalled(EngineVariant.Cpu)) names.Add("CPU 版");
            if (IsInstalled(EngineVariant.Cuda)) names.Add("CUDA 版");
            if (IsInstalled(EngineVariant.Vulkan)) names.Add("Vulkan 版");
            return string.Join(" + ", names) + (build == null ? "" : $" · {build}");
        }
    }

    /// <summary>根据用户偏好与显卡检测给出应推荐的引擎变体。</summary>
    public EngineVariant RecommendedVariant
    {
        get
        {
            var pref = _settings.Current.EnginePreference;
            if (pref == "Cpu") return EngineVariant.Cpu;
            if (pref == "Cuda") return EngineVariant.Cuda;
            if (pref == "Vulkan") return EngineVariant.Vulkan;
            return HasNvidia() ? EngineVariant.Cuda : EngineVariant.Cpu;
        }
    }

    /// <summary>有没有能用 CUDA 的 N 卡（显存未知也先认为可用，真跑不起来会在日志里看到回退）。</summary>
    private bool HasNvidia()
    {
        var nvidia = _systemInfo?.Hardware.Gpus
            .FirstOrDefault(g => g.Vendor == GpuVendor.Nvidia && g.IsDiscrete)
            ?? _systemInfo?.Hardware.Gpus.FirstOrDefault(g => g.Vendor == GpuVendor.Nvidia);
        return nvidia != null && (nvidia.VramGb == null || nvidia.VramGb >= 2);
    }

    /// <summary>有没有任意显卡（含核显）。Vulkan 后端能吃到 Intel Iris / AMD 的共享显存。</summary>
    private bool HasAnyGpu() => _systemInfo?.Hardware.Gpus.Count > 0;

    /// <summary>只有 CPU 版、但检测到 N 卡 → 提示用户可以补上 CUDA 版（随包只带了 CPU 版时常见）。</summary>
    public bool GpuUpgradeAvailable =>
        _settings.Current.EnginePreference != "Cpu"
        && IsInstalled(EngineVariant.Cpu)
        && !IsInstalled(EngineVariant.Cuda)
        && !IsInstalled(EngineVariant.Vulkan)
        && RecommendedVariant == EngineVariant.Cuda;

    /// <summary>引擎文件缺失时给用户的完整指引（多行，界面按换行显示）。</summary>
    public string MissingEngineMessage
    {
        get
        {
            var v = RecommendedVariant;
            var text = "没有找到推理引擎 llama-server.exe。\n" +
                       $"请把 llama.cpp 的 Windows 版解压到这个目录：\n{FolderFor(v)}\n" +
                       $"需要的是 {AssetHint(v)}（解压后把里面的 llama-server.exe 和全部 .dll 放在同一层）。\n";
            if (v == EngineVariant.Cpu)
            {
                text += "如果没有 NVIDIA 显卡但有其它显卡（包括 Intel 核显），用 Vulkan 包提速明显：\n" +
                        $"{AssetHint(EngineVariant.Vulkan)} 解压到 {FolderFor(EngineVariant.Vulkan)}\n";
            }
            return text + $"下载页：{ReleasesPage}\n放好后重启本程序即可。";
        }
    }

    private static string UrlFor(int port) => $"http://127.0.0.1:{port}";

    /// <summary>引擎对外地址。运行中用实例真实端口，未运行时用设置里的端口。</summary>
    public string BaseUrl => UrlFor(_runningPort ?? _settings.Current.ServerPort);

    private void SetState(EngineStatus status, string? model, string message)
    {
        State = new EngineState(status, model, message);
        StateChanged?.Invoke(State);
    }

    /// <summary>重新扫描引擎目录并刷新状态（用户在提示里补完文件后可调用）。</summary>
    public void RefreshInstalledState()
    {
        if (!AnyInstalled)
        {
            SetState(EngineStatus.NotInstalled, null, MissingEngineMessage);
            return;
        }

        var summary = EngineSummary;
        if (State.Status is EngineStatus.NotInstalled or EngineStatus.Failed)
            SetState(EngineStatus.Ready, null, $"引擎就绪（{summary}），选择一个模型开始");
        else if (State.Status == EngineStatus.Ready)
            SetState(EngineStatus.Ready, null, $"引擎就绪（{summary}），选择一个模型开始");
    }

    /// <summary>
    /// 打开（必要时创建）某变体应当存放引擎的目录，方便用户把 llama.cpp 的文件放进去。
    /// 返回实际打开的目录路径。
    /// </summary>
    public string OpenEngineFolder(EngineVariant v)
    {
        var folder = FolderFor(v);
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start("explorer.exe", $"\"{folder}\"");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Engine] 打开目录失败：{ex.Message}");
        }
        return folder;
    }

    /// <summary>加载指定模型（若已有其它模型在运行会先停掉）。引擎缺失时直接报错并给出放置指引。</summary>
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
        if (!File.Exists(modelPath))
        {
            SetState(EngineStatus.Failed, null, "模型文件不存在，可能已被移动或删除。");
            return;
        }

        var variant = PickStartVariant();
        var exe = FindExecutable(variant);
        if (exe == null)
        {
            // 引擎是随包文件，程序不会联网补下载：只能告诉用户放哪里
            SetState(EngineStatus.Failed, null, MissingEngineMessage);
            return;
        }

        lock (_sync)
        {
            KillProcessNoLock();
        }

        var s = _settings.Current;
        // 启动参数取 Generation（而不是只取预设三档）：聊天页改滑块时请求体会覆盖这些默认值，
        // 但引擎自带的 WebUI / 别的客户端调进来时只看得到启动参数，两边保持一致才不致「同一个模型两种脾气」。
        var g = s.Generation;
        g.Clamp();
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

        var args = BuildLaunchArgs(modelPath, g, s.ContextSize, port, variant);
        LastContextArg = s.ContextSize;

        lock (_logTail) _logTail.Clear();
        _starting = true;

        SetState(EngineStatus.Loading, modelPath, variant switch
        {
            EngineVariant.Cuda => "正在以 CUDA 加速加载模型…",
            EngineVariant.Vulkan => "正在以 Vulkan 加速加载模型…",
            _ => "正在加载模型，首次加载大模型可能需要十几秒…",
        });
        LogReceived?.Invoke($"引擎目录：{Path.GetDirectoryName(exe)}");
        LogReceived?.Invoke($"启动（{VariantName(variant)}" +
                            (InstalledEngineBuild() is { } b ? $" {b}" : "") +
                            $"）：llama-server {args}");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
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

            proc.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                RememberLog(e.Data);
                LogReceived?.Invoke(e.Data);
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                RememberLog(e.Data);
                LogReceived?.Invoke(e.Data);
            };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // 盯住引擎自己死掉（跑一半被系统干掉）：不监控的话界面会停在「模型已就绪」，
            // 用户只觉得「它不回话了」，实际卜进程早没了
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) => OnEngineExited(proc, modelPath);

            // 轮询 /health 直到就绪（模型加载完成）
            using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var deadline = DateTime.UtcNow.AddSeconds(300);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                if (proc.HasExited)
                {
                    var info = ReportExit(proc, modelPath, "加载阶段退出");
                    lock (_sync) { KillProcessNoLock(); }
                    RunningVariant = null;
                    _runningPort = null;
                    SetState(EngineStatus.Failed, null,
                        $"{info.Headline}。\n{info.Detail}");
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
        finally
        {
            _starting = false;
        }
    }

    /// <summary>
    /// 子进程退出事件。只有「我们没有主动停它」时才报错：
    /// KillProcessNoLock 会先把 _process 置 null，所以这里用引用比对就能分辨是用户停的还是它自己死的。
    /// </summary>
    private void OnEngineExited(Process proc, string modelPath)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_process, proc)) return;   // 我们停的（或已经换了一个实例）
            _process = null;
            _runningPort = null;
            RunningVariant = null;
            LastContextArg = 0;
        }
        if (_starting) return;   // 加载阶段由启动循环报，不重复说

        var advice = ReportExit(proc, modelPath, "运行中退出");
        SetState(EngineStatus.Failed, null, $"模型运行中引擎退出了。\n{advice.Headline}。\n{advice.Detail}");
        UnexpectedExit?.Invoke(LastExit!);

        lock (_sync) { _deadProcess = proc; }
    }

    /// <summary>
    /// 记录退出现场（写 crash-history.log）并归类原因。
    /// 读 ExitCode 要防一手：进程刚被 Kill 时可能报异常；读不到当 null 处理，不能因此把整个提示弄挂。
    /// </summary>
    private EngineExitAdvice ReportExit(Process proc, string modelPath, string stage)
    {
        int? code = null;
        try { code = proc.HasExited ? proc.ExitCode : (int?)null; }
        catch { /* 进程对象已释放 */ }

        var avail = SystemInfoService.AvailableRamMb();
        string tail;
        lock (_logTail) tail = string.Join("\n", _logTail);

        var advice = CrashReporter.ClassifyEngineExit(code, tail, avail);
        LastExit = new EngineExitInfo(code, avail, tail, DateTime.Now, modelPath, stage);
        CrashReporter.Append($"[{stage}] 引擎退出 代码={code ?? -1} 可用内存={avail}MB · {advice.Headline}");
        return advice;
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
    /// 清理本应用遗留的 llama-server 进程。只处理可执行文件位于引擎候选目录内的进程，
    /// 不碰用户自己手动启动的 llama.cpp。
    /// </summary>
    private int KillOrphanInstances()
    {
        var killed = 0;
        // 引擎可能有多个根目录（用户指定 + 随包 + 旧版），逐个比对前缀，避免误杀或漏杀
        var roots = _settings.Current.EngineRootCandidates()
            .Select(r => r.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var p in Process.GetProcessesByName("llama-server"))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path == null || !roots.Any(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
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

    /// <summary>
    /// 本次加载实际用哪个变体。用户明确指定的优先；指定的那个没放就别硬失败，退回能用的。
    /// 「自动」下：有 N 卡用 CUDA；没有 N 卡但随包带了 Vulkan 且本机有显卡（含核显）就用 Vulkan
    /// —— Iris Xe 这类核显有十几 GB 共享显存，比纯 CPU 快得多；都没有就用 CPU。
    /// </summary>
    private EngineVariant PickStartVariant()
    {
        var pref = _settings.Current.EnginePreference;
        if (pref is "Cpu" or "Cuda" or "Vulkan")
        {
            var wanted = pref == "Cuda" ? EngineVariant.Cuda : pref == "Vulkan" ? EngineVariant.Vulkan : EngineVariant.Cpu;
            if (IsInstalled(wanted)) return wanted;
            return FirstInstalledVariant() ?? wanted;
        }

        if (HasNvidia() && IsInstalled(EngineVariant.Cuda)) return EngineVariant.Cuda;
        if (HasAnyGpu() && IsInstalled(EngineVariant.Vulkan)) return EngineVariant.Vulkan;
        return FirstInstalledVariant() ?? RecommendedVariant;
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
            LastContextArg = 0;    // 上下文进度条回到「按设置值估算」
        }

        // 本来就没在跑时不要伪报一次「服务已停止」，否则会覆盖引擎缺失等真实状态
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

            // 顺手释放上次自己死掉留下的那个进程对象（见 _deadProcess 注释）
            try { _deadProcess?.Dispose(); } catch { }
            _deadProcess = null;
        }
    }

    /// <summary>三档预设 → 温度 / top-p / top-k（真正的定义在 GenerationParams，这里只是老调用点的入口）。</summary>
    public static (double Temp, double TopP, int TopK) PresetFor(string preset) => GenerationParams.PresetFor(preset);

    /// <summary>
    /// 拼 llama-server 的命令行。单独拆成静态方法是为了能不起服务就断言参数（见 D:\tools\verify\mxstudio\param）。
    /// 取值规则与请求体一致：采样五项总发，-n / -s 只在用户真设了才发（-1 本来就是 llama.cpp 的「不限 / 随机」语义）。
    /// 用列表过滤 null 而不是拼完再 Replace("  ", " ")——模型路径里可能有连续空格，那种写法会把文件名改坏。
    /// </summary>
    public static string BuildLaunchArgs(string modelPath, GenerationParams generation, int contextSize,
        int port, EngineVariant variant)
    {
        var g = generation ?? new GenerationParams();
        g.Clamp();
        return string.Join(' ', new[]
        {
            $"-m \"{modelPath}\"",
            "--host 127.0.0.1",
            $"--port {port}",
            $"-c {contextSize}",
            $"--temp {N(g.Temperature)}",
            $"--top-p {N(g.TopP)}",
            $"--top-k {g.TopK}",
            $"--min-p {N(g.MinP)}",
            $"--repeat-penalty {N(g.RepeatPenalty)}",
            g.MaxTokens > 0 ? $"-n {g.MaxTokens}" : null,
            g.Seed > 0 ? $"-s {g.Seed}" : null,
            $"--n-gpu-layers {(variant == EngineVariant.Cpu ? 0 : 999)}",
            "--jinja",
            "--no-webui",
        }.Where(a => !string.IsNullOrEmpty(a)));
    }

    /// <summary>
    /// 命令行里的数字一律用不变文化：“0.05”不能变成“0,05”——
    /// 系统区域设成德语/法语时 {0:0.##} 会输出逗号，llama-server 会把参数当成无效值。
    /// </summary>
    private static string N(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    public ValueTask DisposeAsync()
    {
        lock (_sync) { KillProcessNoLock(); }
        _startLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
