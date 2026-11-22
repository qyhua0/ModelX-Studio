// =====================================================================
// 作者：zhenhua
// 功能：WPF 应用入口。创建核心服务与四个页面 ViewModel（进程内单例，供各界面直接取用），
//       启动时加载设置、异步检测硬件、按设置自动加载上次使用的模型，
//       并兜住所有未处理异常（说人话提示 + 落盘日志），退出时确保 llama-server 进程被结束。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ModelXStudio.Core.Models;
using ModelXStudio.Core.Services;
using ModelXStudio.App.ViewModels;

namespace ModelXStudio.App;

/// <summary>应用主体：服务定位器 + 生命周期 + 全局异常兜底。</summary>
public partial class App : Application
{
    public static SettingsService SettingsService { get; private set; } = null!;
    public static DownloadService DownloadService { get; private set; } = null!;
    public static ModelCatalogService CatalogService { get; private set; } = null!;
    public static EngineManager Engine { get; private set; } = null!;
    public static ChatStore Store { get; private set; } = null!;
    public static SystemInfoService SystemInfo { get; private set; } = null!;
    public static LocalModelScanner Scanner { get; private set; } = null!;
    public static RunStateService RunState { get; private set; } = null!;

    /// <summary>
    /// 上次没退干净留下的记录（null = 上次正常退出 / 第一次跑）。
    /// 开始本次运行时就读出来了，聊天页拿它决定回到哪个会话，主窗口拿它弹「上次异常退出」的提示。
    /// </summary>
    public static RunState? PreviousRun { get; private set; }

    /// <summary>
    /// 页面跳转（聊天页上「去设置改上下文」这类按钮用）。由 MainWindow 构造时挂上；
    /// 没挂上时静默：提示本身比跳转重要，不能因为找不到导航把提示弄挂。
    /// </summary>
    public static Action<string>? NavigateRequested { get; set; }

    public static ChatViewModel Chat { get; private set; } = null!;
    public static ModelsViewModel Models { get; private set; } = null!;
    public static ServerViewModel Server { get; private set; } = null!;
    public static SettingsViewModel Settings { get; private set; } = null!;

    /// <summary>异常日志目录（与 settings.json 同级下的 logs）。</summary>
    public static string CrashLogDirectory => Path.Combine(AppSettings.AppDataDirectory, "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 面向不懂技术的用户：任何未处理异常都要给出看得懂的提示并留下日志，绝不能静默闪退
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        SettingsService = new SettingsService();
        SettingsService.Load();

        DownloadService = new DownloadService();
        CatalogService = new ModelCatalogService();
        Store = new ChatStore();
        Scanner = new LocalModelScanner();

        SystemInfo = new SystemInfoService();
        Engine = new EngineManager(SettingsService, SystemInfo);
        FireAndForget(() => SystemInfo.DetectAsync(), "检测本机硬件");

        // 崩溃恢复的账要在写本次状态之前读，否则上次的痕迹就被盖掉了（RunStateService.BeginRun）。
        // 注意顺序：必须在建 ChatViewModel 之前 —— 建 VM 时要靠这份记录选会话。
        RunState = new RunStateService();
        PreviousRun = RunState.BeginRun(SettingsService.Current.LastModelPath, 0);
        if (PreviousRun != null && !string.IsNullOrWhiteSpace(PreviousRun.ConversationId))
        {
            // 上次是崩的：以它最后待过的会话为准（设置里那个可能还是更早的）
            SettingsService.Current.LastConversationId = PreviousRun.ConversationId;
            SettingsService.Save();
        }

        Models = new ModelsViewModel();
        Chat = new ChatViewModel();
        Server = new ServerViewModel();
        Settings = new SettingsViewModel();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // 异常退出的提示放在窗口出来之后：窗口之前弹会让用户看到一个孤零的对话框，不知道背后进没进去
        if (PreviousRun != null)
            ShowAbnormalRunNotice();

        MaybeAutoLoadLastModel();
    }

