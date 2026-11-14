// =====================================================================
// 作者：zhenhua
// 功能：界面值转换器：bool → Visibility（BoolVis）、false → Visibility（FalseVis）、
//       true → false（InvBool，绑定 IsEnabled 用，例如模型运行中禁止切换）。
// =====================================================================

using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ModelXStudio.App;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class FalseToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// true → false（绑定到 IsEnabled 等布尔目标，例如「模型正在运行时禁止切换」）。
/// 注意：这个转换器只用于 bool→bool，Visibility 请 BoolVis / FalseVis。
/// </summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}
