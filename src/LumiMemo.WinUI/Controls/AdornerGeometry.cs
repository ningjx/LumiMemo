namespace LumiMemo.WinUI.Controls;

/// <summary>图片缩放的八个手柄。</summary>
internal enum ResizeHandle
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

/// <summary>与 UI 框架无关的点（覆盖层坐标，逻辑像素）。</summary>
internal readonly record struct PointD(double X, double Y);

/// <summary>与 UI 框架无关的矩形（覆盖层坐标，逻辑像素）。</summary>
internal readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;
}

/// <summary>装饰器几何：手柄位置/命中、拖拽→新尺寸、原始坐标到覆盖层的换算。</summary>
/// <remarks>
/// 纯函数，无 UI 依赖，全部可单测。拖拽的锚点是图片当前<strong>左上角</strong>：
/// 内嵌图片在行里的天然锚点就是从左上向右下展开（提交后的真实布局也如此），
/// 所以预览和结果一致；左/上手柄的拖拽方向相应取反。
/// </remarks>
internal static class AdornerGeometry
{
    /// <summary>图片可缩到的最小边长（px）。</summary>
    public const double MinEdge = 24;

    /// <summary>手柄命中半径，比可视手柄略大，好点。</summary>
    public const double HandleHitRadius = 11;

    /// <summary>手柄遍历顺序：四角优先于四边（≤ 半径时角优先）。</summary>
    public static readonly ResizeHandle[] AllHandles =
    [
        ResizeHandle.TopLeft, ResizeHandle.TopRight, ResizeHandle.BottomRight, ResizeHandle.BottomLeft,
        ResizeHandle.Top, ResizeHandle.Right, ResizeHandle.Bottom, ResizeHandle.Left,
    ];

    /// <summary>手柄中心的覆盖层坐标。</summary>
    public static PointD HandleCenter(RectD image, ResizeHandle handle) => handle switch
    {
        ResizeHandle.TopLeft => new PointD(image.X, image.Y),
        ResizeHandle.Top => new PointD(image.X + image.Width / 2, image.Y),
        ResizeHandle.TopRight => new PointD(image.Right, image.Y),
        ResizeHandle.Right => new PointD(image.Right, image.Y + image.Height / 2),
        ResizeHandle.BottomRight => new PointD(image.Right, image.Bottom),
        ResizeHandle.Bottom => new PointD(image.X + image.Width / 2, image.Bottom),
        ResizeHandle.BottomLeft => new PointD(image.X, image.Bottom),
        ResizeHandle.Left => new PointD(image.X, image.Y + image.Height / 2),
        _ => new PointD(double.NaN, double.NaN),
    };

