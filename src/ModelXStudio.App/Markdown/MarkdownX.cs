// ====================================================================
// 作者：zhenhua
// 功能：Markdown 渲染核心。Markdig 0.22 的 AST → WPF FlowDocument（聊天气泡里显示模型回复），
//       含代码块卡片（语言标签 + 复制按钮 + 分色）、管道表、任意层嵌套列表、引用块、任务列表。
//       由 Controls/MarkdownView.cs 包成控件，XAML 直接写 <ctrl:MarkdownView Markdown="{Binding …}"/>。
// 设计取舍：
//   · 只用 Markdig 0.22 有的 API（仓库离线用户多，不能假设能升级）：管道只开 PipeTables / EmphasisExtras /
//     AutoLinks / TaskLists / SoftlineBreakAsHardlineBreak，其余全部自己遍历 AST 画。不引 Markdig.Wpf：
//     它还要另装 XamlMath，而且画出来是「文档」风格（大留白、 serif），塞进气泡里很吵。
//   · 为什么不生成 HTML 再喂 RichTextBox：RichTextBox.Document 只吃 FlowDocument，走 HTML 要么引第三方
//     要么自己写一套解析器 —— AST 已经在手边，直接映射最省一层。
//   · 整条链路的原则是「绝不让某条消息显示不出来」：Build 内任何异常都吃掉并降级成原文纯文本；
//     流式时喂进来的永远是残缺 markdown（半截围栏、半截表格、半截链接），所以每种块都要能接受结构不完整。
//   · FlowDocument 的几个坑（都在这份代码里绕开了，改之前先读）：
//       - FlowDocument / Paragraph 没有 TextWrapping（WPF 就没提供），换行靠宿主 RichTextBox 关横向滚动条；
//       - Inline / Span 没有 Padding、Margin：行内代码只能靠底色，不做假内边距（拿不间断空格撑会污染复制）；
//       - 隐式 Style（<Style TargetType>）对 FlowDocument 内容不生效，颜色和字号一律显式设在文档/元素上；
//       - 连续半角空格会被排版折叠，代码缩进必须换成 U+00A0 才看得见 → 见 Visible()；
//       - 一段里做不到「左边一句右边一个按钮」，代码块头用 0 间距两列表格实现。
//   · 代码块不换行做不到（FlowDocument 没有横向滚动），这里就是折行：宁可折行也不要横向滚动条，
//     气泡在 ListBox 里出现横滚会把整个列表带歪。
//   · 图片一律不加载（离线、隐私 —— URL 里常带 token、解码崩溃，三个理由），只画「[图片] alt」+ 灰字 URL。
//   · 链接只认 http/https/mailto；javascript: / data: / file: 不生成 Hyperlink，按普通文字渲染。
//   · 原始 HTML 只丢标签保留文字（把 HTML 当富文本执行等于把 XSS 面开在聊天气泡里），<br> 当换行。
//   · 颜色/字体走主题资源 + 硬编码兜底（MdBrush / MdFont），单元测试环境没有 Application 也不会炸。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// ====================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
// WPF 和 Markdig 都有 Block / Inline：这里给 WPF 的那两个起别名，Markdig 的全部写全名。
// 不这么写的话文件里几十个 Block/Inline 全要写全名，读起来很糟。
using Block = System.Windows.Documents.Block;
using Inline = System.Windows.Documents.Inline;
// Markdig 的 Table / TableRow 跟 WPF 的 System.Windows.Documents.Table / TableRow 同名，必须起别名。
// Markdig 的 TableCell 不是公开类型，正好我们只要它里面的子块 → 统一按 ContainerBlock 处理。
using MdTable = Markdig.Extensions.Tables.Table;
using MdRow = Markdig.Extensions.Tables.TableRow;
using MdAlign = Markdig.Extensions.Tables.TableColumnAlign;

namespace ModelXStudio.App.Markdown;

/// <summary>
/// 渲染用画刷：先查应用主题资源（Themes/Dark.xaml、Light.xaml 里同名 key），查不到用硬编码兜底。
/// 全部 Freeze + 静态缓存：这些刷子会被成千上万个 Run 共享，只在 UI 线程访问，不冻结等于白付
/// 可继承属性的状态检查开销；缓存也避免每次 Build 都 new 一批 SolidColorBrush。
/// </summary>
internal static class MdBrush
{
    // 主题 key
    public const string Text = "MdText";
    public const string Heading = "MdHeading";
    public const string Dim = "MdDim";
    public const string Faint = "MdFaint";
    public const string Link = "MdLink";
    public const string CodeBg = "MdCodeBg";
    public const string CodeFg = "MdCodeFg";
    public const string CodeBorder = "MdCodeBorder";
    public const string Quote = "MdQuote";
    public const string Rule = "MdRule";
    public const string Keyword = "MdKeyword";
    public const string String_ = "MdString";
    public const string Comment = "MdComment";
    public const string Number = "MdNumber";
    public const string Type = "MdType";
    public const string Func = "MdFunc";
    public const string Prep = "MdPrep";

    // 兜底值 = 设计稿深色主题。没有 Application（纯计算 / 单测）或主题还没加载时用。
    private static readonly Dictionary<string, string> Defaults = new(StringComparer.Ordinal)
    {
        [Text] = "#E2E5EA",
        [Heading] = "#F0F3F7",
        [Dim] = "#A0A8B4",
        [Faint] = "#6E7784",
        [Link] = "#86A5F5",
        [CodeBg] = "#10131A",
        [CodeFg] = "#D7DCE4",
        [CodeBorder] = "#2C323C",
        [Quote] = "#3A424F",
        [Rule] = "#2C323C",
        [Keyword] = "#8FB8FF",
        [String_] = "#9CD5A4",
        [Comment] = "#7A8391",
        [Number] = "#E0B26A",
        [Type] = "#6FD0C8",
        [Func] = "#C6A8F2",
        [Prep] = "#D9A05B",
    };

    private static readonly Dictionary<string, Brush> Cache = new(StringComparer.Ordinal);

