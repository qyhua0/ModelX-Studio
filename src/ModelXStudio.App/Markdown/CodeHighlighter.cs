// ====================================================================
// 作者：zhenhua
// 功能：代码块轻量语法高亮器。输入「围栏里的语言名 + 代码原文」，输出 (片段, 颜色类别) 序列，
//       由 Markdown/MarkdownX.cs 转成 FlowDocument 的着色 Run。
// 设计取舍：
//   · 不引第三方高亮库（ColorCode/TextMate 之类）：体积大、离线装不上，而且聊天里的代码块
//     只需要「关键字/字符串/注释/数字」这一档观感，不需要真正的词法正确性。
//   · 全部手写 while + char 扫描，不用正则：流式输出时每来一个 token 都要重扫整段代码，
//     正则的回溯在「未闭合的字符串/注释」这种半截文本上最容易变成 O(n²)，扫描器最差也是线性。
//   · 硬约束：输出的片段按顺序拼接必须逐字符等于原代码。所以每个分支都只做「截原文 + 前进下标」，
//     绝不改写、绝不 trim；识别不了就一个字符一个字符当 Plain 吐出去（check 工程里逐条断言）。
//   · 字符串里的 \ 转义不截断 token；注释/串扫到行尾或文件尾就收，未闭合也照样成 token（流式友好）。
//   · 不做 Punct 满屏分色：C 系语言的每个括号都切成 token 会让 Run 数量翻倍，流式重排时明显掉帧，
//     所以运算符/标点留在 Plain 里，只在 markup / json / yaml / diff 这些「结构靠标点」的语言里用 Punct。
//   · 颜色类别只有 9 个（CodeTokenKind），主题里也就 9 支刷子，够用且不会染成大花布：
//     PascalCase 标识符只在强类型语言里给 Type 色，json/bash/markdown 里不给（普通词会被误染）。
// 许可：ModelX Studio 非商业使用许可 1.0（见仓库根 LICENSE）。上游第三方组件（llama.cpp 等）按各自 MIT 许可授权。
// ====================================================================

// 内部片段类型：到处写 (string, CodeTokenKind) 太长，而且丢名字就只能 .Item1/.Item2
using Frag = (string Text, ModelXStudio.App.Markdown.CodeTokenKind Kind);   // 别名在 namespace 外面，只能写全名

namespace ModelXStudio.App.Markdown;

/// <summary>代码片段的颜色类别（与主题里的 Md* 刷子一一对应）。</summary>
public enum CodeTokenKind
{
    Plain,
    Keyword,
    Type,
    String,
    Comment,
    Number,
    Func,
    Prep,
    Punct,
}

/// <summary>手写线性扫描的代码着色器（无正则、无第三方依赖）。</summary>
public static class CodeHighlighter
{
    /// <summary>
    /// 归一化围栏语言名：csharp/c#/cs→csharp；py/python3→python；js/javascript/node→javascript；
    /// ts/typescript→typescript；sh/bash/zsh/shell→bash；ps1/powershell→powershell；yml→yaml；
    /// md/markdown→markdown；cpp/c++/cc/cxx/hpp→cpp；text/txt/空→""（不高亮）；其余原样小写。
    /// </summary>
    public static string NormalizeLang(string? info)
    {
        if (string.IsNullOrWhiteSpace(info)) return string.Empty;

        // 围栏信息行常见写法：```csharp title=x / ```{.python} / ```bash,linenos，只认第一个 token。
        var s = info.Trim();
        int cut = s.IndexOfAny(Separators);
        if (cut >= 0) s = s.Substring(0, cut);
        if (s.Length > 2 && s[0] == '{' && s[^1] == '}') s = s.Substring(1, s.Length - 2);
        if (s.Length > 0 && s[0] == '.') s = s.Substring(1);
        s = s.ToLowerInvariant();

        switch (s)
        {
            case "c#":
            case "cs":
            case "csharp": return "csharp";
            case "py":
            case "python3": return "python";
            case "js":
            case "node":
            case "javascript": return "javascript";
            case "ts":
            case "typescript": return "typescript";
            case "sh":
            case "zsh":
            case "shell":
            case "bash": return "bash";
            case "ps1":
            case "powershell": return "powershell";
            case "yml":
            case "yaml": return "yaml";
            case "md":
            case "markdown": return "markdown";
            case "cpp":
            case "c++":
            case "cc":
            case "cxx":
            case "hpp": return "cpp";
            case "text":
            case "txt": return string.Empty;
            default: return s;
        }
    }

    private static readonly char[] Separators = { ' ', '\t', ',', ';' };

    /// <summary>
    /// 返回 (片段, 颜色类别)。未知语言 → 单个 (code, Plain)。
    /// 任何扫描异常都退化成「整段 Plain」，绝不能因为着色失败把代码本身弄丢。
    /// </summary>
    public static IEnumerable<(string Text, CodeTokenKind Kind)> Tokenize(string code, string lang)
    {
        var list = new List<Frag>();
        if (string.IsNullOrEmpty(code)) return list;

        var spec = Lookup(lang);
        if (spec == null)
        {
            list.Add((code, CodeTokenKind.Plain));
            return list;
        }

        try
        {
            switch (spec.Mode)
            {
                case Mode.Markdown: ScanMarkdown(code, list); break;
                case Mode.Diff: ScanDiff(code, list); break;
                case Mode.Markup: ScanMarkup(code, list); break;
                case Mode.Json: ScanJson(code, list); break;
                default: ScanGeneric(code, spec, list); break;
            }
        }
        catch
        {
            // 半截文本理论上不该抛（扫描器全程有边界判断），真抛了就整段降级，保证内容完整。
            list.Clear();
            list.Add((code, CodeTokenKind.Plain));
        }

        return Merge(list);
    }

