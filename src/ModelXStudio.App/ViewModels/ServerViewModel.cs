// =====================================================================
// 作者：zhenhua
// 功能：本地服务页逻辑：启停推理服务、显示 OpenAI 兼容地址与 curl 示例（一键复制）、
//       把引擎 stderr 日志按帧合并刷到界面并只保留尾部，避免长日志拖慢 UI。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

using System.IO;
using System.Text;
using System.Threading;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ModelXStudio.Core.Models;
using ModelXStudio.Core.Services;

namespace ModelXStudio.App.ViewModels;

public partial class ServerViewModel : ObservableObject
{
    public ObservableCollection<LocalModel> ModelOptions { get; } = new();

    [ObservableProperty]
    private LocalModel? _selectedModel;

    [ObservableProperty]
    private string _statusLine = "";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private string _baseUrl = "";

    [ObservableProperty]
    private string _logText = "";

    private readonly object _logSync = new();
    private readonly Queue<string> _pendingLogs = new();

    /// <summary>0 = 没有待处理刷新，1 = 已排一帧。用 Interlocked 避免多线程重复排帧。</summary>
    private int _logFlushPending;

    public ServerViewModel()
    {
        RefreshModelOptions();
        App.Engine.StateChanged += state =>
            Application.Current?.Dispatcher.Invoke(() => OnEngineState(state));
        App.Engine.LogReceived += line => AppendLog(line);
        OnEngineState(App.Engine.State);
    }

    public void RefreshModelOptions()
    {
        var models = App.Scanner.Scan(App.SettingsService.Current.ModelsDirectory);
        ModelOptions.Clear();
        foreach (var m in models) ModelOptions.Add(m);
        SelectedModel = ModelOptions.FirstOrDefault(m => m.FilePath == App.Engine.State.ModelPath)
                        ?? ModelOptions.FirstOrDefault(m => m.FilePath == App.SettingsService.Current.LastModelPath)
                        ?? ModelOptions.FirstOrDefault();
    }

    private void OnEngineState(EngineState state)
    {
        IsRunning = state.Status == EngineStatus.Running;
        Busy = state.Status == EngineStatus.Loading;
        BaseUrl = App.Engine.BaseUrl + "/v1";
        StatusLine = state.Message;
    }

    /// <summary>
    /// 追加引擎日志。引擎 stderr 一行一条、模型加载时几十毫秒几十行，
    /// 逐行同步 Invoke + 字符串拼接会把 UI 线程拖住，因此先入队，再按帧合并刷新。
    /// </summary>
    private void AppendLog(string line)
    {
        var app = Application.Current;
        if (app == null) return;

        lock (_logSync) _pendingLogs.Enqueue(line);

        if (Interlocked.Exchange(ref _logFlushPending, 1) == 1) return;

        app.Dispatcher.BeginInvoke(new Action(() =>
        {
            Interlocked.Exchange(ref _logFlushPending, 0);

            string[] batch;
            lock (_logSync)
            {
                if (_pendingLogs.Count == 0) return;
                batch = _pendingLogs.ToArray();
                _pendingLogs.Clear();
            }

            var sb = new StringBuilder(LogText);
            foreach (var l in batch) sb.Append(l).Append(Environment.NewLine);
            var text = sb.ToString();

            // 只保留尾部，避免日志无限增长让 TextBox 越写越卡
            if (text.Length > 30_000) text = text[^15_000..];
            LogText = text;
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (Busy) return;

        if (IsRunning)
        {
            App.Engine.Stop();
            return;
        }

        var path = SelectedModel?.FilePath ?? App.SettingsService.Current.LastModelPath;
        if (path == null || !File.Exists(path))
        {
            ThemedDialog.Info("还没有可用模型，请先到「模型库」下载一个。");
            return;
        }

        await App.Engine.StartModelAsync(path!, CancellationToken.None);
    }

    [RelayCommand]
    private void CopyBaseUrl() => CopyToClipboard(App.Engine.BaseUrl + "/v1");

    [RelayCommand]
    private void CopyExample()
    {
        var example =
$$"""
curl {{App.Engine.BaseUrl}}/v1/chat/completions ^
  -H "Content-Type: application/json" ^
  -d "{"model":"modelx","messages":[{"role":"user","content":"你好"}]}"
""";
        CopyToClipboard(example);
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            // 剪贴板被占用时忽略
        }
    }
}
