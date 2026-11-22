// =====================================================================
// 作者：zhenhua
// 功能：聊天页代码后台：订阅/注销 VM 的滚动请求并合并为每帧一次滚动到底部；
//       处理输入框快捷键（Enter 发送、Shift+Enter 换行）、会话列表的双击改名 /
//       F2 / Delete / 搜索框 Esc 清空（这几个都要摸控件，写在 XAML 里做不到）。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

using System.Windows.Controls;
using System.Windows.Input;
using ModelXStudio.App.ViewModels;
using ModelXStudio.Core.Models;

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

    /// <summary>双击会话名 = 改名。改名是整理会话时最高频的动作，只放在右键菜单里太埋了。</summary>
    private void ChatList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ChatViewModel vm && ChatList.SelectedItem is Conversation conv)
        {
            vm.RenameChatCommand.Execute(conv);
            e.Handled = true;
        }
    }

    /// <summary>会话列表上的键盘：F2 改名、Delete 删除（和资源管理器一致，不用先绕到右键）。</summary>
    private void ChatList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ChatViewModel vm) return;
        if (ChatList.SelectedItem is not Conversation conv) return;

        if (e.Key == Key.F2)
        {
            vm.RenameChatCommand.Execute(conv);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            vm.DeleteChatCommand.Execute(conv);
            e.Handled = true;
        }
    }

    /// <summary>搜索框里 Esc = 清空：比退格删干净快，也比「重新输入全部」便宜。</summary>
    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ChatViewModel vm && vm.ChatSearch.Length > 0)
        {
            vm.ChatSearch = "";
            e.Handled = true;
        }
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