    // ===================== 扫描模式与语言表 =====================

    private enum Mode { Generic, Json, Markup, Markdown, Diff }

    private sealed class Spec
    {
        public Mode Mode;
        public string[] Line = Array.Empty<string>();  // 行注释前缀："//"、"#"、"--"
        public bool LineNeedsGap;                      // # 只有在行首或前面是空白时才算注释（yaml/bash 的值里可能带 #）
        public string BlockOpen = "", BlockClose = ""; // 块注释
        public char Preproc;                           // 行首预处理字符（csharp 的 #region、cpp 的 #include），整行 Prep
        public bool DollarVar;                         // $VAR / ${X} / $(X) 当 Prep（bash、powershell、php、makefile）
        public bool DoubleQuote = true;
        public bool SingleQuote;                       // '…' 是字符串（python/sql/yaml/lua…）
        public bool CharLiteral;                       // 'x' 是字符字面量（C 系/rust/java），其它情况不成 token
        public bool Backtick;                          // `…`（js 模板串、bash 命令替换、sql 反引号标识符）
        public bool Triple;                            // python 三引号
        public bool CSharpStrings;                     // @""、$""、$@""
        public bool RustRaw;                           // r"…"、r#"…"##
        public bool JsonKey;                           // "key": / key: 用 Type 色
        public bool KeyAtLineStart;                    // 行首 key[:=] 用 KeyKind（yaml、toml/ini、makefile）
        public CodeTokenKind KeyKind = CodeTokenKind.Type;
        public char SectionOpen;                       // 行首 [section] 整段（toml/ini）用 Type
        public bool DollarVarParen = true;
        public HashSet<string>? Keywords;
        public bool IgnoreCase;
        public bool PascalType;
    }

    private static readonly Dictionary<string, Spec> Specs;

    // 必须在静态构造函数里建表，不能在字段初始化器里建：语言表在 :160，二十来张关键字表在 :969 之后，
    // 而 C# 的静态字段初始化严格按文本顺序执行 —— 直接写 `Specs = BuildSpecs()` 时 Cs/Py/Sql 还全是 null，
    // 每张 Spec 拿到的 Keywords 都是 null，结果就是「扫描器在跑，但一个关键字也不上色」，
    // 而且不报错、不抛异常，只是颜色全没了，非常难查。
    static CodeHighlighter() => Specs = BuildSpecs();

    private static Spec? Lookup(string lang)
    {
        if (string.IsNullOrEmpty(lang)) return null;
        if (Specs.TryGetValue(lang, out var s)) return s;
        // 少数常见别名只在着色表里认，不动 NormalizeLang 的对外口径（例如 rs 仍原样返回 rs）。
        switch (lang)
        {
            case "c":
            case "cuda":
            case "objc": return Specs["cpp"];
            case "fsharp": return Specs["csharp"];
            case "golang": return Specs["go"];
            case "jsx": return Specs["javascript"];
            case "tsx": return Specs["typescript"];
            case "htm":
            case "xhtml":
            case "svg": return Specs["xml"];
            case "ps":
            case "pwsh": return Specs["powershell"];
            case "ini": return Specs["toml"];
            case "conf":
            case "cfg": return Specs["toml"];
            default: return null;
        }
    }

    private static Dictionary<string, Spec> BuildSpecs()
    {
        var d = new Dictionary<string, Spec>(StringComparer.Ordinal);

        void Add(string lang, Spec s) => d[lang] = s;

        Add("csharp", new Spec
        {
            Line = new[] { "//" }, BlockOpen = "/*", BlockClose = "*/", Preproc = '#', CSharpStrings = true,
            CharLiteral = true, Keywords = Cs, PascalType = true,
        });
        Add("cpp", new Spec
        {
            Line = new[] { "//" }, BlockOpen = "/*", BlockClose = "*/", Preproc = '#', CharLiteral = true,
            Keywords = Cpp, PascalType = true,
        });
        Add("java", new Spec
        {
            Line = new[] { "//" }, BlockOpen = "/*", BlockClose = "*/", CharLiteral = true,
            Keywords = Java, PascalType = true,
        });
        Add("kotlin", new Spec
        {
            Line = new[] { "//" }, BlockOpen = "/*", BlockClose = "*/", CharLiteral = true,
            Keywords = Kotlin, PascalType = true,
        });
        Add("go", new Spec
        {
            Line = new[] { "//" }, BlockOpen = "/*", BlockClose = "*/", CharLiteral = true,
            Keywords = Go, PascalType = true,
        });
        Add("rust", new Spec
        {
            Line = new[] { "//" }, BlockOpen = "/*", BlockClose = "*/", RustRaw = true, CharLiteral = true,
            Keywords = Rust, PascalType = true,
        });
        Add("javascript", new Spec
        {
            Line = new[] { "//" }, BlockOpen = "/*", BlockClose = "*/", Backtick = true, CharLiteral = true,
            Keywords = Js,
        });
        Add("typescript", new Spec
        {
            Line = new[] { "//" }, BlockOpen = "/*", BlockClose = "*/", Backtick = true, CharLiteral = true,
            Keywords = Ts, PascalType = true,
        });
        Add("php", new Spec
        {
            Line = new[] { "//", "#" }, BlockOpen = "/*", BlockClose = "*/", SingleQuote = true, DollarVar = true,
            Keywords = Php, IgnoreCase = true,
        });

        Add("python", new Spec
        {
            Line = new[] { "#" }, Triple = true, SingleQuote = true,
            Keywords = Py,
        });
        Add("bash", new Spec
        {
            Line = new[] { "#" }, LineNeedsGap = true, SingleQuote = true, Backtick = true, DollarVar = true,
            Keywords = Bash,
        });
        Add("powershell", new Spec
        {
            Line = new[] { "#" }, BlockOpen = "<#", BlockClose = "#>", SingleQuote = true, DollarVar = true,
            Keywords = Ps, IgnoreCase = true,
        });
        Add("sql", new Spec
        {
            Line = new[] { "--" }, BlockOpen = "/*", BlockClose = "*/", SingleQuote = true, Backtick = true,
            Keywords = Sql, IgnoreCase = true,
        });
        Add("lua", new Spec
        {
            Line = new[] { "--" }, BlockOpen = "[[", BlockClose = "]]", SingleQuote = true,
            Keywords = Lua,
        });
        Add("yaml", new Spec
        {
            Line = new[] { "#" }, LineNeedsGap = true, SingleQuote = true, JsonKey = true, KeyAtLineStart = true,
        });
        Add("toml", new Spec
        {
            Line = new[] { "#" }, LineNeedsGap = true, SingleQuote = true, KeyAtLineStart = true, SectionOpen = '[',
        });
        Add("makefile", new Spec
        {
            Line = new[] { "#" }, LineNeedsGap = true, SingleQuote = true, DollarVar = true,
            KeyAtLineStart = true, KeyKind = CodeTokenKind.Func, Keywords = Make,
        });

        Add("json", new Spec { Mode = Mode.Json });
        Add("xml", new Spec { Mode = Mode.Markup });
        Add("html", new Spec { Mode = Mode.Markup });
        Add("markdown", new Spec { Mode = Mode.Markdown });
        Add("diff", new Spec { Mode = Mode.Diff });

        return d;
    }