    /// <summary>
    /// 按设置决是否自动加载上次的模型。三个开关要同时成立：勾了自动加载、记着上次的路径、文件还在。
    /// 文件已被移动/删除时不弹错误（引擎页会显示真实状态），只把这条过期记录擦掉，
    /// 免得每次启动都白试一次。手动模式下用户自己从下拉里选。
    /// </summary>
    private static void MaybeAutoLoadLastModel()
    {
        var s = SettingsService.Current;
        var path = s.LastModelPath;

        if (!s.AutoLoadLastModel)
        {
            // 不自动加载也要把引擎状态说清楚，否则聊天页顶着「引擎未就绪」没人知道为什么
            Chat.AwaitLoadHint = string.IsNullOrWhiteSpace(path)
                ? "已关掉自动加载：在上方下拉里选一个模型开始"
                : $"已关掉自动加载：上次用的是 {Path.GetFileName(path)}，在这里选它即可";
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            Chat.AwaitLoadHint = "第一次使用：到上方下拉选一个模型，或先去「模型库」下载";
            return;
        }
        if (!File.Exists(path))
        {
            Chat.AwaitLoadHint = $"上次的模型文件不见了：{path}";
            s.LastModelPath = null;
            SettingsService.Save();
            return;
        }

        // 开机自动加载不能弹窗（会让人以为程序卡死），但内存明显不够时也不硬上：
        // 崩一次比多选一次麻烦多了。
        if (!ModelLoadGuard.Confirm(path, ModelLoadAsking.Silent))
        {
            Chat.AwaitLoadHint = $"内存不够，没自动加载 {Path.GetFileName(path)}：" +
                                 "关掉几个占内存的程序后在上方下拉里选它（或换个小的量化档）";
            return;
        }

        Chat.AwaitLoadHint = $"正在加载上次的模型 {Path.GetFileName(path)}…";
        FireAndForget(() => Models.LoadModelAsync(path), "自动加载上次的模型");
    }

    /// <summary>上次没退干净：把现场（多久、哪个模型、最后几行记录）告诉用户，并让他选要不要看问题记录。</summary>
    private static void ShowAbnormalRunNotice()
    {
        var text = CrashReporter.DescribeAbnormalRun(PreviousRun, CrashReporter.Tail());
        var openLog = ThemedDialog.Confirm(text, "上次没正常退出", "打开问题记录", "知道了");
        if (openLog) OpenCrashLog();
    }

    /// <summary>用系统默认程序打开问题记录（crash-history.log）。失败退到打开所在文件夹。</summary>
    public static void OpenCrashLog()
    {
        try
        {
            var path = AppSettings.CrashLogFilePath;
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(AppSettings.AppDataDirectory);
                File.WriteAllText(path, "还没有记录。下次出问题时这里会有内容。\n");
            }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogError("打开问题记录", ex);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppSettings.AppDataDirectory) { UseShellExecute = true });
            }
            catch (Exception ex2) { LogError("打开设置目录", ex2); }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Engine.Stop();
        }
        catch (Exception ex)
        {
            LogError("退出时停止推理服务", ex);
        }

        try
        {
            // 盖上「正常退出」的戳：下次启动读到它就不会跟用户提「上次没退干净」。
            // 写失败无所谓（磁盘满），不能因此不让用户关窗口。
            RunState.EndRun(Chat.IsBusy ? "退出时正在生成回复" : "用户手动关闭");
        }
        catch (Exception ex)
        {
            LogError("写运行状态", ex);
        }

        base.OnExit(e);
    }

    /// <summary>
    /// 启动一个不阻塞界面的任务，并把异常记进日志。
    /// 直接写 <c>_ = SomeAsync()</c> 的话异常会被静默吞掉，用户只会觉得「点了没反应」。
    /// </summary>
    public static async void FireAndForget(Func<Task> work, string what)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            LogError(what, ex);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError("界面操作", e.Exception);
        ShowCrashDialog(e.Exception);
        // 继续运行，不让用户正在进行的事（例如下载到一半）整体丢掉
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "未知错误");
        LogError("后台线程", ex);
        ShowCrashDialog(ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogError("后台任务", e.Exception);
        e.SetObserved();
    }

    private static void ShowCrashDialog(Exception ex)
    {
        ThemedDialog.Warn(
            $"操作出错了：{ex.Message}\n\n" +
            $"详情已记录到这个文件夹：\n{CrashLogDirectory}\n" +
            "程序会尽量继续运行。如果同样的问题反复出现，请重启程序。",
            "ModelX Studio");
    }

    /// <summary>把异常写入按天分文件的日志；日志本身写不了时绝不再抛异常。</summary>
    public static void LogError(string where, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(CrashLogDirectory);
            var file = Path.Combine(CrashLogDirectory, $"error-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}：{ex}\n\n");
        }
        catch
        {
            // 磁盘不可写时忽略，日志不能反过来搞崩程序
        }
    }
}
