// ====================================================================
// 作者：zhenhua
// 功能：自绘标题栏控件的代码：把按钮接到所属窗口上（最小化、最大化/还原、关闭），
//       并按窗口的 ResizeMode 决定显示哪几个按钮、按窗口状态换最大化的图标。
// ====================================================================
using System;
using System.Windows;
using System.Windows.Controls;

namespace ModelXStudio.App.Controls;

public partial class TitleBar : UserControl
{
    /// <summary>标题文字。默认由宿主窗口把自己的 Title 绑进来。</summary>
    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(nameof(Caption), typeof(string), typeof(TitleBar),
            new PropertyMetadata(string.Empty));

    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public TitleBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private Window? _owner;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (owner == null) return;

        if (!ReferenceEquals(_owner, owner))
        {
            if (_owner != null) _owner.StateChanged -= OnOwnerStateChanged;
            _owner = owner;
            _owner.StateChanged += OnOwnerStateChanged;
        }

        // 固定尺寸的小对话框只留关闭按钮，跟 Windows 的习惯一致
        MinBtn.Visibility = owner.ResizeMode == ResizeMode.NoResize ? Visibility.Collapsed : Visibility.Visible;
        MaxBtn.Visibility = owner.ResizeMode == ResizeMode.CanResize ? Visibility.Visible : Visibility.Collapsed;
        UpdateGlyph();
    }

    private void OnOwnerStateChanged(object? sender, EventArgs e) => UpdateGlyph();

    private void UpdateGlyph()
    {
        var maximized = _owner?.WindowState == WindowState.Maximized;
        MaxGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        var tip = maximized ? "还原" : "最大化";
        MaxBtn.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(MaxBtn, tip);
    }

    private void Min_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } w) w.WindowState = WindowState.Minimized;
    }

    private void Max_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not { } w) return;
        w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Window.GetWindow(this)?.Close();
    }
}
