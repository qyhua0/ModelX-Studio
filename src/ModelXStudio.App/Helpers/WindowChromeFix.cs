// ====================================================================
// 作者：zhenhua
// 功能：无边框窗口（WindowStyle="None" + WindowChrome）最大化时的边界修正。
//       不修的话 Windows 会把「看不见的调整边框」（Win10 每边约 7 像素）也算进去，
//       实测最大化变成 1934x1094（屏幕 1920x1080、工作区 1920x1050）：既盖住任务栏，
//       界面四边又各被裁掉一条。这里按所在显示器的工作区重算 WM_GETMINMAXINFO，
//       让最大化正好等于工作区（多显示器、不同缩放都正确）。
// ====================================================================
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ModelXStudio.App;

public static class WindowChromeFix
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

    /// <summary>在窗口 SourceInitialized（HWND 已创建）之后调用一次。</summary>
    public static void AttachMaximizeFix(Window window)
    {
        if (window == null) return;
        if (PresentationSource.FromVisual(window) is HwndSource source)
            source.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_GETMINMAXINFO || lParam == IntPtr.Zero) return IntPtr.Zero;
        if (!TryGetWorkArea(hwnd, out var work, out var monitor)) return IntPtr.Zero;

        var mmi = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        // 最大化的位置/尺寸相对「所在显示器左上角」给出，所以要减掉显示器原点，
        // 这样最大化区域 = 工作区（已扣掉任务栏），而不是整块屏幕。
        mmi.MaxPosition.X = work.Left - monitor.Left;
        mmi.MaxPosition.Y = work.Top - monitor.Top;
        mmi.MaxSize.X = work.Right - work.Left;
        mmi.MaxSize.Y = work.Bottom - work.Top;
        Marshal.StructureToPtr(mmi, lParam, true);

        handled = true;
        return IntPtr.Zero;
    }

    private static bool TryGetWorkArea(IntPtr hwnd, out Rect work, out Rect monitor)
    {
        work = default;
        monitor = default;

        var hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (hMonitor == IntPtr.Zero) return false;

        var mi = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfo(hMonitor, ref mi)) return false;

        work = new Rect(mi.WorkLeft, mi.WorkTop, mi.WorkRight, mi.WorkBottom);
        monitor = new Rect(mi.MonitorLeft, mi.MonitorTop, mi.MonitorRight, mi.MonitorBottom);
        return true;
    }

    private readonly record struct Rect(int Left, int Top, int Right, int Bottom);

    // WINMININFO：五个 POINTS（各 2 个 short，共 4 字节），字段顺序不能变
    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MaxTrackSize;
        public Point MinTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfoEx
    {
        public int Size;
        public int MonitorLeft, MonitorTop, MonitorRight, MonitorBottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoEx lpmi);
}
