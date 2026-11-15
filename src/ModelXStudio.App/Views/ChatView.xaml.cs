// =====================================================================
// 作者：zhenhua
// 功能：聊天页代码后台：订阅/注销 VM 的滚动请求并合并为每帧一次滚动到底部；
//       处理输入框快捷键（Enter 发送、Shift+Enter 换行）。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

using System.Windows.Controls;
using System.Windows.Input;
using ModelXStudio.App.ViewModels;

namespace ModelXStudio.App.Views;

public partial class ChatView : UserControl
{
    public ChatView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is ChatViewModel vm) vm.ScrollRequested += ScrollToBottom;
        };
        Unloaded += (_, _) =>
        {
            if (DataContext is ChatViewModel vm) vm.ScrollRequested -= ScrollToBottom;
        };
    }

    private bool _scrollPending;

    /// <summary>
    /// 滚动到底部。流式回复时每个增量都会请求一次，这里合并为每帧最多一次，
    /// 并用 Background 优先级排到布局完成之后：原来用同步 Invoke，token 一多 UI 线程会被 ScrollIntoView 拖住。
    /// </summary>
    private void ScrollToBottom()
    {
        if (_scrollPending) return;
        _scrollPending = true;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _scrollPending = false;
            if (MessageList.Items.Count > 0)
                MessageList.ScrollIntoView(MessageList.Items[MessageList.Items.Count - 1]);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Enter 发送，Shift+Enter 换行
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            if (DataContext is ChatViewModel vm && !vm.IsBusy)
                vm.SendCommand.Execute(null);
        }
    }
}
