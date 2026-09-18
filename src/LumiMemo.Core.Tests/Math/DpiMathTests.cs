using LumiMemo.Core.Math;
using LumiMemo.Core.Models;
using Xunit;

namespace LumiMemo.Core.Tests.Math;

/// <summary>
/// <see cref="DpiMath"/> 的单元测试（§13.8）。
/// </summary>
/// <remarks>
/// 这里钉的是<strong>换算方向</strong>。方向写反不会报错、不会崩，只在混合 DPI 的机器上
/// 表现为「便签大小不对」，所以在 100% 单屏的开发机上完全测不出来，只能靠这些用例兜住。
/// </remarks>
public sealed class DpiMathTests
{
    // ---- Normalize ----

    [Fact]
    public void 零DPI归一到九十六()
    {
        // 手改过或早期版本的 layout.json 里 dpi 可能是 0，而 0 做分母会让窗口尺寸变成 NaN。
        Assert.Equal(96u, DpiMath.Normalize(0));
        Assert.Equal(96u, DpiMath.Normalize(96));
        Assert.Equal(144u, DpiMath.Normalize(144));
    }

    // ---- Scale：保存尺寸的缩放比 ----

    [Fact]
    public void 同DPI不缩放() => Assert.Equal(1.0, DpiMath.Scale(96, 96));

    [Fact]
    public void 一百到一百五_放大一倍半() => Assert.Equal(1.5, DpiMath.Scale(96, 144));

    [Fact]
    public void 一百五到一百_缩小到三分之二() =>
        Assert.Equal(2.0 / 3.0, DpiMath.Scale(144, 96), precision: 10);

    [Fact]
    public void 保存DPI为零时当作九十六() =>
        Assert.Equal(1.5, DpiMath.Scale(0, 144));

    [Fact]
    public void 当前DPI为零时当作九十六() =>
        Assert.Equal(96.0 / 144.0, DpiMath.Scale(144, 0), precision: 10);

    // ---- DipToPixelScale：界面常量的换算比 ----

    [Fact]
    public void 一百缩放时DIP与像素一比一() => Assert.Equal(1.0, DpiMath.DipToPixelScale(96));

    [Fact]
    public void 一百五缩放时DIP放大一倍半() =>
        Assert.Equal(1.5, DpiMath.DipToPixelScale(144));

    [Fact]
    public void 界面常量的换算只看当前显示器DPI() =>
        Assert.Equal(2.0, DpiMath.DipToPixelScale(192));

    /// <summary>
    /// 这两个方法必须在「保存 DPI 与当前 DPI 相同」时都等于 1，
    /// 否则会有人以为它们可以互换。
    /// </summary>
    [Fact]
    public void 两个换算方法在无缩放时都等于一()
    {
        Assert.Equal(1.0, DpiMath.Scale(96, 96));
        Assert.Equal(1.0, DpiMath.DipToPixelScale(96));
    }

    /// <summary>
    /// 这个用例专门钉住两者的分歧：保存于 150% 屏、看于 100% 屏时，两个系数相差一倍半。
    /// 折叠高度若误用 <see cref="DpiMath.Scale"/>，44 DIP 会被算成 29 像素。
    /// </summary>
    [Fact]
    public void 换屏时两者的分歧是可观测的()
    {
        Assert.Equal(2.0 / 3.0, DpiMath.Scale(144, 96), precision: 10);
        Assert.Equal(1.0, DpiMath.DipToPixelScale(96));
    }

    // ---- ScaleSize ----

    [Fact]
    public void 缩放尺寸时位置不动()
    {
        // 位置归夹取算法管。缩放顺带挪位置会让「先缩放再夹取」的输入不再可预测。
        var rect = new PixelRect(100, 200, 360, 420);

        PixelRect scaled = DpiMath.ScaleSize(rect, 1.5);

        Assert.Equal(100, scaled.X);
        Assert.Equal(200, scaled.Y);
        Assert.Equal(540, scaled.Width);
        Assert.Equal(630, scaled.Height);
    }

    // ---- DpiFromScale ----

    [Theory]
    [InlineData(1.0, 96u)]
    [InlineData(1.25, 120u)]
    [InlineData(1.5, 144u)]
    [InlineData(1.75, 168u)]
    [InlineData(2.0, 192u)]
    public void 从渲染缩放反推DPI(double m11, uint expected) =>
        Assert.Equal(expected, DpiMath.DpiFromScale(m11));

    [Fact]
    public void 还没接上渲染源时返回默认DPI()
    {
        // PresentationSource.FromVisual 在窗口 Show 之前是 null，此时读到的比例是 0。
        // 让它变成 0 DPI 流进后续的除法，整条恢复链路都会算出 NaN。
        Assert.Equal(96u, DpiMath.DpiFromScale(0));
        Assert.Equal(96u, DpiMath.DpiFromScale(-1));
        Assert.Equal(96u, DpiMath.DpiFromScale(double.NaN));
        Assert.Equal(96u, DpiMath.DpiFromScale(double.PositiveInfinity));
    }

    [Fact]
    public void 反推与正推互为逆运算()
    {
        foreach (uint dpi in (uint[])[96, 120, 144, 168, 192])
        {
            double scale = DpiMath.DipToPixelScale(dpi);

            Assert.Equal(dpi, DpiMath.DpiFromScale(scale));
        }
    }
}
