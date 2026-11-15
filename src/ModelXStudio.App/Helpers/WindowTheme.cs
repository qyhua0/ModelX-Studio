// =====================================================================
// 作者：zhenhua
// 功能：用 DWM 属性把系统还负责画的那部分（窗口边框、Snap 预览、系统标题栏）
//       也染成深色。注：直接指定标题栏底色/文字色的属性 34/35/36 只在 Win11 22000+
//       存在（Win10 上返回 E_INVALIDARG），Win10 1809+ 只有「深色模式」开关；
//       所以标题栏本身已改为 Controls/TitleBar 自绘，这里是补充，全部尽力而为、失败不报错。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ModelXStudio.App;

public static class WindowTheme
{
    // DWM 窗口属性（dwmapi.h 里没有公开常量名时，这里用官方文档给出的数值）
    private const int CaptionColor = 35;      // Win11 22000+
    private const int BorderColor = 34;       // Win11 22000+
    private const int TextColor = 36;         // Win11 22000+
    private const int ImmersiveDarkMode = 20; // Win10 20H1+
    private const int ImmersiveDarkModeLegacy = 19; // Win10 1809 ~ 2004

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// 在窗口 SourceInitialized（HWND 已创建）之后调用。
    /// captionBrushKey 给标题栏底色（仅 Win11 生效；我们的窗口自己画了标题栏，
    /// 传导航同色即可，万一系统仍画了一部分就不会出现白边）；
    /// 边框用稍亮一档的描边色，避免窗口在深色桌面上「糊成一块」。
    /// </summary>
    public static void ApplyDarkChrome(Window window,
        string captionBrushKey = "BrushBg",
        string borderBrushKey = "BrushBorderStrong",
        string textBrushKey = "BrushCaptionText")
    {
        if (window == null) return;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return; // 窗口还没显示，没有 HWND

        // 1) 先开深色模式：Win10 上这是唯一能做的（标题栏变深灰，但颜色不可定制）
        var on = 1;
        if (DwmSetWindowAttribute(hwnd, ImmersiveDarkMode, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, ImmersiveDarkModeLegacy, ref on, sizeof(int));

        // 2) Win11：精确染色。不支持的系统会返回错误码，直接忽略即可
        if (TryColorRef(window, captionBrushKey, out var caption))
            DwmSetWindowAttribute(hwnd, CaptionColor, ref caption, sizeof(int));
        if (TryColorRef(window, borderBrushKey, out var border))
            DwmSetWindowAttribute(hwnd, BorderColor, ref border, sizeof(int));
        if (TryColorRef(window, textBrushKey, out var text))
            DwmSetWindowAttribute(hwnd, TextColor, ref text, sizeof(int));
    }

    /// <summary>取画刷颜色并转成 DWM 要的 COLORREF（0x00BBGGRR，字节序和 WPF 相反）。</summary>
    private static bool TryColorRef(Window window, string key, out int colorRef)
    {
        colorRef = 0;
        if (window.TryFindResource(key) is not SolidColorBrush brush) return false;

        var c = brush.Color;
        colorRef = (c.B << 16) | (c.G << 8) | c.R;
        return true;
    }
}
