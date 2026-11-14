// =====================================================================
// 作者：zhenhua
// 功能：本地服务页代码后台：日志文本变化后让日志框自动滚动到底部。
// =====================================================================

using System.Windows.Controls;

namespace ModelXStudio.App.Views;

public partial class ServerView : UserControl
{
    public ServerView()
    {
        InitializeComponent();
    }

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        LogBox.ScrollToEnd();
    }
}