    /// <summary>取画刷（已冻结）。key 见上面的常量。</summary>
    public static Brush Get(string key)
    {
        if (Cache.TryGetValue(key, out var cached)) return cached;

        Brush b;
        try
        {
            // 资源可能是主题里的 DynamicResource 画刷，克隆一份再冻结，避免绑到别人的继承上下文上。
            var found = Application.Current?.TryFindResource(key);
            b = found is Brush fb ? fb.CloneCurrentValue() : Parse(key);
        }
        catch
        {
            b = Parse(key);
        }

        b.Freeze();
        Cache[key] = b;
        return b;
    }

    private static Brush Parse(string key)
        => (SolidColorBrush)new BrushConverter().ConvertFromString(Defaults[key])!;

    /// <summary>代码 token → 画刷。一张表放这儿，省得主题色和类别的对应关系散落两处。</summary>
    public static Brush ForToken(CodeTokenKind kind) => kind switch
    {
        CodeTokenKind.Keyword => Get(Keyword),
        CodeTokenKind.Type => Get(Type),
        CodeTokenKind.String => Get(String_),
        CodeTokenKind.Comment => Get(Comment),
        CodeTokenKind.Number => Get(Number),
        CodeTokenKind.Func => Get(Func),
        CodeTokenKind.Prep => Get(Prep),
        CodeTokenKind.Punct => Get(Faint),
        _ => Get(CodeFg),
    };
}

/// <summary>渲染用字体：同 MdBrush，主题 key + 兜底。</summary>
internal static class MdFont
{
    public static readonly FontFamily Ui = Resolve("FontUi", "Microsoft YaHei UI, Segoe UI");
    public static readonly FontFamily Mono = Resolve("FontMono", "Consolas, Courier New");

    private static FontFamily Resolve(string key, string fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is FontFamily ff) return ff;
        }
        catch { /* 主题没加载就用兜底，不影响渲染 */ }
        return new FontFamily(fallback);
    }
}

/// <summary>Markdown → FlowDocument。对外只有 Build，具体活交给一次性的 Render 对象（带字号/深度等状态）。</summary>
public static class MarkdownBuilder
{
    /// <summary>
    /// 把 markdown 编译成 FlowDocument。**任何内部异常都吃掉**，降级为「原文纯文本段落」。
    /// baseFontSize 用于正文（标题/代码/间距按它等比缩放）；highlightCode=false 时代码块只给等宽底色不分色。
    /// </summary>
    public static FlowDocument Build(string? markdown, double baseFontSize = 13.5, bool highlightCode = true, bool showCopyButtons = true)
    {
        var doc = NewDocument(baseFontSize);
        if (string.IsNullOrWhiteSpace(markdown)) return doc;   // 空 / 纯空白 → 空文档，不留空行占位

        try
        {
            var parsed = global::Markdig.Markdown.Parse(markdown, Pipeline());
            new Render(doc, baseFontSize, highlightCode, showCopyButtons).RenderContainer(parsed, doc.Blocks);
        }
        catch
        {
            // 半截 markdown 让 Markdig 抛了（真实场景很少，但聊天不能出现「这条消息显示不出来」）
            Fallback(doc, markdown, baseFontSize);
        }
        return doc;
    }

    private static FlowDocument NewDocument(double fontSize) => new()
    {
        FontFamily = MdFont.Ui,
        FontSize = fontSize,
        Foreground = MdBrush.Get(MdBrush.Text),
        PagePadding = new Thickness(0),       // 不要 Word 那套页面留白，控件高度才等于内容高度
        TextAlignment = TextAlignment.Left,
        LineHeight = Round(fontSize * 1.5),
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
    };

