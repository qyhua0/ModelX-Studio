// ====================================================================
// 作者：zhenhua
// 功能：聊天消息的 Markdown 显示控件。RichTextBox 的派生类，XAML 里直接写
//       <ctrl:MarkdownView Markdown="{Binding Content}" Streaming="{Binding IsRunning}"
//              FontFamily="{StaticResource FontUi}" FontSize="13.5"/>
//       渲染本身在 Markdown/MarkdownX.cs，这个文件只负责「在 ListBox 里当一条消息」的宿主行为。
// 设计取舍：
//   · 继承 RichTextBox 而不是 TextBlock/FlowDocumentScrollViewer：只有 RichTextBox 能同时给到
//     FlowDocument 排版 + 选区复制 + 内嵌交互元素（代码块上的复制按钮），后两者都不行。
//   · 两个滚动条一律 Disabled —— 这是整套方案的关键：RichTextBox 内部的 ScrollViewer 关掉之后，
//     测量高度 = 文档内容高度，控件就能在 ListBox / ScrollViewer 里自然撑开，不需要算高度、
//     不需要 hack 模板里的 PART_ContentHost。副作用是自己不能滚，长回复靠外层列表滚（鼠标滚轮透传解决）。
//   · 流式：Markdig 解析 + FlowDocument 重排不便宜，token 一个接一个地来会把 UI 线程压出掉帧，
//     所以 Streaming=true 时用 DispatcherTimer 合并（110ms 一次）；关掉流式时立刻补一次，最后一帧不丢。
//     文本一模一样就直接跳过 —— 定时器每 tick 都重排一遍同样的文档是纯浪费。
//   · 复制语义：从 FlowDocument 里抠出来的选区文本会有奇怪的空格/断行，而消息气泡外面本来就要「复制全文」，
//     所以无选区时直接把这条消息的原始 markdown 写进剪贴板；有选区时走 RichTextBox 默认行为（用户要的就是那段）。
//   · 右键菜单不自己搭：`Themes/Dark.xaml` 里有整套 ContextMenu / MenuItem 样式，
//     控件默认的「复制 / 全选」菜单已经是深色底，不需要再置 null 去遮丑。
//   · 颜色/字号透传：FlowDocument 里隐式 Style 不生效，所以字号字体必须显式写进文档；
//     文档前景色用主题 key（MdText）而不是控件 Foreground，控件 Foreground 留给宿主自己决定。
//   · 不订阅 RichTextBox.Document 的变更事件：所有更新都从 Markdown DP 进来，方向单一，不会出现自激。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// ====================================================================

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using ModelXStudio.App.Markdown;

namespace ModelXStudio.App.Controls;

/// <summary>只读的 Markdown 消息视图（聊天气泡里显示模型回复）。</summary>
public sealed class MarkdownView : RichTextBox
{
    /// <summary>流式重排的最小间隔（毫秒）。小于这个值的连续更新会被合并成一次。</summary>
    private const int StreamIntervalMs = 110;

    private DispatcherTimer? _timer;
    private string _last = string.Empty;   // 上次真正渲染的文本，用来跳过没变化的更新
    private string _lastKey = string.Empty; // 字号/字体也参与判断，改了字体得重排
    private bool _suppress;                 // 重建过程中改 DP 时不要把回调绕回来
    private bool _bubbling;                 // 滚轮透传的重入闸

