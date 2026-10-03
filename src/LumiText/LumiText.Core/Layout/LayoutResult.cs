using LumiText.Core.Documents;
using LumiText.Core.Editing;

namespace LumiText.Core.Layout;

/// <summary>
/// 命中测试结果（M2 字符级）：命中的行 + 块内字符偏移 + 落尾标记。
/// <see cref="CaretPosition"/> 把命中结果直接翻译成编辑器可用的光标位置。
/// </summary>
public readonly record struct HitTestResult(
    bool Found,
    int BlockIndex,
    int CharIndex,
    bool IsTrailingHit)
{
    /// <summary>命中点对应的光标位置（落尾时落在字符之后）。</summary>
    public TextPosition CaretPosition => new(BlockIndex, CharIndex);
}

/// <summary>
/// 一次完整排版的产物。实现 <see cref="IDisposable"/>：各行引用的
/// <see cref="PlacedLine.Batch"/> 的释放责任归本对象（按引用去重后逐个 Dispose，§5.2 所有权契约）。
/// </summary>
public sealed class LayoutResult : IDisposable
{
    public LayoutResult(
        IReadOnlyList<PlacedLine> lines,
        IReadOnlyList<FloatObject> floats,
        float totalHeight,
        IReadOnlyList<Block>? blocks = null,
        IReadOnlyList<BlockExtent>? blockExtents = null)
    {
        Lines = lines;
        Floats = floats;
        TotalHeight = totalHeight;
        Blocks = blocks;
        BlockExtents = blockExtents ?? [];
    }

    /// <summary>全部已放置行盒，按文档顺序（块序 → 字符序）。</summary>
    public IReadOnlyList<PlacedLine> Lines { get; }

    /// <summary>参与本次排版的浮动对象（含最终位置）。</summary>
    public IReadOnlyList<FloatObject> Floats { get; }

    /// <summary>文档总高（内容底缘与浮动对象底缘的较大者）。</summary>
    public float TotalHeight { get; }

    /// <summary>
    /// 源块列表（只读透传，M4）：渲染层画 Todo 复选框需要 <c>TodoBlock.Checked</c>、
    /// 分派块级绘制路径需要块类型（Phase 1 设计 §4——排版产物自身不带块元数据会让渲染层无路可查）。
    /// 经旧签名（段落列表）排版时为 <see langword="null"/>。
    /// </summary>
    public IReadOnlyList<Block>? Blocks { get; }

    /// <summary>
    /// 块几何（Phase 3 §4）：带底色的块的行盒并集矩形（块序），渲染层绘制块背景的数据源。
    /// 无带色块时为空表。
    /// </summary>
    public IReadOnlyList<BlockExtent> BlockExtents { get; }

    /// <summary>坐标命中：命中最上层浮动对象（用于拖动/手柄命中）。</summary>
    public FloatObject? FloatAt(float x, float y)
    {
        for (int i = Floats.Count - 1; i >= 0; i--)
        {
            if (Floats[i].Rect.Contains(x, y))
            {
                return Floats[i];
            }
        }
        return null;
    }

    /// <summary>
    /// 浮动对象的锚定命中（Phase 3 M4，图片拖动落点规则）：在「图片矩形纵向覆盖到的第一条
    /// 文本行」上，取图片左缘处的插入位置——即紧挨图片左侧的那个字之后。
    /// 覆盖不到任何文本行（图片在所有文字之下）时取末行；左缘在文本左/右之外时取行首/行尾。
    /// 传入的版面应是「不含该图片」的自然版面（预览版面里文字已被图片挤开，会差一格）。
    /// </summary>
    public HitTestResult HitTestFloatAnchor(LayoutRect imageRect)
    {
        if (Lines.Count == 0)
        {
            return default;
        }

        // 第一条纵向相交的文本行（行盒按 Y 有序）：跳过「底缘在图片顶缘之上」的行
        PlacedLine? first = null;
        foreach (var line in Lines)
        {
            if (line.Y + line.Height <= imageRect.Y)
            {
                continue;
            }
            first = line;
            break;
        }
        first ??= Lines[^1]; // 图片在所有文字之下：取末行
        var target = first;

        // 行内取「图片左缘」处的插入位置
        if (imageRect.X <= target.X)
        {
            return new HitTestResult(true, target.BlockIndex, target.CharStart, false);
        }
        if (imageRect.X >= target.X + target.Width)
        {
            return new HitTestResult(true, target.BlockIndex,
                target.CharStart + target.CharCount, true);
        }
        if (target.Batch is not { } batch)
        {
            return new HitTestResult(true, target.BlockIndex, target.CharStart, false);
        }
        var hit = batch.HitTestChar(
            imageRect.X - target.X, target.LineOffsetY + (target.Height / 2f));
        if (hit is not { } h)
        {
            return new HitTestResult(true, target.BlockIndex, target.CharStart, false);
        }
        return new HitTestResult(true, target.BlockIndex,
            h.CharacterIndex + (h.IsTrailingHit ? 1 : 0), h.IsTrailingHit);
    }

