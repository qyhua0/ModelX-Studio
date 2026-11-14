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