    // ===================== 通用扫描器 =====================

    private static void ScanGeneric(string s, Spec spec, List<Frag> outTokens)
    {
        int i = 0;
        var n = s.Length;

        while (i < n)
        {
            // 每轮开头记住位置，结尾保证往前走了。着色代码里最不能出现的原地转圈：这个扫描器
            // 跑在 UI 线程上，转圈不是「颜色不对」，是整个窗口卡死（开发时真踩过：C# 的 @$"…"
            // 让 '@' 进了标识符分支，而那分支里的 while 一个字符也没吃）。
            int before = i;
            char c = s[i];

            // 空白成段吞掉：切成一个 token 比一个字符一个 token 省一半对象。
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f' || c == '\v')
            {
                int ws = i;
                while (i < n && IsSpace(s[i])) i++;
                Add(s, ws, i, CodeTokenKind.Plain, outTokens);
                continue;
            }

            bool lineStart = IsLineStart(s, i);

            // 块注释：找不到闭合就一路到文件尾（流式打到一半的 /* 就是这个形状）。
            if (spec.BlockOpen.Length > 0 && MatchAt(s, i, spec.BlockOpen))
            {
                int end = IndexOf(s, i + spec.BlockOpen.Length, spec.BlockClose);
                int stop = end < 0 ? n : end + spec.BlockClose.Length;
                Add(s, i, stop, CodeTokenKind.Comment, outTokens);
                i = stop;
                continue;
            }

            // 行注释
            if (MatchAny(s, i, spec.Line) && (!spec.LineNeedsGap || lineStart || IsSpace(s[i - 1])))
            {
                int stop = EndOfLine(s, i);
                Add(s, i, stop, CodeTokenKind.Comment, outTokens);
                i = stop;
                continue;
            }

            // 预处理（#region / #if / #include）整行一种色，视觉上跟代码分开就达到目的了。
            if (spec.Preproc != '\0' && c == spec.Preproc && lineStart)
            {
                int stop = EndOfLine(s, i);
                Add(s, i, stop, CodeTokenKind.Prep, outTokens);
                i = stop;
                continue;
            }

            // toml/ini 的 [section]
            if (spec.SectionOpen != '\0' && c == spec.SectionOpen && lineStart)
            {
                int stop = IndexOf(s, i + 1, "]");
                stop = stop < 0 ? EndOfLine(s, i) : stop + 1;
                Add(s, i, stop, CodeTokenKind.Type, outTokens);
                i = stop;
                continue;
            }

            // 字符串（含 rust/python/js 的前缀写法）
            if (TryString(s, i, spec, out int strEnd))
            {
                Add(s, i, strEnd, CodeTokenKind.String, outTokens);
                i = strEnd;
                continue;
            }

            // $VAR / ${X} / $(X)
            if (spec.DollarVar && c == '$')
            {
                int j = i + 1;
                if (j < n && (s[j] == '{' || (spec.DollarVarParen && s[j] == '(')))
                {
                    char close = s[j] == '{' ? '}' : ')';
                    int k = IndexOf(s, j + 1, close.ToString());
                    j = k < 0 ? EndOfLine(s, i) : k + 1;
                }
                else
                {
                    while (j < n && (IsIdentChar(s[j]) || s[j] == '.')) j++;
                }
                if (j > i + 1 || (j < n && (s[j] == '?' || s[j] == '#')))
                {
                    Add(s, i, j, CodeTokenKind.Prep, outTokens);
                    i = j;
                    continue;
                }
                Add(s, i, i + 1, CodeTokenKind.Plain, outTokens);
                i++;
                continue;
            }

            // 数字
            if (IsDigitStart(s, i))
            {
                int stop = ScanNumber(s, i);
                Add(s, i, stop, CodeTokenKind.Number, outTokens);
                i = stop;
                continue;
            }

            // 标识符：先看后面紧跟的是不是 '('，再查关键字表，最后才是 PascalCase 的类型色。
            // 条件里多一个 IsIdentChar(At(s, i))：IsIdentStart 为了支持 C# 的 @关键字 把 '@' 也算进来了，
            // 但 '@' 自己不能开头吃词 —— 否则下面那个 while 一个字符也不前进，直接原地转圈。
            if (IsIdentStart(c) && IsIdentChar(At(s, i)))
            {
                int st = i;
                while (i < n && IsIdentChar(s[i])) i++;
                var word = s.Substring(st, i - st);

                int after = i;
                while (after < n && (s[after] == ' ' || s[after] == '\t')) after++;
                bool callLike = after < n && s[after] == '(';

                if (spec.KeyAtLineStart && lineStart && after < n && (s[after] == ':' || s[after] == '='))
                {
                    Add(s, st, i, spec.KeyKind, outTokens);
                    continue;
                }
                if (spec.Keywords != null && Contains(spec, word))
                {
                    Add(s, st, i, CodeTokenKind.Keyword, outTokens);
                    continue;
                }
                // 关键字也可能是调用写法（如 python 的 print(），调用色让「名字」更跳一点，不重复给关键字色。
                if (callLike) { Add(s, st, i, CodeTokenKind.Func, outTokens); continue; }
                if (spec.PascalType && IsPascalType(word)) { Add(s, st, i, CodeTokenKind.Type, outTokens); continue; }
                Add(s, st, i, CodeTokenKind.Plain, outTokens);
                continue;
            }

            // 认不出来的一律单字符 Plain —— 这是「拼接必须等于原文」的兜底分支。
            Add(s, i, i + 1, CodeTokenKind.Plain, outTokens);
            i++;

            // 哪个分支要是忘了推 i（以后加语法时很容易），在这里兜住：宁可这一个字符不上色，
            // 也不能原地不动。正常路径走不到这句 —— 上面那句自己已经 i++ 了。
            if (i <= before)
            {
                Add(s, before, before + 1, CodeTokenKind.Plain, outTokens);
                i = before + 1;
            }
        }
    }

