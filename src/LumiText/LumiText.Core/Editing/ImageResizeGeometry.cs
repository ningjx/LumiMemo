using LumiText.Core.Documents;
using LumiText.Core.Layout;

namespace LumiText.Core.Editing;

/// <summary>图片缩放的八个手柄（Phase 3 M4；语义移植自旧内核的缩放几何）。</summary>
public enum ImageHandle
{
    None,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
}

/// <summary>
/// 图片缩放手柄几何（纯函数，可无头测试）：手柄中心点、坐标命中、拖动 → 新尺寸。
/// 交互契约沿用旧产品（Phase 2 验收过的缩放手感，旧内核已在 Phase 4 退役）：
/// 四角等比（取两轴相对变化的较大者，保证图片不缩到指针内侧）、四边只动单轴；
/// 最小边长 24dip、宽不超过内容区宽。
/// </summary>
public static class ImageResizeGeometry
{
    /// <summary>图片可缩到的最小边长（dip，与旧产品一致）。</summary>
    public const float MinEdge = 24f;

    /// <summary>手柄命中半径（比可视手柄略大，好点）。</summary>
    public const float HandleHitRadius = 11f;

    /// <summary>手柄遍历顺序：四角优先于四边（半径内角优先）。</summary>
    public static readonly ImageHandle[] AllHandles =
    [
        ImageHandle.TopLeft, ImageHandle.TopRight, ImageHandle.BottomRight, ImageHandle.BottomLeft,
        ImageHandle.Top, ImageHandle.Right, ImageHandle.Bottom, ImageHandle.Left,
    ];

    /// <summary>是否四角手柄（相对四边手柄）。缩放手柄的视觉分派用（角=圆弧、边=线段）。</summary>
    public static bool IsCorner(ImageHandle handle) =>
        handle is ImageHandle.TopLeft or ImageHandle.TopRight
            or ImageHandle.BottomRight or ImageHandle.BottomLeft;

    /// <summary>手柄中心的文档坐标。</summary>
    public static (float X, float Y) HandleCenter(LayoutRect image, ImageHandle handle) => handle switch
    {
        ImageHandle.TopLeft => (image.X, image.Y),
        ImageHandle.Top => (image.X + (image.Width / 2f), image.Y),
        ImageHandle.TopRight => (image.Right, image.Y),
        ImageHandle.Right => (image.Right, image.Y + (image.Height / 2f)),
        ImageHandle.BottomRight => (image.Right, image.Bottom),
        ImageHandle.Bottom => (image.X + (image.Width / 2f), image.Bottom),
        ImageHandle.BottomLeft => (image.X, image.Bottom),
        ImageHandle.Left => (image.X, image.Y + (image.Height / 2f)),
        _ => (float.NaN, float.NaN),
    };

    /// <summary>坐标命中：离哪个手柄最近（半径内），没有则 <see cref="ImageHandle.None"/>。</summary>
    public static ImageHandle HitTest(LayoutRect image, float x, float y,
        float radius = HandleHitRadius)
    {
        ImageHandle best = ImageHandle.None;
        float bestDistance = radius;
        foreach (ImageHandle handle in AllHandles)
        {
            (float cx, float cy) = HandleCenter(image, handle);
            float dx = cx - x;
            float dy = cy - y;
            float distance = MathF.Sqrt((dx * dx) + (dy * dy));
            if (distance < bestDistance)
            {
                best = handle;
                bestDistance = distance;
            }
        }
        return best;
    }

