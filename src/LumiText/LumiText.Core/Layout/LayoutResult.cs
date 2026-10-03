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
