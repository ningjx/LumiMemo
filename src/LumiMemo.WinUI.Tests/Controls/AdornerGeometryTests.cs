using LumiMemo.WinUI.Controls;
using Xunit;

namespace LumiMemo.WinUI.Tests.Controls;

/// <summary><see cref="AdornerGeometry"/> 的单元测试：手柄位置/命中、拖拽尺寸与坐标口径自检。</summary>
public sealed class AdornerGeometryTests
{
    private static readonly RectD Image = new(100, 100, 200, 100);

    // ---- 手柄位置与命中 ----

    [Fact]
    public void 手柄中心_四角与四边_位置正确()
    {
        Assert.Equal(new PointD(100, 100), AdornerGeometry.HandleCenter(Image, ResizeHandle.TopLeft));
        Assert.Equal(new PointD(300, 100), AdornerGeometry.HandleCenter(Image, ResizeHandle.TopRight));
        Assert.Equal(new PointD(300, 200), AdornerGeometry.HandleCenter(Image, ResizeHandle.BottomRight));
        Assert.Equal(new PointD(100, 200), AdornerGeometry.HandleCenter(Image, ResizeHandle.BottomLeft));
        Assert.Equal(new PointD(200, 100), AdornerGeometry.HandleCenter(Image, ResizeHandle.Top));
        Assert.Equal(new PointD(300, 150), AdornerGeometry.HandleCenter(Image, ResizeHandle.Right));
        Assert.Equal(new PointD(200, 200), AdornerGeometry.HandleCenter(Image, ResizeHandle.Bottom));
        Assert.Equal(new PointD(100, 150), AdornerGeometry.HandleCenter(Image, ResizeHandle.Left));
    }

    [Fact]
    public void 命中_手柄附近_命中对应手柄()
    {
        Assert.Equal(ResizeHandle.TopLeft, AdornerGeometry.HitTest(Image, new PointD(104, 103)));
        Assert.Equal(ResizeHandle.Right, AdornerGeometry.HitTest(Image, new PointD(305, 150)));
    }

    [Fact]
    public void 命中_离所有手柄都远_未命中()
    {
        Assert.Equal(ResizeHandle.None, AdornerGeometry.HitTest(Image, new PointD(200, 150)));
    }

    [Fact]
    public void 命中_角落处_角优先于边()
    {
        // (300,100) 同时贴近 TopRight（距离 0）与 Top（距离 100）与 Right（100），应命中角。
        Assert.Equal(ResizeHandle.TopRight, AdornerGeometry.HitTest(Image, new PointD(299, 101)));
    }

    // ---- 拖拽 → 新尺寸（锚点 = 左上角） ----

    [Fact]
    public void 右下角拖拽_等比放大()
    {
        PointD size = AdornerGeometry.Resize(Image, ResizeHandle.BottomRight, new PointD(400, 200), 800);

        Assert.Equal(300, size.X, 3);
        Assert.Equal(150, size.Y, 3);
    }

    [Fact]
    public void 左上角拖拽_方向取反_仍等比()
    {
        // 图片右下 (300,200)。指针拖到 (0,50)：宽 = 300-0，高 = 200-50，两轴同为 1.5 倍。
        PointD size = AdornerGeometry.Resize(Image, ResizeHandle.TopLeft, new PointD(0, 50), 800);

        Assert.Equal(300, size.X, 3);
        Assert.Equal(150, size.Y, 3);
    }

    [Fact]
    public void 右边拖拽_只改宽()
    {
        PointD size = AdornerGeometry.Resize(Image, ResizeHandle.Right, new PointD(400, 999), 800);

        Assert.Equal(300, size.X, 3);
        Assert.Equal(100, size.Y, 3);
    }

    [Fact]
    public void 上边拖拽_只改高()
    {
        PointD size = AdornerGeometry.Resize(Image, ResizeHandle.Top, new PointD(999, 50), 800);

        Assert.Equal(200, size.X, 3);
        Assert.Equal(150, size.Y, 3);
    }

    [Fact]
    public void 缩到极小_两轴钳到最小边长()
    {
        PointD size = AdornerGeometry.Resize(Image, ResizeHandle.BottomRight, new PointD(110, 110), 800);

        Assert.Equal(48, size.X, 3);
        Assert.Equal(24, size.Y, 3);
    }

    [Fact]
    public void 放得过大_宽钳到编辑区宽()
    {
        PointD size = AdornerGeometry.Resize(Image, ResizeHandle.BottomRight, new PointD(10000, 100), 500);

        Assert.Equal(500, size.X, 3);
        Assert.Equal(250, size.Y, 3);
    }

    [Fact]
    public void 极端窄编辑区_优先保最小边长()
    {
        // 编辑区只剩 10px 宽：最大宽和最小边互相矛盾时，保最小边长（可读优先）。
        PointD size = AdornerGeometry.Resize(Image, ResizeHandle.Right, new PointD(50, 999), 10);

        Assert.Equal(AdornerGeometry.MinEdge, size.X, 3);
    }

    // ---- 坐标口径自检 ----