    /// <summary>扫一个字符串 token（返回结束下标）。不认的写法返回 false，交给后面的分支。</summary>
    private static bool TryString(string s, int i, Spec spec, out int end)
    {
        var n = s.Length;
        char c = s[i];
        int q = i;      // 引号位置
        int prefix = 0; // 前缀长度（r/f/@/$…）

        if (spec.CSharpStrings)
        {
            if (c == '@' && i + 1 < n && s[i + 1] == '"')
            {
                q = i + 1; prefix = 2;
                return TakeVerbatim(s, i, q, out end);
            }
            // @"…"、$@"…"、@$"…"、$"…"：两个顺序都得认（插值逐字串在日志路径 / SQL 模板里很常见）。
            if (c == '$' || c == '@')
            {
                if (i + 1 < n && s[i + 1] == '"')
                {
                    q = i + 1; prefix = 2;
                    // $"…" 走 \ 转义，@$"…" 只认 ""，两串规则不同不能混着扫
                    return c == '$' ? TakeQuoted(s, i, q, '"', false, out end)
                                    : TakeVerbatim(s, i, q, out end);
                }
                if (i + 2 < n && ((c == '$' && s[i + 1] == '@') || (c == '@' && s[i + 1] == '$')) && s[i + 2] == '"')
                {
                    q = i + 2; prefix = 3;
                    return TakeVerbatim(s, i, q, out end);
                }
            }
        }

        if (spec.RustRaw && (c == 'r' || c == 'b') && !IsIdentChar(At(s, i - 1)))
        {
            // r"…"、r#"…"#、rb"…"
            int p = i + 1;
            if (c == 'b' && p < n && s[p] == 'r') p++;
            if (p < n && s[p] == '"')
            {
                int hashes = 0;
                int k = p + 1;
                while (k < n && s[k] == '#') { hashes++; k++; }
                int stop = k;
                if (hashes == 0)
                {
                    stop = IndexOf(s, k, "\"");
                    end = stop < 0 ? n : stop + 1;
                }
                else
                {
                    var term = "\"" + new string('#', hashes);
                    stop = IndexOf(s, k, term);
                    end = stop < 0 ? n : stop + term.Length;
                }
                return end > i;
            }
        }

        // python/js 的 r""、f""、b""、rb""：只在前缀字母后面紧跟引号时才算串。
        if (IsIdentChar(c) && !spec.JsonKey)
        {
            int p = i;
            while (p < n && (s[p] == 'r' || s[p] == 'R' || s[p] == 'f' || s[p] == 'F' || s[p] == 'b' || s[p] == 'B'
                             || s[p] == 'u' || s[p] == 'U')) p++;
            if (p > i && p < n && (s[p] == '"' || (spec.SingleQuote && s[p] == '\'')) && !IsIdentChar(At(s, p + 1)))
            {
                q = p; prefix = p - i;
                return TakeQuoted(s, i, q, s[q], spec.Triple, out end);
            }
        }

        if (c == '"' && spec.DoubleQuote) return TakeQuoted(s, i, i, '"', spec.Triple, out end);
        if (c == '\'' && spec.Backtick) { /* 见下 */ }
        if (c == '\'' && (spec.SingleQuote || spec.CharLiteral))
        {
            if (spec.SingleQuote) return TakeQuoted(s, i, i, '\'', spec.Triple, out end);
            // C 系字符字面量：'x' 或 '\n'；认不出就当普通字符（避免一个撇号把后面整段染成字符串）
            if (MatchCharLiteral(s, i)) return TakeQuoted(s, i, i, '\'', false, out end);
            return Fail(out end);
        }
        if (c == '`' && spec.Backtick) return TakeQuoted(s, i, i, '`', false, out end);

        _ = prefix;
        return Fail(out end);
    }