    /// <summary>命中测试：离哪个手柄最近（半径内），没有则 <see cref="ResizeHandle.None"/>。</summary>
    public static ResizeHandle HitTest(RectD image, PointD point, double radius = HandleHitRadius)
    {
        ResizeHandle best = ResizeHandle.None;
        double bestDistance = radius;

        foreach (ResizeHandle handle in AllHandles)
        {
            PointD center = HandleCenter(image, handle);
            double dx = center.X - point.X;
            double dy = center.Y - point.Y;
            double distance = Math.Sqrt((dx * dx) + (dy * dy));
            if (distance < bestDistance)
            {
                best = handle;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// 拖拽手柄 → 新尺寸（px）。
    /// 四角等比（取两轴距离相对变化的较大者，保证图片不缩到指针内侧）；
    /// 四边只动单轴。钳制：两边最小 <see cref="MinEdge"/>，宽不超过 <paramref name="maxWidth"/>。
    /// </summary>
    public static PointD Resize(RectD image, ResizeHandle handle, PointD pointer, double maxWidth)
    {
        bool leftSide = handle is ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft;
        bool topSide = handle is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight;
        bool corner = handle is ResizeHandle.TopLeft or ResizeHandle.TopRight
            or ResizeHandle.BottomLeft or ResizeHandle.BottomRight;

        double targetWidth = leftSide ? image.Right - pointer.X : pointer.X - image.X;
        double targetHeight = topSide ? image.Bottom - pointer.Y : pointer.Y - image.Y;

        if (corner)
        {
            double scaleX = targetWidth / image.Width;
            double scaleY = targetHeight / image.Height;
            double scale = Math.Max(scaleX, scaleY);

            double minScale = Math.Max(MinEdge / image.Width, MinEdge / image.Height);
            double maxScale = maxWidth / image.Width;
            scale = maxScale < minScale ? minScale : Math.Clamp(scale, minScale, maxScale);

            return new PointD(image.Width * scale, image.Height * scale);
        }

        if (handle is ResizeHandle.Left or ResizeHandle.Right)
        {
            return new PointD(Math.Clamp(targetWidth, MinEdge, Math.Max(MinEdge, maxWidth)), image.Height);
        }

        return new PointD(image.Width, Math.Max(targetHeight, MinEdge));
    }

    /// <summary>GetPoint 原始坐标 → 覆盖层坐标的一种候选换算：<c>p × Scale + (OffsetX, OffsetY)</c>。</summary>
    public readonly record struct CoordinateTransform(double Scale, double OffsetX, double OffsetY)
    {
        public PointD Apply(PointD point) =>
            new((point.X * Scale) + OffsetX, (point.Y * Scale) + OffsetY);
    }

    /// <summary>
    /// 枚举可能的坐标口径：原样、原点在文本区内（差一个编辑区 Padding）、单位是物理像素
    /// （差一个显示器缩放），以及它们的组合。这里只提供候选，真正的口径由引擎自己回答
    /// （见 <see cref="DiscoverTransform"/>）——因此换机器、换缩放都不用改任何数字。
    /// </summary>
    public static IReadOnlyList<CoordinateTransform> CandidateTransforms(
        double dpiScale, double paddingX, double paddingY)
    {
        double inverse = dpiScale > 0.01 ? 1.0 / dpiScale : 1.0;
        var candidates = new List<CoordinateTransform>();

        foreach (double scale in new[] { 1.0, inverse, dpiScale })
        {
            foreach ((double offsetX, double offsetY) in new (double, double)[]
            {
                (0, 0), (paddingX, paddingY), (-paddingX, -paddingY),
            })
            {
                var candidate = new CoordinateTransform(scale, offsetX, offsetY);
                if (!candidates.Contains(candidate))
                {
                    candidates.Add(candidate);
                }
            }
        }

        return candidates;
    }

    /// <summary>
    /// 用引擎自己的命中映射验证候选口径：把某个候选换算后的点（从图片左上角往里探 3px，
    /// 避开边界像素的临界模糊）喂给引擎，问它落在哪个字符；第一个"答案确实落在图片上"的
    /// 候选就是真口径。全部候选都不成立（例如 GetRangeFromPoint 不可用）时返回 null。
    /// </summary>
    public static CoordinateTransform? DiscoverTransform(
        PointD rawTopLeft,
        double dpiScale,
        double paddingX,
        double paddingY,
        Func<PointD, bool> engineSaysOnImage)
    {
        foreach (CoordinateTransform candidate in CandidateTransforms(dpiScale, paddingX, paddingY))
        {
            PointD corner = candidate.Apply(rawTopLeft);
            if (engineSaysOnImage(new PointD(corner.X + 3, corner.Y + 3)))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 把 <c>ITextRange.GetPoint</c> 的原始坐标换算到覆盖层坐标的缩放系数（兜底用）。
    /// GetPoint 的坐标口径（逻辑像素还是物理像素）没有文档保证，这里用
    /// 「矩形是否落在编辑区边界内」自检：先按 1 试，出界且除法能救回来就按 ÷dpi 解释。
    /// 主路径不靠它——靠 <see cref="DiscoverTransform"/> 的引擎验证。
    /// </summary>
    public static double ChooseScale(RectD rawRect, double editorWidth, double editorHeight, double dpiScale)
    {
        const double margin = 12;

        bool Fits(double scale) =>
            rawRect.X * scale >= -margin
            && rawRect.Y * scale >= -margin
            && rawRect.Right * scale <= editorWidth + margin
            && rawRect.Bottom * scale <= editorHeight + margin;

        if (Fits(1))
        {
            return 1;
        }

        // 返回物理像素且比逻辑像素"数值更大"是唯一可自检、可自动救回的口径差异；
        // 原点平移类的偏差没有信号可推断，留给真机校准常量。
        if (dpiScale > 1.01 && Fits(1 / dpiScale))
        {
            return 1 / dpiScale;
        }

        return 1;
    }
}
