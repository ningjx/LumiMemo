namespace LumiText.Core.Layout;

/// <summary>
/// 排版坐标矩形（dip，文档坐标系，Y 向下）。自定义结构而非 Windows.Foundation.Rect，
/// 是为了让 Core 保持 <c>net10.0</c> 零依赖、可在任意运行时无头测试。
/// </summary>
public readonly record struct LayoutRect(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;

    public float Bottom => Y + Height;

    public bool Contains(float x, float y) => x >= X && x < Right && y >= Y && y < Bottom;

    /// <summary>与垂直区间 [yTop, yBottom) 是否相交。</summary>
    public bool IntersectsVertically(float yTop, float yBottom) => Y < yBottom && Bottom > yTop;

    public LayoutRect MovedTo(float x, float y) => this with { X = x, Y = y };
}
