// =====================================================================
// 作者：zhenhua
// 功能：设置页代码后台：只做 InitializeComponent，交互逻辑都在 SettingsViewModel。
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
