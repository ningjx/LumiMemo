namespace LumiMemo.Core.Models;

/// <summary>
/// 屏幕坐标系下的一个矩形，单位是<strong>物理像素</strong>（§8.3、§13.8）。
/// </summary>
/// <remarks>
/// <para>
/// 全项目中所有「位置与尺寸」都用本类型表达，不用 <c>System.Windows.Rect</c>：
/// 后者住在 WPF 程序集里，而 Core 不能引用任何 UI 框架（§4.1）。
/// </para>
/// <para>
/// <strong>坐标原点不是主显示器的左上角，而是虚拟屏幕的左上角</strong>。多显示器时
/// 副屏可以位于主屏的左侧或上方，此时 <see cref="X"/> / <see cref="Y"/> 会是负数——
/// 这是正常值，任何把它当「非法输入」拦下来的代码都是错的（§13.8）。
/// </para>
/// </remarks>
/// <param name="X">左边缘到虚拟屏幕原点的横向距离，物理像素。</param>
/// <param name="Y">上边缘到虚拟屏幕原点的纵向距离，物理像素。</param>
/// <param name="Width">宽度，物理像素。</param>
/// <param name="Height">高度，物理像素。</param>
public readonly record struct PixelRect(double X, double Y, double Width, double Height)
{
    /// <summary>右边缘的横坐标。注意这是「左 + 宽」，不是「不含右边缘」。</summary>
    public double Right => X + Width;

    /// <summary>下边缘的纵坐标。</summary>
    public double Bottom => Y + Height;

    /// <summary>中心的横坐标。恢复算法用它去判断窗口落在哪台显示器上（§13.8 第 1 步）。</summary>
    public double CenterX => X + (Width / 2);

    /// <summary>中心的纵坐标。</summary>
    public double CenterY => Y + (Height / 2);
}