    private static bool Fail(out int end) { end = -1; return false; }

    /// <summary>普通串：处理 \ 转义；三引号模式优先匹配三个连续引号。</summary>
    private static bool TakeQuoted(string s, int start, int quoteAt, char q, bool triple, out int end)
    {
        var n = s.Length;
        if (triple && quoteAt + 2 < n && s[quoteAt + 1] == q && s[quoteAt + 2] == q)
        {
            int i = quoteAt + 3;
            while (i < n)
            {
                if (s[i] == '\\' && i + 1 < n) { i += 2; continue; }
                if (s[i] == q && i + 2 < n && s[i + 1] == q && s[i + 2] == q)
                {
                    end = i + 3;
                    return true;
                }
                i++;
            }
            end = n;   // 未闭合三引号：吃到文件尾
            return true;
        }

        int j = quoteAt + 1;
        while (j < n)
        {
            char ch = s[j];
            if (ch == '\\' && j + 1 < n) { j += 2; continue; }          // 转义不截断 token
            if (ch == '\n' && q != '`' && q != '"' && q != '\'') break; // 反常引号别跨行
            if (ch == q)
            {
                if (j + 1 < n && s[j + 1] == q && q == '\'') { j += 2; continue; }  // sql 的 '' 转义
                end = j + 1;
                return end > start;
            }
            if (ch == '\n') break;   // 单引号串不跨行：半截串就地收口
            j++;
        }
        // 行内没找到闭合：半截字符串照样成 token，但只在当前行内，避免染掉后文。
        int eol = EndOfLine(s, quoteAt);
        end = Math.Min(j < n ? j : n, eol);
        if (j >= n) end = n;
        return end > start;
    }

    /// <summary>C# 逐字串 @"…"：只认 "" 转义，可以跨行。</summary>
    private static bool TakeVerbatim(string s, int start, int quoteAt, out int end)
    {
        int j = quoteAt + 1;
        var n = s.Length;
        while (j < n)
        {
            if (s[j] == '"')
            {
                if (j + 1 < n && s[j + 1] == '"') { j += 2; continue; }
                end = j + 1;
                return true;
            }
            j++;
        }
        end = n;
        return true;
    }

    private static bool MatchCharLiteral(string s, int i)
    {
        var n = s.Length;
        if (i + 2 < n && s[i + 2] == '\'') return true;                 // 'x'
        return i + 3 < n && s[i + 1] == '\\' && s[i + 3] == '\'';        // '\n' '\'' '\\', 简单形态
    }

