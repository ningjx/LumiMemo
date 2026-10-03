using System.Text;
using LumiText.Core.Documents;

namespace LumiText.Core.Editing;

/// <summary>
/// RTF 投影（Phase 2 设计 §7）：新模型 ↔ RTF 最小集双向转换。
/// <b>只用于剪贴板互通，不是权威格式</b>（框架稿红线 4）——权威格式是 v2 JSON。
/// 最小集：\b \i \ul \strike \cf \highlight + 颜色表 + 段落换行；图片 \pict 留待 M6 内嵌图片。
/// 不支持的 tag 解析时静默跳过，复杂结构降级为纯文本。
/// </summary>
public static class RtfProjection
{
    /// <summary>把文档导出为 RTF 字符串（剪贴板写入用）。</summary>
    public static string ToRtf(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);

        // 第一遍：收集用到的颜色，建颜色表（RTF 颜色表索引从 1 开始）。
        var colors = new List<Color32>();
        var colorIndex = new Dictionary<Color32, int>();
        foreach (var block in document.Blocks)
        {
            if (!BlockTextOps.IsTextBlock(block))
            {
                continue;
            }
            foreach (var run in BlockTextOps.GetRuns(block))
            {
                // 前景色与文字底色共用一张颜色表（\cf 与 \highlight 都按下标引用）
                if (run.Style?.Color is { } c && !colorIndex.ContainsKey(c))
                {
                    colors.Add(c);
                    colorIndex[c] = colors.Count; // 1-based
                }
                if (run.Style?.Background is { } bg && !colorIndex.ContainsKey(bg))
                {
                    colors.Add(bg);
                    colorIndex[bg] = colors.Count;
                }
            }
        }

        var sb = new StringBuilder();
        sb.Append(@"{\rtf1\ansi\ansicpg65001\deff0");
        // 字体表（只用默认字体）：{\fonttbl{\f0 名称;}}
        sb.Append(@"{\fonttbl{\f0 ").Append(TextStyle.Default.FontFamily).Append(";}}");
        // 颜色表
        if (colors.Count > 0)
        {
            sb.Append(@"{\colortbl ;");
            foreach (var c in colors)
            {
                sb.Append(@"\red").Append(c.R).Append(@"\green").Append(c.G).Append(@"\blue").Append(c.B).Append(';');
            }
            sb.Append('}');
        }
        sb.Append('\n');

