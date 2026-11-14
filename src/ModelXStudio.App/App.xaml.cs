// =====================================================================
// 作者：zhenhua
// 功能：WPF 应用入口。创建核心服务与四个页面 ViewModel（进程内单例，供各界面直接取用），
//       启动时加载设置、异步检测硬件、按设置自动加载上次使用的模型，
//       并兜住所有未处理异常（说人话提示 + 落盘日志），退出时确保 llama-server 进程被结束。
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
        Engine = new EngineManager(SettingsService, DownloadService, SystemInfo);
        FireAndForget(() => SystemInfo.DetectAsync(), "检测本机硬件");

        Models = new ModelsViewModel();
        Chat = new ChatViewModel();
        Server = new ServerViewModel();
        Settings = new SettingsViewModel();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        if (SettingsService.Current.AutoLoadLastModel &&
            !string.IsNullOrWhiteSpace(SettingsService.Current.LastModelPath) &&
            File.Exists(SettingsService.Current.LastModelPath))
        {
            FireAndForget(() => Models.LoadModelAsync(SettingsService.Current.LastModelPath!), "自动加载上次的模型");
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