    [Fact]
    public void 坐标自检_在界内_按原值()
    {
        var rect = new RectD(100, 80, 200, 100);

        Assert.Equal(1, AdornerGeometry.ChooseScale(rect, 800, 600, 1.5), 3);
    }

    [Fact]
    public void 坐标自检_越界且除以dpi后落界内_按物理像素解释()
    {
        // 逻辑宽 800 的编辑区里，raw 右缘 1250 > 812 —— 典型"物理像素"特征（125% 缩放）。
        var rect = new RectD(500, 400, 350, 280);

        Assert.Equal(1 / 1.5, AdornerGeometry.ChooseScale(rect, 800, 600, 1.5), 3);
    }

    [Fact]
    public void 坐标自检_怎么解释都不在界内_回退原值()
    {
        var rect = new RectD(-5000, -5000, 100, 100);

        Assert.Equal(1, AdornerGeometry.ChooseScale(rect, 800, 600, 1.5), 3);
    }

    // ---- 坐标口径发现（引擎两点验证） ----

    [Fact]
    public void 候选口径_首个是原样_含窗口原点内边距与物理像素解释()
    {
        var candidates = AdornerGeometry.CandidateTransforms(1.5, 18, 16, new PointD(0, 40));

        Assert.Equal(new AdornerGeometry.CoordinateTransform(1, 0, 0), candidates[0]);
        Assert.Contains(new AdornerGeometry.CoordinateTransform(1, 0, -40), candidates);
        Assert.Contains(new AdornerGeometry.CoordinateTransform(1, 18, 16), candidates);
        Assert.Contains(new AdornerGeometry.CoordinateTransform(1.0 / 1.5, 0, 0), candidates);
    }

    [Fact]
    public void 候选口径_dpi为1且原点为零时没有重复项()
    {
        var candidates = AdornerGeometry.CandidateTransforms(1.0, 18, 16, new PointD(0, 0));

        Assert.Equal(candidates.Count, candidates.Distinct().Count());
    }

    /// <summary>造一个"图片逻辑矩形"的命中判据，模拟引擎回答。</summary>
    private static Func<PointD, bool> ImageAt(RectD image) =>
        point => point.X >= image.X && point.X <= image.Right
            && point.Y >= image.Y && point.Y <= image.Bottom;

    [Fact]
    public void 口径发现_错误缩放只能在左上蒙混_右下探针把它拒绝()
    {
        // 图片逻辑位置 (300,200)、尺寸 375×150；GetPoint 返回物理像素（×1.25）。
        var image = new RectD(300, 200, 375, 150);
        var rawTopLeft = new PointD(375, 250);

        var found = AdornerGeometry.DiscoverTransform(
            rawTopLeft, 375, 150, 1.25, 18, 16, new PointD(0, 0), ImageAt(image));

        // 原样候选的左上探针恰好落在图内（图片大），但右下探针在底缘外 → 拒绝；
        // 只有 ÷dpi 的解释两点都成立。
        Assert.Equal(new AdornerGeometry.CoordinateTransform(1 / 1.25, 0, 0), found);
    }

    [Fact]
    public void 口径发现_原点含标题栏偏移_选中整窗原点候选()
    {
        // raw = 图片逻辑位置 + 根偏移 (0,40)：典型的"坐标从整个窗口算起"。
        var image = new RectD(100, 240, 200, 100);
        var rawTopLeft = new PointD(100, 280);

        var found = AdornerGeometry.DiscoverTransform(
            rawTopLeft, 200, 100, 1.0, 18, 16, new PointD(0, 40), ImageAt(image));

        Assert.Equal(new AdornerGeometry.CoordinateTransform(1, 0, -40), found);
    }

    [Fact]
    public void 口径发现_原点在文本区内_选中加内边距候选()
    {
        // raw = 图片逻辑位置 − 内边距：原点在编辑区文本区（Padding 之内）。
        var image = new RectD(150, 120, 200, 100);
        var rawTopLeft = new PointD(132, 104);

        var found = AdornerGeometry.DiscoverTransform(
            rawTopLeft, 200, 100, 1.0, 18, 16, new PointD(0, 0), ImageAt(image));

        Assert.Equal(new AdornerGeometry.CoordinateTransform(1, 18, 16), found);
    }

    [Fact]
    public void 口径发现_全部候选都不成立_返回null() =>
        Assert.Null(AdornerGeometry.DiscoverTransform(
            new PointD(120, 90), 200, 100, 1.5, 18, 16, new PointD(0, 0), _ => false));

    [Fact]
    public void 口径发现_图片太小无法两点验证_直接返回null() =>
        Assert.Null(AdornerGeometry.DiscoverTransform(
            new PointD(120, 90), 5, 5, 1.5, 18, 16, new PointD(0, 0), _ => true));

    [Fact]
    public void 换算_缩放与偏移同时生效()
    {
        var transform = new AdornerGeometry.CoordinateTransform(0.5, 10, 20);

        Assert.Equal(new PointD(70, 65), transform.Apply(new PointD(120, 90)));
    }
}
