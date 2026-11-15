// =====================================================================
// 作者：zhenhua
// 功能：本地服务页代码后台：日志文本变化后让日志框自动滚动到底部。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
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