    /// <summary>
    /// 锚点 → 浮动矩形左上角（Phase 3 M4 提取的单一事实源）：锚字符所在<b>行盒顶缘</b>（Y）+
    /// 行内插入位置（X，<paramref name="anchorToChar"/>）或按 <paramref name="side"/> 贴内容区左/右缘。
    /// 排版引擎的锚定解析与编辑器拖动/缩放的实时预览共用本方法，保证「预览即结果」。
    /// </summary>
    /// <param name="anchor">锚点；<see cref="FloatAnchor.CharIndex"/> 是块内<b>插入位置</b>
    /// （与 <see cref="LumiText.Core.Editing.TextPosition"/> 同坐标系，同 <see cref="HitTestFloatAnchor"/> 的输出）。</param>
    /// <param name="anchorToChar">true = 紧跟锚字符之后（X 随文字重排走）；false = 按侧贴缘。</param>
    /// <param name="side">浮动侧（<paramref name="anchorToChar"/> 为 false 时生效）。</param>
    /// <param name="floatWidth">浮动矩形宽度（横向钳制用）。</param>
    /// <param name="contentWidth">内容区宽度（dip）。</param>
    /// <returns>false = 全文无文本行盒（调用方回退到 Rect 直给路径）。</returns>
    public bool TryResolveAnchorTopLeft(FloatAnchor anchor, bool anchorToChar, FloatSide side,
        float floatWidth, float contentWidth, out float x, out float y)
    {
        x = 0f;
        y = 0f;
        if (Blocks is not { Count: > 0 } blocks)
        {
            return false;
        }

        // 文本行盒按块分组（只认 Text/TodoText；Divider 占位行盒不算文本块，与 M2 命中一致）
        Dictionary<int, List<PlacedLine>>? linesByBlock = null;
        foreach (var line in Lines)
        {
            if (line.Kind is not (PlacedLineKind.Text or PlacedLineKind.TodoText))
            {
                continue;
            }
            linesByBlock ??= [];
            if (!linesByBlock.TryGetValue(line.BlockIndex, out var list))
            {
                linesByBlock[line.BlockIndex] = list = [];
            }
            list.Add(line);
        }
        if (linesByBlock is null)
        {
            return false;
        }

        // 锚点块解析的异常路径：越界钳到首/末块；锚到非文本块（或无行盒的空块）→
        // 顺延到其后第一个有行盒的文本块；其后没有 → 钳到其前最后一个。
        int start = Math.Clamp(anchor.BlockIndex, 0, blocks.Count - 1);
        List<PlacedLine>? anchorLines = null;
        for (int b = start; b < blocks.Count && anchorLines is null; b++)
        {
            linesByBlock.TryGetValue(b, out anchorLines);
        }
        for (int b = start - 1; b >= 0 && anchorLines is null; b--)
        {
            linesByBlock.TryGetValue(b, out anchorLines);
        }
        if (anchorLines is not { Count: > 0 })
        {
            return false;
        }

        // 按字符定位行盒：找第一个「行末字符偏移 > CharIndex」的行盒；
        // CharIndex 越界（≥ 块总字符数）→ 钳到该块末行。
        var anchorLine = anchorLines[^1];
        foreach (var line in anchorLines)
        {
            if (line.CharStart + line.CharCount > anchor.CharIndex)
            {
                anchorLine = line;
                break;
            }
        }

        y = anchorLine.Y;
        if (anchorToChar)
        {
            float inlineX = anchorLine.X;
            if (anchorLine.Batch is { } batch)
            {
                int lineStart = anchorLine.CharStart;
                int lineEnd = anchorLine.CharStart + anchorLine.CharCount;
                int index = Math.Clamp(anchor.CharIndex, lineStart, lineEnd);
                // 插入位置的几何：行内取「本字符左缘」（= 前一字符右缘，isTrailing=false）；
                // 块尾（index == lineEnd，行内无处可取左缘）取末字符右缘（isTrailing=true）。
                // 恒定 isTrailing=true 会让图片偏右一个字——A1 探针实测
                // （索引 4 的左缘 56 / 右缘 70，锚定落到了 70）。
                bool isTrailing = index >= lineEnd;
                var caret = batch.GetCaretGeometry(index, isTrailing);
                inlineX += caret.X;
            }
            x = Math.Clamp(inlineX, 0f, Math.Max(0f, contentWidth - floatWidth));
        }
        else
        {
            x = side == FloatSide.Left ? 0f : contentWidth - floatWidth;
        }
        return true;
    }