    private static void Fallback(FlowDocument doc, string markdown, double fontSize)
    {
        doc.Blocks.Clear();
        var p = new Paragraph
        {
            FontFamily = MdFont.Mono,
            FontSize = Math.Max(10.5, fontSize - 1),
            Foreground = MdBrush.Get(MdBrush.CodeFg),
            Background = MdBrush.Get(MdBrush.CodeBg),
            LineHeight = Round(fontSize * 1.4),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Margin = new Thickness(0),
        };
        // 降级也得保住缩进和换行，否则贴过来的代码更难读
        foreach (var line in markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
        {
            p.Inlines.Add(new Run(Visible(line)));
            p.Inlines.Add(new LineBreak());
        }
        doc.Blocks.Add(p);
    }

    private static Markdig.MarkdownPipeline? _pipeline;

    /// <summary>管道建一次就够（Markdig 的 pipeline 建好即不可变、可复用）。没关 HTML：HTML 要保文字只丢标签。</summary>
    private static Markdig.MarkdownPipeline Pipeline()
    {
        if (_pipeline == null)
        {
            _pipeline = new Markdig.MarkdownPipelineBuilder()
                .UsePipeTables()
                .UseEmphasisExtras()                 // ~~删除~~、==高亮==（后者按普通文字处理）
                .UseAutoLinks()
                .UseTaskLists()
                .UseSoftlineBreakAsHardlineBreak()   // 聊天里单个换行就是换行，模型不太守 CommonMark
                .Build();
        }
        return _pipeline;
    }

    private static double Round(double v) => Math.Round(v, 1);

    /// <summary>
    /// 行首空白换成 U+00A0：FlowDocument 会折叠连续半角空格，代码缩进不换掉就全贴左边。
    /// 只换「行首」那一段，行中的空格照旧 —— 这样从文档里选区复制出来的仍是真空格；
    /// 代码块卡片上的复制按钮用的是原始字符串，跟文档排版完全无关。
    /// </summary>
    private static string Visible(string t)
    {
        int i = 0;
        while (i < t.Length && (t[i] == ' ' || t[i] == '\t')) i++;
        if (i == 0) return t;

        var sb = new StringBuilder(t.Length + i);
        for (int k = 0; k < i; k++) sb.Append(t[k] == '\t' ? "\u00A0\u00A0\u00A0\u00A0" : "\u00A0");
        sb.Append(t, i, t.Length - i);
        return sb.ToString();
    }

    // ============================ 内部渲染器 ============================

    private sealed class Render
    {
        private readonly double _base;        // 正文字号
        private readonly double _code;        // 代码字号 = base - 1
        private readonly double _inlineCode;  // 行内码 = base - 0.5
        private readonly double _scale;       // 相对 13.5 的等比系数（间距跟着字号走）
        private readonly bool _highlight;
        private readonly bool _copyButton;

        // 列对齐（管道表分隔行的 :---: / ---: / :---）要到渲染单元格时才用得上，先按列号存着
        private MdAlign?[] _align = Array.Empty<MdAlign?>();

        // 复制按钮模板全局建一次；链接 hover 下划线同理
        private static ControlTemplate? _btnTpl;
        private static Style? _linkStyle;

        public Render(FlowDocument doc, double baseSize, bool highlight, bool copyButton)
        {
            _base = baseSize;
            _code = Math.Max(10.5, baseSize - 1);
            _inlineCode = Math.Max(10.5, baseSize - 0.5);
            _scale = baseSize / 13.5;
            _highlight = highlight;
            _copyButton = copyButton;
        }

        // ---------------------- 块 ----------------------

        public void RenderContainer(ContainerBlock container, BlockCollection target)
        {
            foreach (var b in container) Block(b, target, 0);
        }

        private void Block(Markdig.Syntax.Block b, BlockCollection target, int depth)
        {
            if (depth > 8) return;    // 恶意/畸形嵌套（复制一万行 "&gt;"）别把排版拖死

            switch (b)
            {
                case HeadingBlock h:
                    Heading(h, target);
                    break;

                case MdTable tbl:                      // 必须在通用 ContainerBlock 之前判
                    Table(tbl, target);
                    break;

                case ListBlock lb:
                    List(lb, target, depth);
                    break;

                case QuoteBlock q:
                    Quote(q, target, depth);
                    break;

                case FencedCodeBlock f:                // 顺序要紧：FencedCodeBlock : CodeBlock
                    Code(f.Info, LinesOf(f), target);
                    break;

                case CodeBlock cb:                     // 4 空格 / Tab 缩进块
                    Code(null, LinesOf(cb), target);
                    break;

                case ThematicBreakBlock:
                    Rule(target);
                    break;

                case HtmlBlock hb:
                    Html(LinesOf(hb), target);
                    break;

                case ParagraphBlock p:
                    if (p.Inline != null) ParaOf(p.Inline, target, depth);
                    break;

                case LeafBlock leaf:                   // 兜底：未知叶子块，有点内容就画成段落
                    if (leaf.Inline != null) ParaOf(leaf.Inline, target, depth);
                    break;

                case ContainerBlock inner:             // 兜底：未知容器块直接展开
                    foreach (var x in inner) Block(x, target, depth + 1);
                    break;

                default:
                    break;                              // BlankLineBlock 等：不占位
            }
        }

        /// <summary>
        /// 块的原始文本。StringLineGroup.ToString() 用 \n 连接各行；未闭合围栏同样给出已到达的内容，
        /// 所以流式打到一半的 ``` 也能当场画成代码卡片。
        /// </summary>
        private static string LinesOf(LeafBlock block)
        {
            try { return block.Lines.ToString() ?? string.Empty; }
            catch { return string.Empty; }
        }

        private Paragraph NewPara(double top, double bottom, double? lineHeight = null)
        {
            // 首块不要上间距：气泡顶上多出 8px 空隙很显眼
            var p = new Paragraph
            {
                Margin = new Thickness(0, top * _scale, 0, bottom * _scale),
                LineHeight = Round(lineHeight ?? _base * 1.5),
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            };
            return p;
        }

        /// <summary>由调用方在加进 target 之后调一次，首块的上间距归零。</summary>
        private void FixFirstGap(BlockCollection target, Block added)
        {
            if (target.Count == 1) added.Margin = new Thickness(added.Margin.Left, 0, added.Margin.Right, added.Margin.Bottom);
        }

        private void ParaOf(ContainerInline inline, BlockCollection target, int depth)
        {
            var p = NewPara(0, 5);
            Inlines(inline, p.Inlines);
            target.Add(p);
            FixFirstGap(target, p);
        }

        private void Heading(HeadingBlock h, BlockCollection target)
        {
            // 18/16.5/15/13.5/13.5/13.5，按 baseFontSize 等比缩放；上间距 > 下间距（靠上文的分组感）
            double[] sizes = { 18, 16.5, 15, 13.5, 13.5, 13.5 };
            double[] tops = { 14, 12, 10, 8, 8, 8 };
            double[] bottoms = { 6, 6, 4, 3, 3, 3 };
            int i = Math.Clamp(h.Level, 1, 6) - 1;
            double size = sizes[i] * _scale;

            var p = NewPara(tops[i], bottoms[i], size * 1.25);
            p.FontSize = size;
            p.FontWeight = FontWeights.Bold;
            p.Foreground = MdBrush.Get(MdBrush.Heading);
            if (h.Level <= 2)
            {
                p.BorderThickness = new Thickness(0, 0, 0, 1);       // h1/h2 底下一条 1px 细线
                p.BorderBrush = MdBrush.Get(MdBrush.Rule);
                p.Padding = new Thickness(0, 0, 0, 4);
            }
            if (h.Inline != null) Inlines(h.Inline, p.Inlines);
            target.Add(p);
            FixFirstGap(target, p);
        }

        private void Rule(BlockCollection target)
        {
            var p = new Paragraph
            {
                Margin = new Thickness(0, 8, 0, 8),
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = MdBrush.Get(MdBrush.Rule),
                LineHeight = 2,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            };
            target.Add(p);
        }

        private void Quote(QuoteBlock q, BlockCollection target, int depth)
        {
            // Section 是 FlowDocument 里唯一「能装块 + 有 Border/Padding」的容器，正好当引用竖条。
            // 嵌套引用就是嵌套 Section，前景色靠继承变 Dim，里面的代码/列表照常渲染。
            var sec = new Section
            {
                Margin = new Thickness(0, 4 * _scale, 0, 6 * _scale),
                Padding = new Thickness(9, 1, 0, 1),
                BorderThickness = new Thickness(2, 0, 0, 0),
                BorderBrush = MdBrush.Get(MdBrush.Quote),
                Foreground = MdBrush.Get(MdBrush.Dim),
            };
            foreach (var child in q) Block(child, sec.Blocks, depth + 1);
            if (sec.Blocks.Count > 0) target.Add(sec);
        }

        // ---------------------- 列表 ----------------------

        private void List(ListBlock lb, BlockCollection target, int depth)
        {
            int n = ParseStart(lb.OrderedStart);
            char delim = lb.OrderedDelimiter == ')' ? ')' : '.';

            foreach (var child in lb)
            {
                if (child is not ListItemBlock li) continue;
                string marker;
                if (lb.IsOrdered)
                {
                    // 用 Markdig 的 Order：嵌套列表各自从 1 数，起始号（3. 开头）也照它
                    int num = li.Order > 0 ? li.Order : n;
                    marker = num.ToString(CultureInfo.InvariantCulture) + delim;
                }
                else marker = Bullet(depth);
                n++;
                ListItem(li, target, depth, marker);
            }
        }

        private static int ParseStart(string? s)
            => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 1;

        private static string Bullet(int depth) => (depth % 3) switch { 0 => "•", 1 => "◦", _ => "▪" };

        /// <summary>
        /// 列表项首行用悬挂缩进（负 TextIndent），不用表格：表格能让折行严格对齐，但每个列表项一张表，
        /// 流式每秒重排一次的话这个开销太离谱。悬挂缩进的代价是「项里嵌代码块时左边线差几像素」。
        /// 首行起点 = Margin.Left + TextIndent = 子弹位；折行/硬换行起点 = Margin.Left = 文字位。
        /// </summary>
        private void ListItem(ListItemBlock li, BlockCollection target, int depth, string marker)
        {
            double hang = Round(_base * 1.35);
            double indent = depth == 0 ? 0 : Round(_base * 1.15);

            var head = new Paragraph
            {
                TextIndent = -hang,
                Margin = new Thickness(indent + hang, 0, 0, 0),
                LineHeight = Round(_base * 1.45),
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            };

            // 任务列表：TaskList 内联是该项首段的第一个 inline，取出来当标记，剩下的照常渲染
            var firstPara = First<ParagraphBlock>(li);
            var task = firstPara?.Inline?.FirstChild as Markdig.Extensions.TaskLists.TaskList;
            string glyph = marker;
            var glyphBrush = MdBrush.Get(MdBrush.Dim);
            if (task != null)
            {
                glyph = task.Checked ? "☑" : "☐";
                glyphBrush = task.Checked ? MdBrush.Get(MdBrush.Text) : MdBrush.Get(MdBrush.Dim);   // 未完成的淡化一点
            }
            head.Inlines.Add(new Run(glyph + " ") { Foreground = glyphBrush });

            var rest = new List<Markdig.Syntax.Block>();
            bool headFilled = false;
            foreach (var c in li)
            {
                if (!headFilled && ReferenceEquals(c, firstPara) && firstPara!.Inline != null)
                {
                    // 任务框那个 inline 已经画掉了，从它的下一个开始
                    var start = task != null ? firstPara.Inline.FirstChild?.NextSibling : firstPara.Inline.FirstChild;
                    InlinesFrom(start, head.Inlines);
                    headFilled = true;
                    continue;
                }
                rest.Add(c);
            }

            target.Add(head);

            int from = target.Count;
            foreach (var c in rest) Block(c, target, depth + 1);   // 嵌套列表因此自动降一层

            // 项内其余块整体右移一层，视觉上属于同一项；项间距极小（气泡里挤一点更好读）。
            // BlockCollection 只给 FirstBlock/LastBlock，没有索引器，所以边枚举边数。
            double gap = 2 * _scale;
            Block? last = null;
            int idx = 0;
            foreach (Block blk in target)
            {
                if (idx >= from)
                {
                    blk.Margin = new Thickness(blk.Margin.Left + indent, blk.Margin.Top, blk.Margin.Right, blk.Margin.Bottom);
                    last = blk;
                }
                idx++;
            }
            if (last != null)
            {
                gap = 4 * _scale;
                last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, gap);
            }
            else head.Margin = new Thickness(head.Margin.Left, head.Margin.Top, head.Margin.Right, gap);
        }

        private static T? First<T>(ContainerBlock container) where T : Markdig.Syntax.Block
        {
            foreach (var b in container) if (b is T t) return t;
            return null;
        }

        // ---------------------- 代码块 ----------------------

        private void Code(string? info, string code, BlockCollection target)
        {
            code = code.Replace("\r\n", "\n").Replace("\r", "\n");
            var lang = CodeHighlighter.NormalizeLang(info);

            // 卡片：Section 出左右边和上边，最后一行代码补下边线 → 视觉闭合（首行自带下边线）
            var card = new Section
            {
                Margin = new Thickness(0, 4 * _scale, 0, 6 * _scale),
                BorderThickness = new Thickness(1, 1, 1, 0),
                BorderBrush = MdBrush.Get(MdBrush.CodeBorder),
                FontFamily = MdFont.Mono,
                FontSize = _code,
                Foreground = MdBrush.Get(MdBrush.CodeFg),
            };

            card.Blocks.Add(Header(lang, code));

            var lines = code.Split('\n');
            int count = lines.Length;
            if (count > 1 && lines[count - 1].Length == 0) count--;   // 结尾空行不占位

            for (int i = 0; i < count; i++)
            {
                bool last = i == count - 1;
                var p = new Paragraph
                {
                    Margin = new Thickness(0),
                    Padding = new Thickness(9, i == 0 ? 4 : 0, 9, last ? 6 : 0),
                    Background = MdBrush.Get(MdBrush.CodeBg),
                    BorderThickness = new Thickness(0, 0, 0, last ? 1 : 0),
                    BorderBrush = MdBrush.Get(MdBrush.CodeBorder),
                    LineHeight = Round(_code * 1.35),
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                };
                CodeLine(p, lines[i], lang);
                card.Blocks.Add(p);
            }

            target.Add(card);
        }

        private void CodeLine(Paragraph p, string line, string lang)
        {
            if (line.Length == 0) return;   // 空行：Paragraph 本身就占一行，不用塞内容

            // 缩进要换成 U+00A0（FlowDocument 会折叠普通空格），但只能按「整行」算一次。
            // 早先每个 token 各调一次 Visible，token 边界上的空格（"namespace" + " Demo"）
            // 也被换成 U+00A0：选区复制出去的代码夹一串隐形怪字符，粘进编辑器直接编译不过。
            int lead = 0;
            while (lead < line.Length && (line[lead] == ' ' || line[lead] == '\t')) lead++;
            if (lead > 0)
            {
                var pad = new StringBuilder(lead * 4);
                for (int k = 0; k < lead; k++) pad.Append(line[k] == '\t' ? "\u00A0\u00A0\u00A0\u00A0" : "\u00A0");
                p.Inlines.Add(new Run(pad.ToString()) { Foreground = MdBrush.Get(MdBrush.CodeFg) });
            }

            // 行首之外一律保持真空格：文档里选区复制出来的必须是能编译的字符
            var rest = line.Substring(lead);
            if (rest.Length == 0) return;

            if (!_highlight || lang.Length == 0)
            {
                p.Inlines.Add(new Run(rest) { Foreground = MdBrush.Get(MdBrush.CodeFg) });
                return;
            }

            foreach (var (text, kind) in CodeHighlighter.Tokenize(rest, lang))
            {
                if (text.Length == 0) continue;
                p.Inlines.Add(new Run(text) { Foreground = MdBrush.ForToken(kind) });
            }
        }

        /// <summary>代码卡片头：左语言名、右复制按钮 —— 用 0 间距两列表格实现（段内做不到左右分开对齐）。</summary>
        private Table Header(string lang, string code)
        {
            double headSize = Math.Max(10, _code - 1);
            var t = new Table
            {
                CellSpacing = 0,
                Margin = new Thickness(0),
                Padding = new Thickness(0),
                Background = MdBrush.Get(MdBrush.CodeBg),
                FontFamily = MdFont.Mono,
                FontSize = headSize,
            };
            t.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
            t.Columns.Add(new TableColumn { Width = GridLength.Auto });

            var left = new Paragraph(new Run(string.IsNullOrEmpty(lang) ? "text" : lang))
            {
                Margin = new Thickness(0),
                Foreground = MdBrush.Get(MdBrush.Faint),
                LineHeight = Round(headSize * 1.2),
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            };
            var right = new Paragraph
            {
                Margin = new Thickness(0),
                TextAlignment = TextAlignment.Right,
                LineHeight = Round(headSize * 1.2),
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            };
            if (_copyButton) right.Inlines.Add(new InlineUIContainer(CopyButton(code)) { BaselineAlignment = BaselineAlignment.Center });

            var row = new TableRow();
            row.Cells.Add(new TableCell(left) { Padding = new Thickness(9, 4, 0, 1), BorderThickness = new Thickness(0) });
            row.Cells.Add(new TableCell(right) { Padding = new Thickness(0, 4, 9, 1), BorderThickness = new Thickness(0) });

            var group = new TableRowGroup();
            group.Rows.Add(row);
            t.RowGroups.Add(group);
            return t;
        }

        private UIElement CopyButton(string code)
        {
            // 点它的是消息里的代码，不是聊天窗口本身：Focusable/IsTabStop 全关，别把输入框焦点抢走。
            var b = new Button
            {
                Content = "复制",
                Focusable = false,
                IsTabStop = false,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 0, 4, 0),
                FontFamily = MdFont.Ui,
                FontSize = Math.Max(10, _code - 1),
                Foreground = MdBrush.Get(MdBrush.Dim),
                Tag = code,        // 复制的是「这个代码块的原始字符串」，绝不从文档里抠（文档里的空格被动过）
                Template = ButtonTemplate(),
            };
            b.Click += OnCopyClick;
            return b;
        }