    private static int ScanNumber(string s, int i)
    {
        var n = s.Length;
        if (s[i] == '0' && i + 1 < n && (s[i + 1] == 'x' || s[i + 1] == 'X' || s[i + 1] == 'b' || s[i + 1] == 'B'
                                         || s[i + 1] == 'o' || s[i + 1] == 'O'))
        {
            i += 2;
            while (i < n && (IsHex(s[i]) || s[i] == '_')) i++;
        }
        else
        {
            while (i < n && (char.IsDigit(s[i]) || s[i] == '_')) i++;
            if (i + 1 < n && s[i] == '.' && char.IsDigit(s[i + 1]))
            {
                i++;
                while (i < n && (char.IsDigit(s[i]) || s[i] == '_')) i++;
            }
            else if (i < n && s[i] == '.' && i == 0)
            {
                // 不会走到：调用方保证有数字开头
            }
            if (i + 1 < n && (s[i] == 'e' || s[i] == 'E'))
            {
                int j = i + 1;
                if (j < n && (s[j] == '+' || s[j] == '-')) j++;
                if (j < n && char.IsDigit(s[j]))
                {
                    i = j;
                    while (i < n && (char.IsDigit(s[i]) || s[i] == '_')) i++;
                }
            }
        }
        // 后缀：f/F/m/d/u/l/ul/ll/i32/u64/f64/usize…（宁可多吃两个字母，也不要把 1f 切成两段）
        while (i < n && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
        return i;
    }

    // ===================== JSON =====================

    private static void ScanJson(string s, List<Frag> outTokens)
    {
        var n = s.Length;
        int i = 0;
        while (i < n)
        {
            int before = i;   // 见 ScanGeneric 里的同类兜底：宁可不画颜色，也不能原地不动
            char c = s[i];
            if (IsSpace(c))
            {
                int st = i;
                while (i < n && IsSpace(s[i])) i++;
                Add(s, st, i, CodeTokenKind.Plain, outTokens);
                continue;
            }
            if (c == '"')
            {
                int stop;
                if (!TryQuotedEnd(s, i, '"', out stop)) stop = n;
                // 往后找冒号（跳过空白）→ 是 key 就不是普通字符串值
                int k = stop;
                while (k < n && IsSpace(s[k])) k++;
                var kind = k < n && s[k] == ':' ? CodeTokenKind.Type : CodeTokenKind.String;
                Add(s, i, stop, kind, outTokens);
                i = stop;
                continue;
            }
            if (char.IsDigit(c) || (c == '-' && i + 1 < n && char.IsDigit(s[i + 1])))
            {
                int from = c == '-' ? i + 1 : i;
                int stop = ScanNumber(s, from);
                Add(s, i, stop, CodeTokenKind.Number, outTokens);
                i = stop;
                continue;
            }
            // 用 IsIdentChar 而不是 IsIdentStart：后者把 C# 的 @关键字 也算成开头字符，
            // 而 '@' 自己不算标识符字符 —— 那个组合会让下面的 while 一个字符都不吃，整条 JSON 卡死。
            if (IsIdentChar(c))
            {
                int st = i;
                while (i < n && IsIdentChar(s[i])) i++;
                var w = s.Substring(st, i - st);
                var kind = (w == "true" || w == "false" || w == "null") ? CodeTokenKind.Keyword : CodeTokenKind.Plain;
                Add(s, st, i, kind, outTokens);
                continue;
            }
            Add(s, i, i + 1, CodeTokenKind.Punct, outTokens);   // { } [ ] : , 是 json 的骨架，给淡色
            i++;

            if (i <= before)
            {
                Add(s, before, before + 1, CodeTokenKind.Plain, outTokens);
                i = before + 1;
            }
        }
    }

    // ===================== XML / HTML =====================

    private static void ScanMarkup(string s, List<Frag> outTokens)
    {
        var n = s.Length;
        int i = 0;
        while (i < n)
        {
            int before = i;   // 同上：任何分支忘了推 i 都在末尾兜住
            if (s[i] != '<')
            {
                int st = i;
                while (i < n && s[i] != '<') i++;
                Add(s, st, i, CodeTokenKind.Plain, outTokens);
                continue;
            }
            if (MatchAt(s, i, "<!--"))
            {
                int end = IndexOf(s, i + 4, "-->");
                int stop = end < 0 ? n : end + 3;
                Add(s, i, stop, CodeTokenKind.Comment, outTokens);
                i = stop;
                continue;
            }
            int gt = IndexOf(s, i + 1, ">");
            if (gt < 0)
            {
                Add(s, i, n, CodeTokenKind.Plain, outTokens);   // 半截标签，别硬凑成 Keyword
                i = n;
                continue;
            }
            // 标签整体：尖括号 Punct，标签名 Keyword，属性名 Type，属性值 String
            int t = i;
            Add(s, t, t + 1, CodeTokenKind.Punct, outTokens); t++;                    // <
            if (t < n && s[t] == '/') { Add(s, t, t + 1, CodeTokenKind.Punct, outTokens); t++; }
            int nameStart = t;
            while (t < gt && (IsIdentChar(s[t]) || s[t] == ':' || s[t] == '-' || s[t] == '.')) t++;
            if (t > nameStart) { Add(s, nameStart, t, CodeTokenKind.Keyword, outTokens); }
            while (t < gt)
            {
                if (IsSpace(s[t]))
                {
                    int st = t;
                    while (t < gt && IsSpace(s[t])) t++;
                    Add(s, st, t, CodeTokenKind.Plain, outTokens);
                    continue;
                }
                if (s[t] == '"' || s[t] == '\'')
                {
                    int stop;
                    if (!TryQuotedEnd(s, t, s[t], out stop)) stop = gt;
                    // 夹在 gt 之前：标签里没闭合的引号（`<a b="c>`）不能把收尾的 > 一起吞进 String，
                    // 否则下面那句 Add(gt, gt+1) 会把 > 再输出一遍 —— 分词拼接比原文多一个字符。
                    stop = Math.Min(stop, gt);
                    if (stop <= t) stop = gt;
                    Add(s, t, stop, CodeTokenKind.String, outTokens);
                    t = stop;
                    continue;
                }
                if (s[t] == '=')
                {
                    Add(s, t, t + 1, CodeTokenKind.Punct, outTokens);
                    t++;
                    continue;
                }
                int st2 = t;
                while (t < gt && !IsSpace(s[t]) && s[t] != '=' && s[t] != '"' && s[t] != '\'') t++;
                if (t == st2) t = gt;   // 兜底推进，避免死循环
                Add(s, st2, t, CodeTokenKind.Type, outTokens);   // 属性名（或裸值）
            }
            Add(s, gt, gt + 1, CodeTokenKind.Punct, outTokens);                       // >
            i = gt + 1;

            if (i <= before)
            {
                Add(s, before, before + 1, CodeTokenKind.Plain, outTokens);
                i = before + 1;
            }
        }
    }

    // ===================== Markdown =====================

    private static void ScanMarkdown(string s, List<Frag> outTokens)
    {
        var n = s.Length;
        int i = 0;
        bool inFence = false;
        while (i < n)
        {
            int eol = EndOfLine(s, i);
            var line = s.Substring(i, eol - i);
            var trimmed = line.TrimStart();

            if (inFence)
            {
                Add(s, i, eol, CodeTokenKind.String, outTokens);
                if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~")) inFence = false;
                i = eol;
                continue;
            }
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                Add(s, i, eol, CodeTokenKind.String, outTokens);
                inFence = true;
                i = eol;
                continue;
            }
            if (trimmed.StartsWith("#"))
            {
                Add(s, i, eol, CodeTokenKind.Keyword, outTokens);       // 标题整行
                i = eol;
                continue;
            }
            if (trimmed.StartsWith(">"))
            {
                Add(s, i, eol, CodeTokenKind.Comment, outTokens);       // 引用
                i = eol;
                continue;
            }

            // 行内：`code` 与 (url) 给 String 色，列表符号给 Punct，其余 Plain。
            int p = i;
            int markEnd = i;
            while (markEnd < eol && (s[markEnd] == '-' || s[markEnd] == '*' || s[markEnd] == '+' || char.IsDigit(s[markEnd]) || s[markEnd] == '.' || s[markEnd] == ' '))
            {
                if (s[markEnd] == ' ' || s[markEnd] == '\t') { if (markEnd + 1 < eol && (s[markEnd + 1] == '-' || s[markEnd + 1] == '*' || s[markEnd + 1] == '+')) { markEnd++; markEnd++; break; } break; }
                markEnd++;
            }
            bool bullet = markEnd > i && markEnd < eol && (s[markEnd - 1] == '-' || s[markEnd - 1] == '*' || s[markEnd - 1] == '+' || s[markEnd - 1] == '.');
            if (bullet)
            {
                Add(s, i, markEnd, CodeTokenKind.Punct, outTokens);
                p = markEnd;
            }

            while (p < eol)
            {
                char ch = s[p];
                if (ch == '`')
                {
                    int close = IndexOf(s, p + 1, "`");
                    int stop = close < 0 || close >= eol ? eol : close + 1;
                    Add(s, p, stop, CodeTokenKind.String, outTokens);
                    p = stop;
                    continue;
                }
                if (ch == '(' && p > i && s[p - 1] == ')')
                {
                    int close = IndexOf(s, p + 1, ")");
                    int stop = close < 0 || close >= eol ? eol : close + 1;
                    Add(s, p, stop, CodeTokenKind.String, outTokens);    // 链接目标
                    p = stop;
                    continue;
                }
                int st = p;
                while (p < eol && s[p] != '`' && !(s[p] == '(' && p > i && s[p - 1] == ')')) p++;
                Add(s, st, p, CodeTokenKind.Plain, outTokens);
            }

            i = eol;
        }
    }

