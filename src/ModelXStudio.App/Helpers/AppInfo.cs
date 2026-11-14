// =====================================================================
// 作者：zhenhua
// 功能：应用级元信息集中定义（名称、作者、版本号、界面署名文案、默认系统提示词）。
//       版本号从程序集读取，避免 XAML / csproj / README 三处各写一份版本对不上。
// =====================================================================

using System.Reflection;

namespace ModelXStudio.App;

/// <summary>应用名称、作者与版本信息（界面与提示文案统一取这里）。</summary>
public static class AppInfo
{
    /// <summary>应用名。</summary>
    public const string Name = "ModelX Studio";

    /// <summary>作者署名。</summary>
    public const string Author = "zhenhua";

    /// <summary>窗口标题。</summary>
    public const string WindowTitle = "ModelX Studio · 本地大模型工具";

    /// <summary>一句话定位（左侧导航副标题）。</summary>
    public const string Tagline = "本地大模型 · 简单易用";

    /// <summary>
    /// 对话默认 system 提示词。集中在这里，想改回答风格只需改一处。
    /// </summary>
    public const string DefaultSystemPrompt = "你是一个乐于助人的 AI 助手，请使用用户提问的语言回答。";

    /// <summary>
    /// 版本号，取自 csproj 的 &lt;Version&gt;（InformationalVersion）；
    /// 拿不到时回退到 AssemblyName 版本，保证界面永远不会显示空版本。
    /// </summary>
    public static string Version { get; } = ReadVersion();

    /// <summary>左下角第一行：版本 + 隐私承诺（与作者分两行写，侧栏宽 230 会断词）。</summary>
    public static string CreditLine => $"v{Version} · 数据全部留在本机";

    /// <summary>左下角第二行：作者署名。</summary>
    public static string AuthorLine => $"作者 {Author}";

    private static string ReadVersion()
    {
        var asm = typeof(AppInfo).Assembly;

        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // InformationalVersion 可能形如 0.1.0+abcdef，取 + 之前的部分
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }

        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
