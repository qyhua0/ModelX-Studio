// =====================================================================
// 作者：zhenhua
// 功能：应用内消息框的实现：静态入口 Info / Success / Warn / Error / Confirm，
//       统一深色外观与按钮行为（Enter 确定、Esc 取消），并在主题/资源异常时
//       退回系统 MessageBox，保证「出错提示」本身绝不会再次崩溃。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ModelXStudio.App;

public partial class ThemedDialog : Window
{
    private ThemedDialog()
    {
        InitializeComponent();
        PreviewKeyDown += OnKey;
    }

    protected override void OnSourceInitialized(System.EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowTheme.ApplyDarkChrome(this);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        // 只有「确定」一种按钮时，Esc 也要能关掉（隐藏的取消按钮收不到事件）
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        DialogResult = false;
    }

    /// <summary>普通提示。</summary>
    public static void Info(string message, string title = "提示") =>
        Prompt(message, title, "i", "BrushAccent", false, "确定", "取消", null, out _);

    /// <summary>成功提示。</summary>
    public static void Success(string message, string title = "完成") =>
        Prompt(message, title, "✓", "BrushSuccess", false, "确定", "取消", null, out _);

    /// <summary>注意事项（不严重）。</summary>
    public static void Warn(string message, string title = "注意") =>
        Prompt(message, title, "!", "BrushWarning", false, "确定", "取消", null, out _);

    /// <summary>错误提示。</summary>
    public static void Error(string message, string title = "出错了") =>
        Prompt(message, title, "×", "BrushDanger", false, "确定", "取消", null, out _);

    /// <summary>需要用户决定，返回 true 表示点了确定。</summary>
    public static bool Confirm(string message, string title = "请确认",
        string okText = "确定", string cancelText = "取消") =>
        Prompt(message, title, "?", "BrushAccent", true, okText, cancelText, null, out _);

    /// <summary>
    /// 要用户输一行文字（重命名会话这类）。点「取消」或整个弹窗挂掉时返回 null ——
    /// 调用方要区分「没输入」与「输入了空字符串」，所以不把 null 和 "" 混在一起。
    /// </summary>
    public static string? AskText(string message, string title, string initial, string okText = "保存") =>
        Prompt(message, title, "✎", "BrushAccent", true, okText, "取消", initial, out var text) ? text : null;

    private static bool Prompt(string message, string title, string glyph,
        string glyphBrushKey, bool confirm, string okText, string cancelText,
        string? initial, out string? text)
    {
        text = null;
        var app = Application.Current;
        if (app == null)
        {
            MessageBox.Show(message, title,
                confirm ? MessageBoxButton.OKCancel : MessageBoxButton.OK,
                confirm ? MessageBoxImage.Question : MessageBoxImage.Information);
            return false;
        }

        try
        {
            var dlg = new ThemedDialog();
            dlg.Title = title;
            dlg.Message.Text = message;
            dlg.Glyph.Text = glyph;
            dlg.OkBtn.Content = okText;

            if (dlg.TryFindResource(glyphBrushKey) is SolidColorBrush sb)
                dlg.Glyph.Foreground = sb;

            if (confirm)
            {
                dlg.CancelBtn.Visibility = Visibility.Visible;
                dlg.CancelBtn.Content = cancelText;
            }

            if (initial != null)
            {
                dlg.Input.Visibility = Visibility.Visible;
                dlg.Input.Text = initial;
                // 打开就全选：重命名是「换个名字」，不是「在原名后面接着打字」
                dlg.Input.SelectAll();
                dlg.Input.Focus();
            }

            var owner = app.Windows.OfType<Window>().FirstOrDefault(w => !ReferenceEquals(w, dlg) && w.IsActive && w.IsVisible)
                        ?? (app.MainWindow is { IsVisible: true } mw ? mw : null);
            if (owner != null) dlg.Owner = owner;

            var ok = dlg.ShowDialog() == true;
            if (ok) text = dlg.Input.Text;
            return ok;
        }
        catch
        {
            // 主题/资源出问题时也要能把话递到用户眼前
            MessageBox.Show(message, title,
                confirm ? MessageBoxButton.OKCancel : MessageBoxButton.OK,
                confirm ? MessageBoxImage.Question : MessageBoxImage.Information);
            text = null;
            return false;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