        private static void OnCopyClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b) return;
            var text = b.Tag as string ?? string.Empty;
            try
            {
                if (text.Length == 0) Clipboard.Clear(); else Clipboard.SetText(text);
            }
            catch { /* 剪贴板被别的进程占着（CLIPBRD_E_C_DATA_BUSY），吞掉，别把聊天搞崩 */ }

            b.Content = "已复制";
            b.Foreground = MdBrush.Get(MdBrush.Link);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            timer.Tick += (_, __) =>
            {
                timer.Stop();                       // 一次性的表跑完就停，不留在调度器上
                b.Content = "复制";
                b.Foreground = MdBrush.Get(MdBrush.Dim);
            };
            timer.Start();
        }

        private static ControlTemplate ButtonTemplate()
        {
            if (_btnTpl != null) return _btnTpl;

            // 默认按钮模板在深色主题下是一块灰板 + 系统白字，太吵；这里只要「小字 + 手型 + hover 变强调色」。
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(cp);

            var tpl = new ControlTemplate(typeof(Button)) { VisualTree = border };
            tpl.Triggers.Add(new Trigger
            {
                Property = UIElement.IsMouseOverProperty,
                Value = true,
                Setters = { new Setter(Control.ForegroundProperty, MdBrush.Get(MdBrush.Link)) },
            });
            tpl.Seal();
            _btnTpl = tpl;
            return tpl;
        }

        // ---------------------- 表格 ----------------------

        private void Table(MdTable md, BlockCollection target)
        {
            int cols = 0;
            foreach (var b in md) if (b is MdRow r && r.Count > cols) cols = r.Count;
            if (cols == 0) return;

            var t = new Table
            {
                CellSpacing = 0,
                Margin = new Thickness(0, 5 * _scale, 0, 7 * _scale),
                Padding = new Thickness(0),
                BorderBrush = MdBrush.Get(MdBrush.CodeBorder),
                BorderThickness = new Thickness(1),
                FontSize = _code,          // 单元格里字挤，比正文小一号
                LineHeight = Round(_code * 1.35),
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            };

            _align = new MdAlign?[cols];
            for (int i = 0; i < cols; i++)
            {
                // Markdig 的相对宽度：几何宽度（:--- / :---: / ---:）是字符数，没写就是 NaN → 平分
                double w = double.NaN;
                MdAlign? align = null;
                if (i < md.ColumnDefinitions.Count)
                {
                    var def = md.ColumnDefinitions[i];
                    w = def.Width;
                    align = def.Alignment;
                }
                t.Columns.Add(new TableColumn
                {
                    Width = !double.IsNaN(w) && w > 0.001
                        ? new GridLength(w, GridUnitType.Star)
                        : new GridLength(1, GridUnitType.Star),
                });
                _align[i] = align;
            }

            var group = new TableRowGroup();
            foreach (var b in md)
            {
                if (b is not MdRow row) continue;
                var wr = new TableRow();
                int ci = 0;
                foreach (var cb in row)
                {
                    if (cb is not ContainerBlock cell) continue;   // Markdig 的 TableCell 就是个容器
                    var tc = new TableCell
                    {
                        Padding = new Thickness(6, 3, 6, 3),
                        BorderBrush = MdBrush.Get(MdBrush.CodeBorder),
                        BorderThickness = new Thickness(1),
                    };
                    if (row.IsHeader)
                    {
                        tc.Background = MdBrush.Get(MdBrush.CodeBg);
                        tc.FontWeight = FontWeights.Bold;
                        tc.Foreground = MdBrush.Get(MdBrush.Heading);
                    }
                    CellContent(cell, tc.Blocks, ci);
                    wr.Cells.Add(tc);
                    ci++;
                }
                while (ci < cols)   // 补齐空列，别让列宽算歪
                {
                    wr.Cells.Add(new TableCell
                    {
                        Padding = new Thickness(6, 3, 6, 3),
                        BorderBrush = MdBrush.Get(MdBrush.CodeBorder),
                        BorderThickness = new Thickness(1),
                    });
                    ci++;
                }
                group.Rows.Add(wr);
            }
            t.RowGroups.Add(group);
            target.Add(t);
        }

        private void CellContent(ContainerBlock cell, BlockCollection target, int col)
        {
            var a = col < _align.Length ? _align[col] : null;
            var textAlign = a switch
            {
                MdAlign.Center => TextAlignment.Center,
                MdAlign.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            };

            foreach (var b in cell)
            {
                switch (b)
                {
                    case ParagraphBlock p when p.Inline != null:
                        var cp = new Paragraph
                        {
                            Margin = new Thickness(0),
                            TextAlignment = textAlign,
                            LineHeight = Round(_code * 1.35),
                            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                        };
                        Inlines(p.Inline, cp.Inlines);
                        target.Add(cp);
                        break;

                    case ListBlock lb:
                        List(lb, target, 0);
                        break;

                    case CodeBlock cb:
                        var mp = new Paragraph
                        {
                            Margin = new Thickness(0),
                            FontFamily = MdFont.Mono,
                            LineHeight = Round(_code * 1.3),
                            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                        };
                        foreach (var l in LinesOf(cb).Split('\n'))
                        {
                            mp.Inlines.Add(new Run(Visible(l)));
                            mp.Inlines.Add(new LineBreak());
                        }
                        target.Add(mp);
                        break;

                    default:
                        break;
                }
            }
            if (target.Count == 0) target.Add(new Paragraph { Margin = new Thickness(0) });   // 空单元格也要有高度
        }

        // ---------------------- 原始 HTML ----------------------

        private void Html(string html, BlockCollection target)
        {
            var text = StripHtml(html);
            if (text.Trim().Length == 0) return;

            var p = NewPara(0, 5);
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length > 0) p.Inlines.Add(new Run(lines[i]) { Foreground = MdBrush.Get(MdBrush.Dim) });
                if (i < lines.Length - 1) p.Inlines.Add(new LineBreak());
            }
            target.Add(p);
            FixFirstGap(target, p);
        }

        /// <summary>丢标签保文字：把 HTML 当富文本执行等于在聊天气泡里开 XSS 面，标签一个都不认。</summary>
        private static string StripHtml(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length;)
            {
                if (s[i] == '<')
                {
                    int gt = s.IndexOf('>', i);
                    if (gt < 0) { sb.Append(s, i, s.Length - i); break; }   // 半截标签：当文字吐出去
                    if (i + 4 <= s.Length && s[i + 1] == '!' && s[i + 2] == '-' && s[i + 3] == '-')   // s[i] 已是 '<'
                    {
                        // 注释整块丢掉：里面通常是不想给人看见的说明
                        var close = s.IndexOf("-->", i, StringComparison.Ordinal);
                        i = close < 0 ? s.Length : close + 3;
                        continue;
                    }
                    var tag = s.Substring(i + 1, gt - i - 1).Trim();
                    while (tag.Length > 0 && (tag[0] == '/' || tag[0] == '!')) tag = tag.Substring(1).TrimStart();
                    int cut = tag.Length;
                    for (int k = 0; k < tag.Length; k++)
                        if (!char.IsLetterOrDigit(tag[k]) && tag[k] != '-') { cut = k; break; }
                    var name = tag.Substring(0, cut).ToLowerInvariant();
                    if (name == "br" || IsBlockTag(name)) sb.Append('\n');
                    i = gt + 1;
                    continue;
                }
                if (s[i] == '&')
                {
                    int semi = s.IndexOf(';', i + 1);
                    if (semi > i && semi - i <= 8)
                    {
                        var dec = DecodeEntity(s.Substring(i + 1, semi - i - 1));
                        if (dec != null) { sb.Append(dec); i = semi + 1; continue; }
                    }
                }
                sb.Append(s[i]);
                i++;
            }
            return sb.ToString();
        }

        private static bool IsBlockTag(string n) => n switch
        {
            "p" or "div" or "li" or "tr" or "table" or "section" or "article" or "ul" or "ol"
            or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote" or "pre" or "hr"
            or "header" or "footer" or "nav" or "figure" => true,
            _ => false,
        };

        private static string? DecodeEntity(string e)
        {
            switch (e)
            {
                case "amp": return "&";
                case "lt": return "<";
                case "gt": return ">";
                case "quot": return "\"";
                case "apos": return "'";
                case "nbsp": return " ";
            }
            if (e.Length > 1 && e[0] == '#')
            {
                bool hex = e[1] == 'x' || e[1] == 'X';
                if (int.TryParse(e.Substring(hex ? 2 : 1),
                        hex ? NumberStyles.HexNumber : NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var cp) && cp > 0 && cp < 0x110000)
                {
                    try { return char.ConvertFromUtf32(cp); } catch { return null; }
                }
            }
            return null;
        }

        // ---------------------- 行内 ----------------------

        private void Inlines(ContainerInline? container, InlineCollection target)
            => InlinesFrom(container?.FirstChild, target);

        /// <summary>从指定 inline 开始渲染（列表项要跳过任务框那个 inline，所以单独拆一层）。</summary>
        private void InlinesFrom(Markdig.Syntax.Inlines.Inline? inline, InlineCollection target) => InlinesFrom(inline, target, 0);

        private void InlinesFrom(Markdig.Syntax.Inlines.Inline? inline, InlineCollection target, int depth)
        {
            if (depth > 24) return;   // 「**」复制一万次的保险：嵌套太深就只留文字，别把栈打爆

            for (var node = inline; node != null; node = node.NextSibling)
            {
                switch (node)
                {
                    case LiteralInline lit:
                        AddText(target, lit.Content.ToString());
                        break;

                    case LineBreakInline:
                        // 软换行已在管道里换成硬换行 → 到这儿的一律真空行（聊天里模型不规范换行很常见）
                        target.Add(new LineBreak());
                        break;

                    case CodeInline ci:
                        InlineCode(ci.Content, target);
                        break;

                    case EmphasisInline em:
                        Emphasis(em, target, depth);
                        break;

                    case LinkInline link:
                        Link(link, target, depth);
                        break;

                    case AutolinkInline auto:
                        AutoLink(auto, target);
                        break;

                    case HtmlEntityInline ent:
                        AddText(target, ent.Transcoded.ToString());
                        break;

                    case HtmlInline h:
                        // 其余标签直接丢（保文字），只有 <br> 有意义
                        if (IsBrTag(h.Tag)) target.Add(new LineBreak());
                        break;

                    case Markdig.Extensions.TaskLists.TaskList:
                        break;   // 已在列表项那里当 ☐/☑ 画掉了

                    case ContainerInline cont:
                        InlinesFrom(cont.FirstChild, target, depth + 1);
                        break;

                    default:
                        break;
                }
            }
        }

        private static bool IsBrTag(string? tag)
        {
            if (string.IsNullOrEmpty(tag)) return false;
            // tag 形如 "<br>" / "<br/>" / "< BR >"：尖括号也得剥掉。
            // 早先只去了空格和斜杠，"<br>" 永远不等于 "br"，结果 "第一行<br>第二行" 被粘成 "第一行第二行"。
            var sb = new StringBuilder(tag.Length);
            foreach (var c in tag) if (char.IsLetter(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString() == "br";
        }

        /// <summary>渲染中（正在往 Hyperlink 里塞子节点）——此时不能再扫邮箱，Hyperlink 套 Hyperlink 会抛。</summary>
        private bool _insideLink;

        private void AddText(InlineCollection target, string text)
        {
            if (text.Length == 0) return;
            // 绝大多数文本没有 @，走快路径，不给流式渲染加活
            if (_insideLink || text.IndexOf('@') < 0) { AddPlain(target, text); return; }

            int last = 0;
            foreach (var (start, len) in Emails(text))
            {
                if (start > last) AddPlain(target, text.Substring(last, start - last));
                AddMail(target, text.Substring(start, len));
                last = start + len;
            }
            if (last < text.Length) AddPlain(target, text.Substring(last));
        }

        private static void AddPlain(InlineCollection target, string text)
        {
            if (text.Length == 0) return;
            target.Add(new Run(text));   // 不设前景色：正文颜色靠文档 / 引用块 Section 继承
        }

        /// <summary>
        /// 在一段纯文本里找裸邮箱（模型经常直接甩个 a@b.com 就完事）。规则保守：
        /// 本地部分只认 [A-Za-z0-9._%+-]，域名必须有至少一个点、结尾标点不算进邮箱（"发我 a@b.com。" 不含句号）。
        /// </summary>
        private static IEnumerable<(int Start, int Len)> Emails(string s)
        {
            for (int i = 1; i < s.Length - 1; i++)
            {
                if (s[i] != '@') continue;

                int aStart = i;
                while (aStart > 0 && IsLocal(s[aStart - 1])) aStart--;
                if (aStart == i) continue;                                       // @ 前面没东西
                if (aStart >= 7 && string.Compare(s, aStart - 7, "mailto:", 0, 7, StringComparison.OrdinalIgnoreCase) == 0)
                    continue;                                                    // 已经写了 mailto: 的，不再动它

                int b = i + 1;
                while (b < s.Length && (char.IsAsciiLetterOrDigit(s[b]) || s[b] == '-' || s[b] == '.')) b++;
                while (b > i + 1 && s[b - 1] == '.') b--;                         // 句尾的点不算
                string dom = s.Substring(i + 1, b - i - 1);
                int dot = dom.LastIndexOf('.');
                if (dom.Length < 4 || dot <= 0 || dot == dom.Length - 1) continue;
                if (dom[0] == '-' || dom[dom.Length - 1] == '-' || dom.Contains("..")) continue;
                string tld = dom.Substring(dot + 1);
                if (tld.Length < 2) continue;
                bool tldOk = true;
                foreach (var c in tld) if (!char.IsAsciiLetter(c)) { tldOk = false; break; }
                if (!tldOk) continue;

                yield return (aStart, b - aStart);
                i = b - 1;
            }

            static bool IsLocal(char c) => char.IsAsciiLetterOrDigit(c) || c == '.' || c == '_' || c == '+' || c == '-';
        }

        private void AddMail(InlineCollection target, string mail)
        {
            var hl = new Hyperlink
            {
                Foreground = MdBrush.Get(MdBrush.Link),
                Style = LinkStyle(),
                NavigateUri = ToUri("mailto:" + mail),
            };
            hl.Inlines.Add(new Run(mail) { Foreground = MdBrush.Get(MdBrush.Link) });
            hl.Click += (_, __) => OpenUrl("mailto:" + mail);
            target.Add(hl);
        }

        /// <summary>
        /// 挂 NavigateUri 用：状态栏能看到真实目标、右键能「复制链接地址」，安全审计也只看这一个属性。
        /// 只接受绝对的 http/https/mailto，别的宁可不挂（点了没反应比点了出事好）。
        /// </summary>
        private static Uri? ToUri(string href)
        {
            if (!Uri.TryCreate(href, UriKind.Absolute, out var u)) return null;
            return u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeMailto
                ? u
                : null;
        }

        private void InlineCode(string text, InlineCollection target)
        {
            if (string.IsNullOrEmpty(text)) return;
            // Span 没有 Padding（WPF 没给），也不拿不间断空格冒充内边距 —— 那会污染选区复制出来的文本。
            target.Add(new Span(new Run(text))
            {
                FontFamily = MdFont.Mono,
                FontSize = _inlineCode,
                Background = MdBrush.Get(MdBrush.CodeBg),
                Foreground = MdBrush.Get(MdBrush.CodeFg),
            });
        }

        private void Emphasis(EmphasisInline em, InlineCollection target, int depth)
        {
            // 0.22 EmphasisExtras 的符号：~~ 删除、== 高亮、~ 下标、^ 上标（后三种 WPF 里没好对应，只留文字）
            if (em.DelimiterChar == '=' || (em.DelimiterChar == '~' && em.DelimiterCount != 2) || em.DelimiterChar == '^')
            {
                InlinesFrom(em.FirstChild, target, depth + 1);
                return;
            }

            Span span = em.DelimiterChar == '~'
                ? new Span { TextDecorations = TextDecorations.Strikethrough }
                : em.DelimiterCount == 2 ? new Bold() : new Italic();   // IsDouble 在 0.22 已标记过时

            // 中文斜体不好看，但「*斜*」总得有视觉区别 —— 保留 Italic，交给字体自己处理（雅黑是伪斜，够用）
            InlinesFrom(em.FirstChild, span.Inlines, depth + 1);
            target.Add(span);
        }

        private void Link(LinkInline link, InlineCollection target, int depth)
        {
            if (link.IsImage)
            {
                // 不加载图片：离线、隐私（URL 常带 token）、解码崩溃，三个理由够了。留 alt + 淡色 URL 让人自己判断。
                target.Add(new Run("[图片] ") { Foreground = MdBrush.Get(MdBrush.Dim) });
                var alt = PlainText(link.FirstChild);
                if (alt.Length > 0) target.Add(new Run(alt) { Foreground = MdBrush.Get(MdBrush.Dim) });
                if (!string.IsNullOrWhiteSpace(link.Url))
                {
                    target.Add(new Run(" " + Ellipsis(link.Url!, 60))
                    {
                        FontSize = Math.Max(10, _base - 2),
                        Foreground = MdBrush.Get(MdBrush.Faint),
                    });
                }
                return;
            }

            var text = PlainText(link.FirstChild);
            if (!IsSafeUrl(link.Url, out var href) || text.Length == 0)
            {
                // javascript:/data:/file: 或没有可读文字：不生成 Hyperlink，按普通文字渲染
                _insideLink = true;
                try { InlinesFrom(link.FirstChild, target, depth + 1); }
                finally { _insideLink = false; }
                if (text.Length == 0 && !string.IsNullOrWhiteSpace(link.Url)) AddText(target, link.Url!);
                return;
            }

            var hl = new Hyperlink
            {
                Foreground = MdBrush.Get(MdBrush.Link),
                Style = LinkStyle(),
                NavigateUri = ToUri(href),   // 只挂已过白名单的地址
            };
            var inner = new Span { Foreground = MdBrush.Get(MdBrush.Link) };
            _insideLink = true;
            try { InlinesFrom(link.FirstChild, inner.Inlines, depth + 1); }
            finally { _insideLink = false; }
            if (inner.Inlines.Count == 0) inner.Inlines.Add(new Run(href));
            hl.Inlines.Add(inner);
            hl.Click += (_, __) => OpenUrl(href);
            target.Add(hl);
        }

        private void AutoLink(AutolinkInline auto, InlineCollection target)
        {
            var url = auto.Url ?? string.Empty;
            // 邮箱的 AutolinkInline.Url 不带 mailto: 前缀，得自己补，否则点了打不开邮件客户端
            var href = auto.IsEmail && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? "mailto:" + url : url;
            if (!IsSafeUrl(href, out href) || url.Length == 0) { AddText(target, url); return; }

            var hl = new Hyperlink
            {
                Foreground = MdBrush.Get(MdBrush.Link),
                Style = LinkStyle(),
                NavigateUri = ToUri(href),
            };
            hl.Inlines.Add(new Run(url) { Foreground = MdBrush.Get(MdBrush.Link) });
            hl.Click += (_, __) => OpenUrl(href);
            target.Add(hl);
        }

        private static void OpenUrl(string href)
        {
            try
            {
                using var _ = Process.Start(new ProcessStartInfo(href) { UseShellExecute = true });
            }
            catch { /* 没装浏览器 / URI 非法：吞掉，点链接不该弹错误框 */ }
        }

        private static Style LinkStyle()
        {
            if (_linkStyle != null) return _linkStyle;
            // 默认 Hyperlink 一直带下划线，密集链接很吵；改成 hover 才下划线。
            // FlowDocument 里隐式 Style 不生效，所以显式挂到每个 Hyperlink 上。
            var s = new Style(typeof(Hyperlink));
            s.Setters.Add(new Setter(Inline.TextDecorationsProperty, null));
            var t = new Trigger { Property = Hyperlink.IsMouseOverProperty, Value = true };
            t.Setters.Add(new Setter(Inline.TextDecorationsProperty, TextDecorations.Underline));
            s.Triggers.Add(t);
            s.Seal();
            _linkStyle = s;
            return _linkStyle;
        }

        private static string PlainText(Markdig.Syntax.Inlines.Inline? node)
        {
            var sb = new StringBuilder();
            Flat(node, sb);
            return sb.ToString();
        }

        private static void Flat(Markdig.Syntax.Inlines.Inline? node, StringBuilder sb)
        {
            for (var n = node; n != null; n = n.NextSibling)
            {
                switch (n)
                {
                    case LiteralInline l: sb.Append(l.Content.ToString()); break;
                    case CodeInline c: sb.Append(c.Content); break;
                    case LineBreakInline: sb.Append(' '); break;
                    case HtmlEntityInline e: sb.Append(e.Transcoded.ToString()); break;
                    case ContainerInline c2: Flat(c2.FirstChild, sb); break;
                    default: break;
                }
            }
        }

        private static string Ellipsis(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

        /// <summary>链接安全闸：只放 http/https/mailto，其它 scheme（javascript: / data: / file:）一律当普通文字。</summary>
        private static bool IsSafeUrl(string? url, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(url)) return false;
            var u = url.Trim();
            if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || u.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                normalized = u;
                return true;
            }
            return false;   // 相对路径 / 锚点也算不安全：气泡里没有「当前页」，点了只会出问题或毫无意义
        }
    }
}
