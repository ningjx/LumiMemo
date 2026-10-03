namespace LumiText.Core.Layout;

/// <summary>
/// 浮动图片的几何换算（Phase 3 打磨）：模型里的浮动矩形是「排版包络」——
/// 拖动 / 缩放 / 锚定 / 命中都用它；而<b>绘制</b>与<b>文字流向</b>用它的「视觉矩形」（内缩后的那张）。
/// </summary>
/// <remarks>
/// <para>
/// 内缩的意义：给图片与文字之间留出视觉呼吸，但这条带子<b>不算图片占用</b>——
/// 排版排除区取视觉矩形，所以一条差一点点放不下的行会优先吃掉缓冲带、
/// 贴到看得见的图片边上，而不是把字绕到图片另一侧（Phase 3 打磨第二版）。
/// </para>
/// <para>
/// 常量是视觉值，随便调：调大 = 文字可用空间更大、图片看起来离文字更远（图片本身也画得更小）。
/// 两处上限只防小图被吃掉。
/// </para>
/// </remarks>
public static class FloatGeometry
{
    /// <summary>★ 图片左右内缩（dip）：绘制位置的偏移量，同时也是文字可用的缓冲间距。</summary>
    public const float VisualInset = 4f;

    /// <summary>★ 图片上边界下移（dip）：与本行文字顶缘对齐（14dip 中文墨迹顶缘约低 3dip）。</summary>
    public const float VisualTopOffset = 3f;

    /// <summary>左右内缩上限：不超过图宽的 12%（小图别被间距吃掉）。</summary>
    public const float VisualInsetMaxRatio = 0.12f;

    /// <summary>上边界下移上限：不超过图高的 25%。</summary>
    public const float VisualTopOffsetMaxRatio = 0.25f;

    /// <summary>
    /// 浮动包络矩形 → 视觉矩形（<b>绘制</b>用，两侧等比内缩；不变形、不裁切）。
    /// 宽高非正时原样返回。
    /// </summary>
    public static LayoutRect VisualRect(LayoutRect reserved)
    {
        if (reserved.Width <= 0f || reserved.Height <= 0f)
        {
            return reserved;
        }
        float inset = Math.Min(VisualInset, reserved.Width * VisualInsetMaxRatio);
        return Inset(reserved, inset);
    }

    /// <summary>
    /// 浮动包络矩形 → <b>排版排除区</b>（文字流向用）：与 <see cref="VisualRect"/> 同源，
    /// 但<b>只收没有余量的那一侧</b>——那一侧本来就放不下字，再切一条几 dip 的缝
    /// 只会让引擎为它白排一次版（Phase 3 打磨实测：贴墙图片每行都多一次批创建）。
    /// </summary>
    /// <remarks>
    /// 排除区恒 ⊇ 视觉矩形：文字永远碰不到图片本体。纵向上取包络本身的 Y 区间
    /// （顶缘不跟着下移——否则首行会先在「图上方 3dip 的空带」里排一次版再重探，白建一批），
    /// 底缘取包络与视觉矩形的较大者（宽图等比内缩后视觉底缘可能略微更低）。
    /// </remarks>
    public static LayoutRect ExclusionRect(LayoutRect reserved, float contentWidth)
    {
        if (reserved.Width <= 0f || reserved.Height <= 0f)
        {
            return reserved;
        }
        float inset = Math.Min(VisualInset, reserved.Width * VisualInsetMaxRatio);
        float bottom = Math.Max(reserved.Bottom, VisualRect(reserved).Bottom);

        float leftRoom = reserved.X;
        float rightRoom = contentWidth - reserved.Right;
        float left = leftRoom > inset ? reserved.X + inset : reserved.X;
        float right = rightRoom > inset ? reserved.Right - inset : reserved.Right;
        return new LayoutRect(left, reserved.Y, Math.Max(0f, right - left), bottom - reserved.Y);
    }

    private static LayoutRect Inset(LayoutRect reserved, float inset)
    {
        float width = reserved.Width - (2f * inset);
        float height = reserved.Height * (width / reserved.Width); // 等比，避免变形
        float topOffset = Math.Min(VisualTopOffset, reserved.Height * VisualTopOffsetMaxRatio);
        return new LayoutRect(reserved.X + inset, reserved.Y + topOffset, width, height);
    }
}