    // ===================== diff =====================

    private static void ScanDiff(string s, List<Frag> outTokens)
    {
        var n = s.Length;
        int i = 0;
        while (i < n)
        {
            int eol = EndOfLine(s, i);
            char head = s[i];
            var kind = head == '+' ? CodeTokenKind.String        // 新增：偏绿
                       : head == '-' ? CodeTokenKind.Comment     // 删除：偏灰，读起来自然「少了」
                       : head == '@' || head == '\\' ? CodeTokenKind.Keyword  // @@ hunk @@ / diff --git
                       : CodeTokenKind.Plain;
            int stop = kind == CodeTokenKind.Plain ? eol : eol;
            Add(s, i, stop, kind, outTokens);
            i = stop;
        }
    }

    // ===================== 小工具 =====================

    private static void Add(string s, int start, int end, CodeTokenKind kind, List<Frag> outTokens)
    {
        if (end <= start) return;
        outTokens.Add((s.Substring(start, end - start), kind));
    }

    /// <summary>合并相邻同类片段：少切一刀就少一个 Run，流式重排时这点节省很明显。</summary>
    private static List<Frag> Merge(List<Frag> src)
    {
        var res = new List<Frag>(src.Count == 0 ? 1 : src.Count);
        foreach (var t in src)
        {
            if (t.Text.Length == 0) continue;
            if (res.Count > 0 && res[res.Count - 1].Kind == t.Kind)
            {
                var last = res[res.Count - 1];
                res[res.Count - 1] = (last.Text + t.Text, t.Kind);
            }
            else res.Add(t);
        }
        if (res.Count == 0) res.Add((string.Empty, CodeTokenKind.Plain));
        return res;
    }

    private static bool Contains(Spec spec, string word)
    {
        if (spec.Keywords == null) return false;
        if (spec.Keywords.Contains(word)) return true;
        if (!spec.IgnoreCase) return false;
        // SQL/PHP/PowerShell 的关键字大小写随意，这里现推大写再查一次（比给每个词建两份表省内存）。
        return spec.Keywords.Contains(word.ToUpperInvariant());
    }

    private static bool IsPascalType(string w)
    {
        if (w.Length < 2 || !char.IsUpper(w[0])) return false;
        // 全大写（常量、JSON 的 KEY）不算类型；至少要有一个小写字母才像 CamelPascal。
        for (int i = 1; i < w.Length; i++)
        {
            char c = w[i];
            if (char.IsLower(c)) return true;
            if (c == '_') return false;
        }
        return false;
    }