        // 正文：块 → 段落，run → 样式组
        bool firstBlock = true;
        foreach (var block in document.Blocks)
        {
            if (!firstBlock)
            {
                sb.Append(@"\par").Append('\n');
            }
            firstBlock = false;

            switch (block)
            {
                case DividerBlock:
                    // 分割线降级为一条字符线
                    sb.Append("────────");
                    break;
                case ImageBlock:
                    // 图片 \pict 留待 M6 内嵌图片；占位避免静默丢内容
                    sb.Append("[图片]");
                    break;
                default:
                    if (BlockTextOps.IsTextBlock(block))
                    {
                        // Heading 用字号比表达（RTF 无标题概念，用绝对字号 \fs，单位半磅）+ 块级加粗
                        bool blockBold = block is HeadingBlock { EffectiveStyle.Bold: true };
                        if (block is HeadingBlock h)
                        {
                            sb.Append(@"\fs").Append((int)(h.EffectiveStyle.FontSize * 2)).Append(' ');
                        }
                        foreach (var run in BlockTextOps.GetRuns(block))
                        {
                            AppendRun(sb, run, colorIndex, blockBold);
                        }
                        if (block is HeadingBlock)
                        {
                            sb.Append(@"\fs").Append((int)(TextStyle.Default.FontSize * 2)).Append(' ');
                        }
                    }
                    break;
            }
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendRun(StringBuilder sb, TextRun run, Dictionary<Color32, int> colorIndex,
        bool blockBold = false)
    {
        var style = run.Style;
        // 块级加粗（标题的 EffectiveStyle.Bold）与 run 级加粗取或：H1–H3 复制到 Word 也要粗
        bool bold = blockBold || (style?.Bold ?? false);
        bool hasStyle = bold ||
            (style is not null &&
                (style.Italic || style.Strikethrough || style.Underline
                    || style.Color is not null || style.Background is not null));
        if (!hasStyle)
        {
            AppendEscaped(sb, run.Text);
            return;
        }

        sb.Append('{');
        if (bold) sb.Append(@"\b");
        if (style?.Italic == true) sb.Append(@"\i");
        if (style?.Underline == true) sb.Append(@"\ul");
        if (style?.Strikethrough == true) sb.Append(@"\strike");
        if (style?.Color is { } c && colorIndex.TryGetValue(c, out int idx))
        {
            sb.Append(@"\cf").Append(idx);
        }
        if (style?.Background is { } bg && colorIndex.TryGetValue(bg, out int highlight))
        {
            sb.Append(@"\highlight").Append(highlight);
        }
        sb.Append(' ');
        AppendEscaped(sb, run.Text);
        // 关闭样式
        if (bold) sb.Append(@"\b0");
        if (style?.Italic == true) sb.Append(@"\i0");
        if (style?.Underline == true) sb.Append(@"\ul0");
        if (style?.Strikethrough == true) sb.Append(@"\strike0");
        if (style?.Color is not null) sb.Append(@"\cf0");
        if (style?.Background is not null) sb.Append(@"\highlight0");
        sb.Append('}');
    }

    private static void AppendEscaped(StringBuilder sb, string text)
    {
        foreach (char ch in text)
        {
            switch (ch)
            {
                case '\\': sb.Append(@"\\"); break;
                case '{': sb.Append(@"\{"); break;
                case '}': sb.Append(@"\}"); break;
                default:
                    if (ch > 0x7F)
                    {
                        // RTF 非 ASCII 用 \uN?（带 ANSI 回退字符 ?）
                        sb.Append(@"\u").Append((short)ch).Append('?');
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// 解析 RTF 为文档（剪贴板读取用）。最小子集：\b \i \ul \strike \cf \highlight \par \u + 颜色表。
    /// 不支持的 tag 静默跳过；解析失败/无样式时退化为单段落纯文本。
    /// </summary>
    public static Document FromRtf(string rtf)
    {
        ArgumentNullException.ThrowIfNull(rtf);
        var parser = new Parser(rtf);
        return parser.Parse();
    }

    private sealed class Parser
    {
        private readonly string _rtf;
        private int _pos;
        private readonly List<Color32> _colorTable = new();
        private readonly List<Block> _blocks = new();
        private readonly List<TextRun> _currentRuns = new();
        private readonly StringBuilder _currentText = new();
        private InlineStyle _currentStyle = new();

        public Parser(string rtf)
        {
            _rtf = rtf;
        }

        public Document Parse()
        {
            while (_pos < _rtf.Length)
            {
                char ch = _rtf[_pos];
                if (ch == '\\')
                {
                    ParseControlWord();
                }
                else if (ch == '{')
                {
                    _pos++;
                    // 父组已是 skip → 子组继承（计入深度）；否则待第一个控制词判定。
                    if (_skipDepth > 0)
                    {
                        _skipDepth++;
                        _skipStack.Push(SkipKind.Inherited);
                    }
                    else
                    {
                        _skipStack.Push(SkipKind.None);
                        _pendingDecision = true;
                    }
                }
                else if (ch == '}')
                {
                    if (_skipStack.Count > 0)
                    {
                        // 起点或继承组都计入了深度，关闭时 depth--；起点额外复位颜色表。
                        var kind = _skipStack.Pop();
                        if (kind != SkipKind.None && _skipDepth > 0)
                        {
                            _skipDepth--;
                        }
                        if (kind == SkipKind.Start)
                        {
                            _inColorTable = false;
                        }
                    }
                    _pos++;
                }
                else if (ch == '\r' || ch == '\n')
                {
                    _pos++;
                }
                else if (_skipDepth == 0)
                {
                    _currentText.Append(ch);
                    _pos++;
                }
                else
                {
                    _pos++; // 跳过组内普通字符（字体名等）
                }
            }
            FlushText();
            FlushBlock();
            if (_blocks.Count == 0)
            {
                _blocks.Add(new ParagraphBlock(""));
            }
            return new Document(_blocks);
        }

        private enum SkipKind { None, Start, Inherited }

        private int _skipDepth;
        private bool _pendingDecision;
        private readonly Stack<SkipKind> _skipStack = new();

        /// <summary>元数据控制词命中：把当前待判定组标记为 skip 起点。</summary>
        private void MarkCurrentGroupSkipped()
        {
            if (_pendingDecision && _skipStack.Count > 0)
            {
                _skipStack.Pop();
                _skipStack.Push(SkipKind.Start);
                _skipDepth++;
                _pendingDecision = false;
            }
        }

        private void ParseControlWord()
        {
            _pos++; // 跳过 '\'
            if (_pos >= _rtf.Length)
            {
                return;
            }
            // 转义字符 \\ \{ \}
            char c = _rtf[_pos];
            if (c == '\\' || c == '{' || c == '}')
            {
                _currentText.Append(c);
                _pos++;
                return;
            }
            // 读控制词字母
            int start = _pos;
            while (_pos < _rtf.Length && char.IsLetter(_rtf[_pos]))
            {
                _pos++;
            }
            string word = _rtf[start.._pos];
            // 读可选数字参数（含负号）
            int numStart = _pos;
            if (_pos < _rtf.Length && (_rtf[_pos] == '-'))
            {
                _pos++;
            }
            while (_pos < _rtf.Length && char.IsDigit(_rtf[_pos]))
            {
                _pos++;
            }
            int? num = null;
            if (_pos > numStart && int.TryParse(_rtf[numStart.._pos], out int n))
            {
                num = n;
            }
            // 控制词后单空格是分隔符，吃掉
            if (_pos < _rtf.Length && _rtf[_pos] == ' ')
            {
                _pos++;
            }

            ApplyControlWord(word, num);
        }

        private void ApplyControlWord(string word, int? num)
        {
            switch (word)
            {
                // 元数据组：当前待判定组标记为 skip 起点（字体名/样式表名/图片二进制不进正文）
                case "fonttbl": case "stylesheet": case "info": case "pict":
                    MarkCurrentGroupSkipped();
                    _pendingDecision = false;
                    break;
                case "colortbl":
                    // 颜色表组：普通字符跳过，但 \red\green\blue 控制词照常解析
                    _inColorTable = true;
                    MarkCurrentGroupSkipped();
                    _pendingDecision = false;
                    break;
                case "b": if (_skipDepth == 0) SetStyle(bold: num != 0); _pendingDecision = false; break;
                case "i": if (_skipDepth == 0) SetStyle(italic: num != 0); _pendingDecision = false; break;
                case "ul": if (_skipDepth == 0) SetStyle(underline: num != 0); _pendingDecision = false; break;
                case "ulnone": if (_skipDepth == 0) SetStyle(underline: false); _pendingDecision = false; break;
                case "strike": if (_skipDepth == 0) SetStyle(strikethrough: num != 0); _pendingDecision = false; break;
                case "cf": if (_skipDepth == 0) SetColor(num); _pendingDecision = false; break;
                case "highlight": if (_skipDepth == 0) SetBackground(num); _pendingDecision = false; break;
                case "par": if (_skipDepth == 0) { FlushText(); FlushBlock(); } _pendingDecision = false; break;
                case "u": if (_skipDepth == 0) AppendUnicode(num); _pendingDecision = false; break;
                case "red": case "green": case "blue": CollectColor(word, num); break;
                case "rtf": case "ansi": case "ansicpg": case "deff": case "f": case "fs":
                    _pendingDecision = false; // 正文/声明控制词：外层组是正文组，不是元数据组
                    break;
                default:
                    break; // 不支持的 tag 静默跳过
            }
        }

        private bool _inColorTable;
        private int _r, _g, _b;

        private void CollectColor(string word, int? num)
        {
            if (!_inColorTable || num is not int v)
            {
                return;
            }
            switch (word)
            {
                case "red": _r = v; break;
                case "green": _g = v; break;
                case "blue":
                    _b = v;
                    _colorTable.Add(new Color32(255, (byte)_r, (byte)_g, (byte)_b));
                    break;
            }
        }

        private void SetStyle(bool? bold = null, bool? italic = null,
            bool? underline = null, bool? strikethrough = null)
        {
            FlushText();
            _currentStyle = new InlineStyle(
                Bold: bold ?? _currentStyle.Bold,
                Italic: italic ?? _currentStyle.Italic,
                Strikethrough: strikethrough ?? _currentStyle.Strikethrough,
                Underline: underline ?? _currentStyle.Underline,
                Color: _currentStyle.Color,
                Background: _currentStyle.Background);
        }

        private void SetColor(int? num)
        {
            FlushText();
            Color32? color = null;
            if (num is > 0 && num.Value <= _colorTable.Count)
            {
                color = _colorTable[num.Value - 1];
            }
            _currentStyle = _currentStyle with { Color = color };
        }

        /// <summary>文字底色（\highlightN，N=0 表示无底色）；与 \cf 共用颜色表。</summary>
        private void SetBackground(int? num)
        {
            FlushText();
            Color32? background = null;
            if (num is > 0 && num.Value <= _colorTable.Count)
            {
                background = _colorTable[num.Value - 1];
            }
            _currentStyle = _currentStyle with { Background = background };
        }

        private void AppendUnicode(int? num)
        {
            if (num is not int code)
            {
                return;
            }
            // RTF \uN 是带符号 16 位；负值要按 ushort 还原
            char ch = (char)(ushort)(short)code;
            _currentText.Append(ch);
            // \uN 后跟一个 ANSI 回退字符（通常 '?'），吃掉
            if (_pos < _rtf.Length && _rtf[_pos] != '\\')
            {
                _pos++;
            }
        }

        private void FlushText()
        {
            if (_currentText.Length == 0)
            {
                return;
            }
            var style = _currentStyle == new InlineStyle() ? null : _currentStyle;
            _currentRuns.Add(new TextRun(_currentText.ToString(), style));
            _currentText.Clear();
        }

        private void FlushBlock()
        {
            if (_currentRuns.Count > 0)
            {
                _blocks.Add(new ParagraphBlock(_currentRuns.ToList()));
                _currentRuns.Clear();
            }
            else if (_blocks.Count > 0 || _currentText.Length > 0)
            {
                // 空段落
                _blocks.Add(new ParagraphBlock(""));
            }
        }
    }
}
