// =====================================================================
// 作者：zhenhua
// 功能：主窗口代码后台：给四个页面挂 DataContext；导航单选按钮切换页面显隐，
//       并在切页时刷新对应数据（模型列表 / 运行中的服务）。
// =====================================================================

using System.Windows;
using System.Windows.Controls;

namespace ModelXStudio.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ViewChat.DataContext = App.Chat;
        ViewModels.DataContext = App.Models;
        ViewServer.DataContext = App.Server;
        ViewSettings.DataContext = App.Settings;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 标题栏由 Controls/TitleBar 自绘（底色跟左侧导航一致），系统那份已被 WindowStyle=None 去掉；
        // 这里只把系统还负画的边框 / 预览部分也弄成深色（Win10 只支持开关，Win11 可染色）。
        WindowTheme.ApplyDarkChrome(this, "BrushPanel");

        // 无边框窗口最大化默认会把任务栏盖住、四边各裁掉一条，按工作区修正
        WindowChromeFix.AttachMaximizeFix(this);
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (ViewChat == null) return; // InitializeComponent 期间会提前触发

        var tag = (sender as RadioButton)?.Tag as string;
        ViewChat.Visibility = tag == "chat" ? Visibility.Visible : Visibility.Collapsed;
        ViewModels.Visibility = tag == "models" ? Visibility.Visible : Visibility.Collapsed;
        ViewServer.Visibility = tag == "server" ? Visibility.Visible : Visibility.Collapsed;
        ViewSettings.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;

        // 切页时刷新数据
        switch (tag)
        {
            case "chat":
                App.Chat.RefreshModelOptions();
                break;
            case "models":
                App.Models.Refresh();
                break;
            case "server":
                App.Server.RefreshModelOptions();
                break;
        }
    }
}