    /// <summary>
    /// 拖动手柄 → 新矩形（dip）：<b>对边/对角固定</b>——左/上侧手柄动左/上边缘（右/下边缘不动），
    /// 右/下侧手柄动右/下边缘（左/上边缘不动）。四角等比（取两轴变化较大者）、四边单轴；
    /// 两边不小于 <see cref="MinEdge"/>，宽不超过 <paramref name="maxWidth"/>。
    /// </summary>
    /// <remarks>
    /// 这是「指针直给」的自由矩形：图片的真实落位由浮动锚点决定（X 随锚字符、Y 随锚字符所在行），
    /// 因此左/上侧手柄松手时要按新左上角重算锚点（编辑器侧，见 LumiEditor 的缩放松手）。
    /// </remarks>
    public static LayoutRect Resize(LayoutRect image, ImageHandle handle,
        float pointerX, float pointerY, float maxWidth)
    {
        bool leftSide = handle is ImageHandle.TopLeft or ImageHandle.Left or ImageHandle.BottomLeft;
        bool topSide = handle is ImageHandle.TopLeft or ImageHandle.Top or ImageHandle.TopRight;
        bool corner = handle is ImageHandle.TopLeft or ImageHandle.TopRight
            or ImageHandle.BottomLeft or ImageHandle.BottomRight;

        float targetWidth = leftSide ? image.Right - pointerX : pointerX - image.X;
        float targetHeight = topSide ? image.Bottom - pointerY : pointerY - image.Y;

        float width;
        float height;
        if (corner)
        {
            float scaleX = targetWidth / image.Width;
            float scaleY = targetHeight / image.Height;
            float scale = MathF.Max(scaleX, scaleY);

            float minScale = MathF.Max(MinEdge / image.Width, MinEdge / image.Height);
            float maxScale = maxWidth / image.Width;
            scale = maxScale < minScale ? minScale : Math.Clamp(scale, minScale, maxScale);
            width = image.Width * scale;
            height = image.Height * scale;
        }
        else if (handle is ImageHandle.Left or ImageHandle.Right)
        {
            width = Math.Clamp(targetWidth, MinEdge, Math.Max(MinEdge, maxWidth));
            height = image.Height;
        }
        else
        {
            width = image.Width;
            height = MathF.Max(targetHeight, MinEdge);
        }

        // 对边固定：左/上侧手柄 = 右/下边缘钉住，矩形往左上长；其余手柄 = 左/上边缘钉住
        float left = leftSide ? image.Right - width : image.X;
        float top = topSide ? image.Bottom - height : image.Y;
        return new LayoutRect(left, top, width, height);
    }

    /// <summary>
    /// 缩放松手的最终落位（Phase 3 M4，左/上侧手柄专用）：自由矩形左上角 → 锚点（与拖动落点
    /// 同一规则）→ 锚定后的矩形——锚点决定图片落位，<b>右下边缘钉回自由矩形的位置</b>
    /// （重锚会把左上角吸到字符/行上，尺寸要跟着让，否则右下边缘会被挪动）。
    /// </summary>
    /// <param name="naturalLayout">「去掉这张图」的自然版面（图片挤开文字后锚点会差一格）。</param>
    /// <param name="freeRect">指针直给的自由矩形（对边固定）。</param>
    /// <param name="contentWidth">内容区宽度（dip）。</param>
    /// <returns>锚点与最终矩形；锚点解析不出来（全文无文本行盒）时 null（调用方退回只改尺寸）。</returns>
    public static (FloatAnchor Anchor, LayoutRect Rect)? ResolveAnchoredResize(
        LayoutResult naturalLayout, LayoutRect freeRect, float contentWidth)
    {
        ArgumentNullException.ThrowIfNull(naturalLayout);
        var hit = naturalLayout.HitTestFloatAnchor(freeRect);
        if (!hit.Found)
        {
            return null;
        }
        var anchor = new FloatAnchor(hit.BlockIndex, hit.CharIndex);
        if (!naturalLayout.TryResolveAnchorTopLeft(anchor, anchorToChar: true, FloatSide.Right,
                freeRect.Width, contentWidth, out float x, out float y))
        {
            return null;
        }
        return (anchor, new LayoutRect(x, y,
            Math.Max(MinEdge, freeRect.Right - x),
            Math.Max(MinEdge, freeRect.Bottom - y)));
    }
}