    static MarkdownView()
    {
        // 字号/字体是透传进 FlowDocument 的（隐式样式在文档里不生效），改了必须重排；
        // Control 没有 OnFontSizeChanged 这种虚方法，只能用 OverrideMetadata 接变更。
        // 默认值照搬 Control 自己的，免得到时候改写了默认值没人知道。
        FontSizeProperty.OverrideMetadata(typeof(MarkdownView),
            new FrameworkPropertyMetadata(SystemFonts.MessageFontSize, OnFontMetricChanged));
        FontFamilyProperty.OverrideMetadata(typeof(MarkdownView),
            new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, OnFontMetricChanged));
    }

    private static void OnFontMetricChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var v = (MarkdownView)d;
        if (!v._suppress) v.Rebuild(force: true);
    }

    public MarkdownView()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true;           // 不加这个，Hyperlink 点不动、代码块里的按钮也不响应
        BorderThickness = new Thickness(0);
        Background = System.Windows.Media.Brushes.Transparent;
        Padding = new Thickness(0);
        IsTabStop = false;                  // Tab 应该在输入框之间跳，不该逐条消息跳
        Cursor = Cursors.IBeam;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;

        // 选区色跟主题走（Dark/Light 都有这个 key）；SetResourceReference 换主题时会自动跟上
        SetResourceReference(SelectionBrushProperty, "BrushSelection");

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnCopyExecuted, OnCopyCanExecute));

        // RichTextBox 自带的默认文档里有一个空 Paragraph、PagePadding 也不是 0；
        // 空消息的气泡会因此凭空高出一行。构造时就换成真正的空文档（Build(null) 就是 PagePadding=0 的空壳）。
        Document = MarkdownBuilder.Build(null, FontSize, HighlightCode);
        _last = string.Empty;
        _lastKey = Key();

        // FrameworkElement.OnUnloaded 不是虚方法，只能挂事件：虚拟化把气泡收走时必须停表，
        // 否则 DispatcherTimer 会一直握着这个控件，每次 tick 重排一份没人看的文档。
        Unloaded += (_, _) => StopTimer();

        // 滚轮要透给外层：TextBox 基类无条件把 MouseWheel 标记成已处理，而本控件自己两向滚动都是
        // Disabled（内容整篇铺开，不内滞）。不转发的话，鼠标只要停在回复气泡上就滚不动消息列表。
        AddHandler(PreviewMouseWheelEvent, new MouseWheelEventHandler(ForwardMouseWheel), true);
    }

    /// <summary>把滚轮原样交给父级（往上就是消息列表的 ScrollViewer）。</summary>
    private void ForwardMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        var up = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = this,
        };
        var target = (Parent as UIElement) ?? VisualTreeHelper.GetParent(this) as UIElement;
        target?.RaiseEvent(up);
    }

    // ============================ 依赖属性 ============================

    /// <summary>要显示的 markdown 原文。</summary>
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView),
        new PropertyMetadata(string.Empty, OnMarkdownChanged));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value ?? string.Empty);
    }

    /// <summary>false = 纯文本渲染（保留换行，不解析 markdown）。给用户消息/关闭渲染开关用。</summary>
    public static readonly DependencyProperty RenderMarkdownProperty = DependencyProperty.Register(
        nameof(RenderMarkdown), typeof(bool), typeof(MarkdownView),
        new PropertyMetadata(true, OnRenderOptionChanged));

    public bool RenderMarkdown
    {
        get => (bool)GetValue(RenderMarkdownProperty);
        set => SetValue(RenderMarkdownProperty, value);
    }

    /// <summary>代码块是否分色（关掉就只给等宽底色）。</summary>
    public static readonly DependencyProperty HighlightCodeProperty = DependencyProperty.Register(
        nameof(HighlightCode), typeof(bool), typeof(MarkdownView),
        new PropertyMetadata(true, OnRenderOptionChanged));

    public bool HighlightCode
    {
        get => (bool)GetValue(HighlightCodeProperty);
        set => SetValue(HighlightCodeProperty, value);
    }

    /// <summary>流式模式：更新合并到 110ms 一次，并在末尾画一个光标。由 true→false 会立刻补渲染一次。</summary>
    public static readonly DependencyProperty StreamingProperty = DependencyProperty.Register(
        nameof(Streaming), typeof(bool), typeof(MarkdownView),
        new PropertyMetadata(false, OnStreamingChanged));

    public bool Streaming
    {
        get => (bool)GetValue(StreamingProperty);
        set => SetValue(StreamingProperty, value);
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var v = (MarkdownView)d;
        if (v.Streaming) v.Schedule();      // 流式：攒一下再说
        else v.Rebuild(force: true);        // 一次性赋值：立刻显示，别等定时器
    }

    private static void OnRenderOptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MarkdownView)d).Rebuild(force: true);

    private static void OnStreamingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var v = (MarkdownView)d;
        if (v.Streaming) { v.Rebuild(force: true); return; }

        // 流式结束：停表并马上补一次，保证最后一帧文本一定落到文档里
        v.StopTimer();
        v.Rebuild(force: true);
    }

    // ============================ 渲染 ============================

    private void Schedule()
    {
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(StreamIntervalMs),
            };
            _timer.Tick += OnTick;
        }
        // 重排成本随文本长度涨（实测 2KB≈9ms、8KB≈32ms、20KB≈107ms）。
        // 长回答里按 110ms 硬刷会把 UI 线程吃满，宁可让它刷得慢一点，也别让窗口卡顿。
        int len = Markdown?.Length ?? 0;
        _timer.Interval = TimeSpan.FromMilliseconds(len > 16000 ? 400 : len > 8000 ? 260 : StreamIntervalMs);
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        // 文本没变就只是空转一次定时器；真变了才重排
        if (Changed()) Rebuild(force: false);
        if (!_dirty) StopTimer();   // 没有后续更新就把表停掉，不留着耗电
    }

    private bool _dirty;

    private bool Changed()
        => !string.Equals(Markdown ?? string.Empty, _last, StringComparison.Ordinal)
           || !string.Equals(Key(), _lastKey, StringComparison.Ordinal);

    private string Key() => FontSize.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                            + "|" + (FontFamily?.Source ?? string.Empty);

    private void StopTimer()
    {
        if (_timer == null) return;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _timer = null;
    }

    private void Rebuild(bool force)
    {
        var text = Markdown ?? string.Empty;
        if (!force && !Changed()) return;

        _suppress = true;
        try
        {
            Document = RenderMarkdown ? MarkdownBuilder.Build(text, FontSize, HighlightCode) : PlainDoc(text);
            if (Streaming) Document.Blocks.Add(CaretParagraph());
            _last = text;
            _lastKey = Key();
            _dirty = false;
        }
        catch
        {
            // Build 内部已经吞过一轮异常，这里再兜一层：控件出问题也不能把整个聊天面板带走
            try { Document = PlainDoc(text); _last = text; _lastKey = Key(); }
            catch { /* 连兜底都失败就算了，保持现有文档 */ }
        }
        finally
        {
            _suppress = false;
        }
        InvalidateMeasure();   // 内容换了，宿主（ListBox 项）得重新量一次高度
    }

    private FlowDocument PlainDoc(string text)
    {
        var doc = new FlowDocument
        {
            FontFamily = FontFamily,
            FontSize = FontSize,
            Foreground = MdBrush.Get(MdBrush.Text),
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
            LineHeight = Math.Round(FontSize * 1.5, 1),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        if (text.Trim().Length == 0) return doc;

        var p = new Paragraph { Margin = new Thickness(0) };
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length > 0) p.Inlines.Add(new Run(lines[i]));
            if (i < lines.Length - 1) p.Inlines.Add(new LineBreak());   // 纯文本路径只保留换行，别的什么都不解析
        }
        doc.Blocks.Add(p);
        return doc;
    }

    private Paragraph CaretParagraph()
    {
        // 流式光标：单独一段，跟在最后一个块后面；颜色用「最淡那档」，不跟正文抢注意力
        var p = new Paragraph(new Run("▍") { Foreground = MdBrush.Get(MdBrush.Faint) })
        {
            Margin = new Thickness(0),
            TextAlignment = TextAlignment.Left,
            LineHeight = Math.Round(FontSize * 1.2, 1),
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };
        return p;
    }

    // ============================ 交互 ============================

    /// <summary>
    /// 自己滚不动（内容高度 ≤ 可视高度，本来就是 Disabled）时，把滚轮事件原样抛给父链：
    /// 不然鼠标停在长回复上，外层 ListBox 就滚不动了。
    /// </summary>
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        if (_bubbling) return;              // 自己抛回来的那一次，直接放过，别循环
        if (!CanSelfScroll())
        {
            var src = e.Source as IInputElement ?? this;
            var pe = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = this,
            };
            _bubbling = true;
            try { src.RaiseEvent(pe); }
            finally { _bubbling = false; }
            e.Handled = true;               // 交出去了就别让基类再动一次
            return;
        }
        base.OnPreviewMouseWheel(e);
    }

    private bool CanSelfScroll()
    {
        // 构造函数的契约就是「自己不滚」：ScrollBarVisibility.Disabled 时 ExtentHeight 照样大于视口，
        // 只看 ExtentHeight 会误判成「我能滚」，于是滚轮被吞掉、外层列表卡住。必须先认这个开关。
        if (VerticalScrollBarVisibility == ScrollBarVisibility.Disabled) return false;
        try
        {
            return ExtentHeight > ViewportHeight + 1
                   && VerticalOffset + ViewportHeight < ExtentHeight - 1;
        }
        catch { return false; }             // 文档还没量过，按「不能滚」处理最安全
    }

    /// <summary>鼠标滚轮阶段同样不能自己消化（滚动条已 Disabled，这里只是防止基类把事件吃掉）。</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_bubbling) return;
        base.OnMouseWheel(e);
    }


    // ---------------- 复制：优先给原始 markdown ----------------

    private void OnCopyCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = true;    // 没选区也要能 Ctrl+C：这时候复制整条消息
    }

    private void OnCopyExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        var selected = Selection == null || Selection.IsEmpty ? string.Empty : Selection.Text;
        if (selected.Length > 0)
        {
            base.Copy();        // 用户框了一段就照他框的走，别擅自换成全文
            return;
        }

        var raw = Markdown ?? string.Empty;
        if (raw.Length == 0) { e.Handled = true; return; }
        try
        {
            Clipboard.SetText(raw);
        }
        catch { /* 剪贴板被别的进程占着（CLIPBRD_E_C_DATA_BUSY），吞掉 */ }
        e.Handled = true;
    }
}