    /// <summary>
    /// Todo 复选框命中（Phase 3 M3）：返回命中的块索引，-1 = 未命中。
    /// 判定 = 该块<b>首行</b>行盒的纵向范围内、X 落在悬挂缩进区（复选框绘制区，与
    /// <see cref="LumiText.Core.Documents.TodoBlock.LeftIndent"/> 同宽）。
    /// 无副作用：悬停（光标/高亮）与点击共用同一判定。
    /// </summary>
    public int HitTestTodoCheckbox(float x, float y)
    {
        if (Blocks is not { } blocks)
        {
            return -1;
        }
        foreach (var line in Lines)
        {
            if (line.Y > y)
            {
                break; // 行盒按 Y 有序，越过即停
            }
            if (line.Kind != PlacedLineKind.TodoText || !line.IsBlockStart)
            {
                continue;
            }
            if (y < line.Y || y > line.Y + line.Height)
            {
                continue;
            }
            if (line.BlockIndex >= blocks.Count || blocks[line.BlockIndex] is not TodoBlock todo)
            {
                continue;
            }
            if (x >= line.X - todo.LeftIndent && x <= line.X)
            {
                return line.BlockIndex;
            }
        }
        return -1;
    }

    /// <summary>
    /// 坐标命中：字符级命中（M2）+ 空白区兜底（Phase 3 修复）。
    /// 先定位行盒，再经行盒所属批的 <see cref="ILineBatch.HitTestChar"/> 精确到字符偏移；
    /// 批不支持字符级命中（如 Divider 占位行盒无批）时退化为行首字符（M1 行为）。
    /// </summary>
    /// <remarks>
    /// 点在行盒矩形之外但纵向仍落在该行高度内（短行右侧空白、缩进区左侧、
    /// 被浮动挤开的窄行两端）→ 取横向距离最近的行，落行首/行尾——
    /// 否则「行尾空白点不到、拖选经过空白就断」。
    /// 纵向整篇之外：首行之上 → 文档首，末行之下 → 文档末。
    /// </remarks>
    public HitTestResult HitTest(float x, float y)
    {
        foreach (var line in Lines)
        {
            if (!line.Bounds.Contains(x, y))
            {
                continue;
            }
            return HitOnLine(line, x, (y - line.Y) + line.LineOffsetY);
        }

        if (Lines.Count == 0)
        {
            return default;
        }

        // 空白区兜底：取「纵向距离 → 横向距离」字典序最近的行。覆盖：短行右侧空白、
        // 缩进区左侧、被浮动挤开的窄行两端、块间距（spaceAfter）空白带、
        // 首行之上 / 末行之下（点空白处落最近文本，与常见编辑器一致）。
        PlacedLine? nearest = null;
        float bestVertical = float.MaxValue;
        float bestHorizontal = float.MaxValue;
        foreach (var line in Lines)
        {
            float vertical = y < line.Y
                ? line.Y - y
                : y >= line.Y + line.Height ? y - (line.Y + line.Height) : 0f;
            if (vertical > bestVertical)
            {
                continue;
            }
            float horizontal = x < line.X
                ? line.X - x
                : x > line.X + line.Width ? x - (line.X + line.Width) : 0f;
            if (vertical < bestVertical || horizontal < bestHorizontal)
            {
                bestVertical = vertical;
                bestHorizontal = horizontal;
                nearest = line;
            }
        }
        if (nearest is not { } target)
        {
            return default;
        }

        if (x <= target.X)
        {
            return new HitTestResult(true, target.BlockIndex, target.CharStart, false);
        }
        if (x >= target.X + target.Width)
        {
            return new HitTestResult(true, target.BlockIndex, target.CharStart + target.CharCount, true);
        }
        // x 仍在文本推进宽度内（点在行带的纵向之外）：按 x 精确落字，y 钳到行内中线
        return HitOnLine(target, x, target.LineOffsetY + target.Height / 2f);
    }

    /// <summary>
    /// 行内精确字符命中（x 为文档坐标，<paramref name="localY"/> 为批布局坐标）。
    /// 批缺省（占位行盒）或批答不上来（点在批文本区外）时退化为行首。
    /// </summary>
    private static HitTestResult HitOnLine(PlacedLine line, float x, float localY)
    {
        if (line.Batch is not { } batch)
        {
            // 无批占位行盒（空块/空 bullet/空 todo）：命中即块内唯一光标位（0,0）。
            // 空段落原本没有行盒、点击不响应——这条路径让它获得光标落点。
            return new HitTestResult(true, line.BlockIndex, line.CharStart, false);
        }
        var hit = batch.HitTestChar(x - line.X, localY);
        if (hit is not { } h)
        {
            return new HitTestResult(true, line.BlockIndex, line.CharStart, false);
        }
        // HitTestChar 返回批文本流内偏移（与 GetCaretGeometry/GetCharRegions 同坐标系）；
        // 块内偏移 = 批内偏移（批的文本起点 == 行的 CharStart 由排版引擎保证）
        int charIndex = h.CharacterIndex + (h.IsTrailingHit ? 1 : 0);
        return new HitTestResult(true, line.BlockIndex, charIndex, h.IsTrailingHit);
    }

    public void Dispose()
    {
        var seen = new HashSet<ILineBatch>(ReferenceEqualityComparer.Instance);
        foreach (var line in Lines)
        {
            if (line.Batch is not null && seen.Add(line.Batch))
            {
                line.Batch.Dispose();
            }
        }
    }
}