    private static bool IsDigitStart(string s, int i)
    {
        char c = s[i];
        if (char.IsDigit(c)) return i == 0 || !IsIdentChar(s[i - 1]);
        return c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1]) && (i == 0 || !IsIdentChar(s[i - 1]));
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c == '@';
    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    private static bool IsSpace(char c) => c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f' || c == '\v';
    private static bool IsHex(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static char At(string s, int i) => i >= 0 && i < s.Length ? s[i] : '\0';

    /// <summary>本行第一个非空白字符是不是就是这里（预处理、行首 key 都要看这个）。</summary>
    private static bool IsLineStart(string s, int i)
    {
        while (i > 0 && (s[i - 1] == ' ' || s[i - 1] == '\t')) i--;
        return i == 0 || s[i - 1] == '\n' || s[i - 1] == '\r';
    }

    private static int EndOfLine(string s, int i)
    {
        while (i < s.Length && s[i] != '\n') i++;
        return i < s.Length ? i + 1 : i;   // 把换行吃进当前 token，行内容才是纯的
    }

    private static bool MatchAt(string s, int i, string t)
    {
        if (t.Length == 0 || i + t.Length > s.Length) return false;
        for (int k = 0; k < t.Length; k++) if (s[i + k] != t[k]) return false;
        return true;
    }

    private static bool MatchAny(string s, int i, string[] list)
    {
        for (int k = 0; k < list.Length; k++) if (MatchAt(s, i, list[k])) return true;
        return false;
    }

    private static int IndexOf(string s, int from, string t)
    {
        if (from < 0) from = 0;
        return s.IndexOf(t, from, StringComparison.Ordinal);
    }

    private static bool TryQuotedEnd(string s, int quoteAt, char q, out int end)
    {
        int j = quoteAt + 1;
        var n = s.Length;
        while (j < n)
        {
            if (s[j] == '\\' && j + 1 < n) { j += 2; continue; }
            if (s[j] == q) { end = j + 1; return true; }
            if (s[j] == '\n') break;
            j++;
        }
        end = j;
        return false;
    }

    // ===================== 关键字表 =====================
    // 只列「出现频率高、染上颜色确实好认」的词，不追求语言规范完整。

    private static readonly HashSet<string> Cs = Words(
        "abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using var virtual void volatile while yield async await record init get set when not value nameof global partial");

    private static readonly HashSet<string> Cpp = Words(
        "alignas alignof and asm auto bool break case catch char class compl const consteval constexpr const_cast continue decltype default delete do double dynamic_cast else enum explicit export extern false float for friend goto if inline int long mutable namespace new nullptr operator or private protected public register reinterpret_cast return short signed sizeof static static_assert static_cast struct switch template this throw true try typedef typeid typename union unsigned using virtual void volatile wchar_t while");

    private static readonly HashSet<string> Java = Words(
        "abstract assert boolean break byte case catch char class continue default do double else enum extends final finally float for goto if implements import instanceof int interface long native new package private protected public return short static strictfp super switch synchronized this throw throws transient try var void volatile while record sealed permits yield");

    private static readonly HashSet<string> Kotlin = Words(
        "as break by class continue crossinline data do else enum expect external false for fun get if import in infix init inline interface internal is lateinit noinline null object open operator out override package private protected public reified return sealed set super suspend this throw true try typealias val var when where while");

    private static readonly HashSet<string> Go = Words(
        "break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var nil true false make new len cap append panic recover");

    private static readonly HashSet<string> Rust = Words(
        "as async await break const continue crate dyn else enum extern false fn for if impl in let loop match mod move mut pub ref return self Self static struct trait true type unsafe use where while Some None Ok Err Vec String Box Rc Arc usize u8 i32 f64");

    private static readonly HashSet<string> Js = Words(
        "async await break case catch class const continue debugger default delete do else export extends finally for from function get if import in instanceof let new of return set static super switch this throw try typeof var void while with yield null undefined true false");

    private static readonly HashSet<string> Ts = Words(
        "abstract any as asserts bigint boolean break case catch class const constructor continue declare debugger default delete do enum export extends finally for from function get if implements import in infer instanceof interface keyof let namespace new never of override package private protected public readonly return satisfies set static super switch this throw try type typeof var void while with yield null undefined true false");

    private static readonly HashSet<string> Php = Words(
        "abstract and array as break callable case catch class clone const continue declare default do echo else elseif empty enddeclare endfor endforeach endif endswitch endwhile extends final finally fn for foreach function global goto if implements include include_once instanceof interface isset list namespace new or print private protected public require require_once return static switch throw trait try unset use var while xor yield true false null");

    private static readonly HashSet<string> Py = Words(
        "and as assert async await break class continue def del elif else except finally for from global if import in is lambda match case nonlocal not or pass raise return try while with yield True False None self cls");

    private static readonly HashSet<string> Bash = Words(
        "if then elif else fi case esac for while until do done function in select time return break continue local export readonly declare typeset unset shift source alias echo cd exit set trap eval read");

    private static readonly HashSet<string> Ps = Words(
        "begin break catch continue data do dynamicparam else elseif end finally for foreach from function if in param pause return switch throw trap try until while workflow class using");

    private static readonly HashSet<string> Sql = Words(
        "SELECT FROM WHERE INSERT INTO VALUES UPDATE SET DELETE CREATE TABLE ALTER DROP ADD COLUMN INDEX VIEW JOIN LEFT RIGHT INNER OUTER FULL CROSS ON GROUP BY ORDER HAVING LIMIT OFFSET AS AND OR NOT NULL IS IN LIKE RLIKE BETWEEN EXISTS UNION ALL DISTINCT CASE WHEN THEN ELSE END COUNT SUM AVG MIN MAX ASC DESC PRIMARY KEY FOREIGN REFERENCES DEFAULT CONSTRAINT UNIQUE CHECK AUTO_INCREMENT ENGINE CHARSET COLLATE DATABASE SCHEMA USE GRANT REVOKE COMMIT ROLLBACK TRANSACTION BEGIN DECLARE EXEC EXECUTE PROCEDURE FUNCTION TRIGGER INT INTEGER BIGINT SMALLINT TINYINT DECIMAL NUMERIC FLOAT DOUBLE REAL VARCHAR NVARCHAR CHAR TEXT BLOB DATE TIME DATETIME TIMESTAMP BOOLEAN BOOL SERIAL JSON UUID TRUE FALSE IF WHILE FOR RETURN");

    private static readonly HashSet<string> Lua = Words(
        "and break do else elseif end false for function goto if in local nil not or repeat return then true until while self");

    private static readonly HashSet<string> Make = Words(
        "ifeq ifneq ifdef ifndef else endif include -include sinclude define endef export unexport override vpath");

    private static HashSet<string> Words(string text)
        => new HashSet<string>(text.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
}
