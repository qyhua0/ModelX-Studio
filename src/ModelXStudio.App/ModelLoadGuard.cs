// =====================================================================
// 作者：zhenhua
// 功能：加载模型前的那道「问一句」：下拉里选一下不该直接吃掉几个 G 内存，
//       内存不够时也要在加载之前就说清楚，而不是等引擎崩了再猜。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// =====================================================================

using System.IO;
using System.Text;
using ModelXStudio.Core.Models;
using ModelXStudio.Core.Services;

namespace ModelXStudio.App;

/// <summary>加载前的询问力度。</summary>
public enum ModelLoadAsking
{
    /// <summary>不问（开机自动加载那种：弹窗会让人以为程序卡住了）；但内存明显不够还是不加载。</summary>
    Silent,

    /// <summary>只在内存不够时弹窗（用户已经明确点了「加载」按钮）。</summary>
    PreflightOnly,

    /// <summary>总要问一句（下拉里选中一项 —— 选一下不等于要立刻加载进内存）。</summary>
    Ask,
}

/// <summary>
/// 一次「要不要加载」的判定结果。<see cref="ModelLoadGuard.Judge"/> 是纯函数：
/// 文件、内存、当前挂着哪个模型都由调用方给进来，验证脚本不用真加载模型就能断言文案和按钮。
/// </summary>
public sealed record ModelLoadPlan
{
    public required string Name { get; init; }
    public string? RunningName { get; init; }

    /// <summary>当前跑着的是另一个模型（加载会先停掉它）。</summary>
    public bool Switching { get; init; }

    /// <summary>内存判定的结论（false = 大概率装不下）。</summary>
    public bool Fits { get; init; }

    public long ModelMb { get; init; }
    public long NeedMb { get; init; }
    public long AvailMb { get; init; }

    /// <summary>停掉当前模型能还回来的内存（已经算进 <see cref="AvailMb"/>）。</summary>
    public long FreedMb { get; init; }

    public int ContextSize { get; init; }

    public required string Message { get; init; }
    public required string Title { get; init; }
    public required string OkText { get; init; }

    /// <summary>这个力度下要不要真弹窗拦一下。</summary>
    public bool NeedsDialog(ModelLoadAsking mode) => mode switch
    {
        // 自动加载：从来不弹窗（结果只看 <see cref="Fits"/>）
        ModelLoadAsking.Silent => false,
        // 用户已经明确点了「加载」，内存又够就别再拦一道
        ModelLoadAsking.PreflightOnly => !Fits,
        // 下拉里选一下：一律问一句
        _ => true,
    };
}

/// <summary>
/// 加载模型前的确认 + 内存预检。判定用的是 <see cref="ModelRecommender.Preflight"/>
/// （按「当下可用内存」算，不是总量：总共 16 GB 但浏览器占了 12 GB 才是真实处境）。
/// </summary>
public static class ModelLoadGuard
{
    /// <summary>返回 false 表示别加载（用户取消，或开机自动加载时判定装不下）。</summary>
    public static bool Confirm(string path, ModelLoadAsking mode)
    {
        var running = App.Engine.State.ModelPath;
        var ctx = App.Engine.LastContextArg > 0
            ? App.Engine.LastContextArg                                  // 引擎在跑：按它实际的 -c 算
            : App.SettingsService.Current.ContextSize;                   // 没跑：按设置值，和进度条同一口径
        var plan = Judge(path, mode, SystemInfoService.AvailableRamMb(), running, ctx);

        if (mode == ModelLoadAsking.Silent)
        {
            // 自动加载不弹窗，但也不硬上：判定装不下就不加载，把原因写进问题记录（下拉旁边也有提示）
            if (!plan.Fits) CrashReporter.Append($"跳过自动加载 {plan.Name}：{plan.Message.Replace("\n", " ")}");
            return plan.Fits;
        }

        if (!plan.NeedsDialog(mode)) return true;

        return ThemedDialog.Confirm(plan.Message, plan.Title, plan.OkText, "取消");
    }

    /// <summary>
    /// 纯判定：算内存 + 组弹窗文案。<paramref name="availableMb"/> 是「此刻可用」，
    /// 切换模型时 <paramref name="runningPath"/> 那份会被停掉释放，函数内部负责把这笔加进去。
    /// </summary>
    public static ModelLoadPlan Judge(string path, ModelLoadAsking mode,
        long availableMb, string? runningPath, int contextSize)
    {
        var name = Path.GetFileName(path);

        var switching = !string.IsNullOrWhiteSpace(runningPath) &&
                        !string.Equals(runningPath, path, StringComparison.OrdinalIgnoreCase);
        var runningName = switching ? Path.GetFileName(runningPath!) : null;
        var freedMb = switching ? SizeOfMb(runningPath!) : 0;

        var modelMb = SizeOfMb(path);
        var bytes = modelMb * 1024L * 1024;
        var avail = availableMb + freedMb;
        var needMb = (long)((bytes / GiB + ModelRecommender.EstimateKvCacheGb(bytes, contextSize)) * 1024);

        var (ok, preflight) = ModelRecommender.Preflight(bytes, contextSize, avail);

        var sb = new StringBuilder();
        if (mode == ModelLoadAsking.Ask)
            sb.Append(switching ? $"要换成「{name}」吗？\n" : $"要加载「{name}」吗？\n");
        else if (mode == ModelLoadAsking.PreflightOnly)
            sb.Append($"「{name}」现在加载有点悬：\n");

        sb.Append(bytes > 0
            ? preflight
            : "读不到模型文件的大小（文件可能刚被移走），没法预估内存。");

        if (switching && runningName != null)
        {
            sb.Append($"\n\n当前挂着的是「{runningName}」，加载新模型会先停掉它");
            sb.Append(freedMb > 0
                ? $"，约 {freedMb / 1024.0:0.#} GB 会还回来（上面的可用内存已经把这笔算进去了）。"
                : "。");
        }

        if (!ok)
            sb.Append("\n\n真要试也可以 —— 失败了引擎会退出，程序会把原因写在问题记录里，不会损坏文件。");

        return new ModelLoadPlan
        {
            Name = name,
            RunningName = runningName,
            Switching = switching,
            Fits = ok,
            ModelMb = modelMb,
            NeedMb = needMb,
            AvailMb = avail,
            FreedMb = freedMb,
            ContextSize = contextSize,
            Message = sb.ToString(),
            Title = ok ? "加载模型" : "内存可能不够",
            OkText = ok ? (switching ? "停掉它，加载" : "加载") : "仍然尝试加载",
        };
    }

    private const double GiB = 1024.0 * 1024 * 1024;

    private static long SizeOfMb(string p)
    {
        try { return new FileInfo(p).Length / (1024 * 1024); }
        catch { return 0; }
    }
}
