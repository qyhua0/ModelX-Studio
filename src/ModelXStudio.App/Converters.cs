// =====================================================================
// 作者：zhenhua
// 功能：界面值转换器：bool → Visibility（BoolVis）、false → Visibility（FalseVis）、
//       true → false（InvBool，绑定 IsEnabled 用，例如模型运行中禁止切换）、
//       ModelFit → 指示灯画刷（FitLed，模型适配等级的小绿灯）。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ModelXStudio.Core.Models;

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

/// <summary>
/// ModelFit（模型适配等级）→「指示灯」画刷，用来替掉文案里的 🟢🔵🟡⚠️（WPF 不支持彩色字体，
/// 那些 emoji 只会画成灰白空心圈，看着像灯没亮）。画刷在 Themes/Dark.xaml：
/// LedGreenCore / LedBlueCore / LedAmberCore / LedRedCore / LedGreyCore（灯芯，中心高光），
/// ConverterParameter 传 halo 时给对应的外圈柔光 Led*Halo。主题里找不到时退回纯色，不会出现「没有灯」。
/// </summary>
public sealed class ModelFitToLedConverter : IValueConverter
{
    private static readonly Dictionary<ModelFit, (string Core, string Halo)> BrushKeys = new()
    {
        [ModelFit.GpuFast] = ("LedGreenCore", "LedGreenHalo"),
        [ModelFit.CpuOk] = ("LedBlueCore", "LedBlueHalo"),
        [ModelFit.Tight] = ("LedAmberCore", "LedAmberHalo"),
        [ModelFit.TooBig] = ("LedRedCore", "LedRedHalo"),
        [ModelFit.Unknown] = ("LedGreyCore", "LedGreyHalo"),
    };

    private static readonly Dictionary<ModelFit, Color> FallbackColors = new()
    {
        [ModelFit.GpuFast] = Color.FromRgb(0x52, 0xB7, 0x88),
        [ModelFit.CpuOk] = Color.FromRgb(0x6C, 0x8E, 0xEF),
        [ModelFit.Tight] = Color.FromRgb(0xD9, 0xA0, 0x5B),
        [ModelFit.TooBig] = Color.FromRgb(0xDE, 0x6B, 0x6E),
        [ModelFit.Unknown] = Color.FromRgb(0x6E, 0x77, 0x84),
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fit = value is ModelFit f ? f : ModelFit.Unknown;
        var halo = parameter is string p && p.Equals("halo", StringComparison.OrdinalIgnoreCase);
        var (core, haloKey) = BrushKeys.TryGetValue(fit, out var k) ? k : ("LedGreyCore", "LedGreyHalo");

        if (Application.Current?.TryFindResource(halo ? haloKey : core) is Brush found)
            return found;

        var c = FallbackColors.TryGetValue(fit, out var color) ? color : Colors.Gray;
        return new SolidColorBrush(halo ? Color.FromArgb(0x40, c.R, c.G, c.B) : c);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
