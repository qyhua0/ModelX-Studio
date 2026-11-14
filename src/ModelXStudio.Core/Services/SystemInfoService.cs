// =====================================================================
// 作者：zhenhua
// 功能：本机硬件检测：CPU 型号与核数/线程数、物理内存、显卡型号与显存。
//       显存优先用 nvidia-smi 取精确值，其余走 WMI（Win32_Processor / Win32_VideoController），
//       全部在后台线程执行，任何一步失败都不影响其它信息和界面。
// =====================================================================

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ModelXStudio.Core.Models;

namespace ModelXStudio.Core.Services;

/// <summary>
/// 系统环境检测：CPU / 内存 / 显卡与显存。
/// 显存优先用 nvidia-smi（NVIDIA 精确值），其余走 WMI Win32_VideoController（AdapterRAM 上限 4GB，超出记为未知）。
/// 全部在后台线程执行，任何一步失败都不影响其它信息。
/// </summary>
public sealed class SystemInfoService
{
    public SystemHardware Hardware { get; private set; } = new();

    public event Action<SystemHardware>? Changed;

    public async Task DetectAsync(CancellationToken ct = default)
    {
        try
        {
            var hw = await Task.Run(() => Detect(), ct);
            Hardware = hw;
            Changed?.Invoke(hw);
        }
        catch
        {
            // 检测失败时保持旧数据
        }
    }

    public SystemHardware Detect()
    {
        // 逻辑线程数只由 WMI 逐 CPU 累加；这里不能先填 Environment.ProcessorCount，
        // 否则会与 WMI 的值相加，单路 8 核 16 线程会被显示成「8 核 32 线程」
        var hw = new SystemHardware
        {
            RamGb = TotalRamGb()
        };

        if (OperatingSystem.IsWindows())
        {
            try { DetectWithWmi(hw); } catch { /* WMI 不可用则跳过 */ }
            try { DetectNvidiaSmi(hw); } catch { /* 无 nvidia-smi 则跳过 */ }
        }

        // WMI 不可用/无结果时兜底
        if (hw.LogicalCores <= 0) hw.LogicalCores = Environment.ProcessorCount;

        // 去掉虚拟机/基础显示适配器等噪音
        hw.Gpus.RemoveAll(g => g.Name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)
                            || g.Name.Contains("Microsoft Radio", StringComparison.OrdinalIgnoreCase));
        return hw;
    }

    private static void DetectWithWmi(SystemHardware hw)
    {
        if (!OperatingSystem.IsWindows()) return;

        using (var searcher = new System.Management.ManagementObjectSearcher(
            "SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor"))
        {
            foreach (var mo in searcher.Get())
            {
                if (hw.CpuName.Length == 0 && mo["Name"] != null)
                    hw.CpuName = mo["Name"].ToString()!.Trim();
                if (mo["NumberOfCores"] != null && int.TryParse(mo["NumberOfCores"].ToString(), out var pc))
                    hw.PhysicalCores += pc;
                if (mo["NumberOfLogicalProcessors"] != null && int.TryParse(mo["NumberOfLogicalProcessors"].ToString(), out var lc))
                    hw.LogicalCores += lc;
            }
        }

        using (var searcher = new System.Management.ManagementObjectSearcher(
            "SELECT Name, AdapterRAM FROM Win32_VideoController"))
        {
            foreach (var mo in searcher.Get())
            {
                var name = mo["Name"]?.ToString()?.Trim() ?? "";
                if (name.Length == 0) continue;

                double? vram = null;
                if (mo["AdapterRAM"] != null &&
                    double.TryParse(mo["AdapterRAM"].ToString(), out var bytes) && bytes > 0)
                {
                    var gb = bytes / (1024.0 * 1024 * 1024);
                    // WMI 32 位字段封顶 ~4GB：视为「4GB 以上但不确定」，交给 nvidia-smi 修正
                    vram = gb >= 3.9 ? null : gb;
                }

                var vendor = GpuInfo.GuessVendor(name);
                hw.Gpus.Add(new GpuInfo
                {
                    Name = name,
                    VramGb = vram,
                    Vendor = vendor,
                    IsDiscrete = GpuInfo.GuessDiscrete(name, vendor)
                });
            }
        }
    }

    /// <summary>用 nvidia-smi 精确读取 NVIDIA 显存；顺带补充 WMI 漏掉的卡。</summary>
    private static void DetectNvidiaSmi(SystemHardware hw)
    {
        if (!OperatingSystem.IsWindows()) return;

        var psi = new ProcessStartInfo
        {
            FileName = "nvidia-smi",
            Arguments = "--query-gpu=name,memory.total --format=csv,noheader,nounits",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return;

        //  stdout / stderr 必须同时异步读：只读一路时，另一路管道缓冲写满会把 nvidia-smi 卡死
        var outputTask = proc.StandardOutput.ReadToEndAsync();
        var errorTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(6000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
            return;
        }

        // 进程已退出，给读任务一点时间收尾；拿不到就当作没有显存信息，不阻塞整个检测
        var output = outputTask.Wait(2000) ? outputTask.Result : "";
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var parts = line.Split(',');
            if (parts.Length < 2) continue;

            var name = parts[0].Trim();
            if (!double.TryParse(parts[1].Trim(), out var mib) || mib <= 0) continue;
            var vramGb = mib / 1024.0;

            var existing = hw.Gpus.FirstOrDefault(g =>
                g.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(g.Name, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.VramGb = vramGb;
                existing.IsDiscrete = true;
            }
            else
            {
                hw.Gpus.Add(new GpuInfo
                {
                    Name = name,
                    VramGb = vramGb,
                    Vendor = GpuVendor.Nvidia,
                    IsDiscrete = true
                });
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public static double TotalRamGb()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return 0;
            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            return GlobalMemoryStatusEx(ref status) ? status.ullTotalPhys / (1024.0 * 1024 * 1024) : 0;
        }
        catch
        {
            return 0;
        }
    }
}
