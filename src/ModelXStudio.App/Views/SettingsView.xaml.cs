// =====================================================================
// 作者：zhenhua
// 功能：设置页代码后台：只做 InitializeComponent，交互逻辑都在 SettingsViewModel。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

using System.Windows.Controls;

namespace ModelXStudio.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }
}
