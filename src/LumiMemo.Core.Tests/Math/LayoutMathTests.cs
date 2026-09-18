using LumiMemo.Core.Math;
using LumiMemo.Core.Models;
using LumiMemo.Core.Tests.TestDoubles;
using Xunit;

namespace LumiMemo.Core.Tests.Math;

/// <summary>
/// <see cref="LayoutMath"/> 的单元测试，逐条对应 §13.8 的恢复算法与夹取规则。
/// </summary>
/// <remarks>
/// 这些用例是「便签关掉重开还在原位」这条核心承诺的地基。它们全是纯函数测试，
/// 不碰 Win32、不碰窗口，因此能覆盖到手工测不出来的边界：
/// 副屏在主屏左侧时的负坐标、两台显示器接缝上的判定、折叠态跨 DPI 的换算。
/// </remarks>
public sealed class LayoutMathTests
{
    private const string PrimaryId = @"\\.\DISPLAY1";
    private const string SecondaryId = @"\\.\DISPLAY2";

    /// <summary>100% 显示器上展开态的最小尺寸，物理像素（§15.1）。DIP 与像素在这里是 1:1。</summary>
    private const double MinWidthPx = LayoutMath.MinimumWidth;

    /// <inheritdoc cref="MinWidthPx"/>
    private const double MinHeightPx = LayoutMath.MinimumExpandedHeight;

    // ================= Clamp：工作区夹取 =================

    [Fact]
    public void 尺寸超过工作区时缩到工作区大小()
    {
        // 副屏换成竖屏后，横着放不下的便签必须还能用，而不是挂在屏幕外。
        var workArea = new PixelRect(0, 0, 1920, 1080);

        PixelRect clamped = LayoutMath.Clamp(new PixelRect(0, 0, 3000, 2000), workArea, MinWidthPx, MinHeightPx);

        Assert.Equal(1920, clamped.Width);
        Assert.Equal(1080, clamped.Height);
    }

    [Fact]
    public void 窗口被推到左边之外_拉回工作区左边缘()
    {
        var workArea = new PixelRect(0, 0, 1920, 1080);

        // 左上角已经跑到屏幕外很远，可见部分不足 80px。
        PixelRect clamped = LayoutMath.Clamp(new PixelRect(-5000, 100, 360, 420), workArea, MinWidthPx, MinHeightPx);

        Assert.Equal(0, clamped.X);
        Assert.Equal(100, clamped.Y);
    }

    [Fact]
    public void 窗口被推到右边之外_把右边缘贴到工作区右侧()
    {
        var workArea = new PixelRect(0, 0, 1920, 1080);

        PixelRect clamped = LayoutMath.Clamp(new PixelRect(1900, 100, 360, 420), workArea, MinWidthPx, MinHeightPx);

        Assert.Equal(1560, clamped.X);
    }

    [Fact]
    public void 窗口被推到上边之外_拉回工作区上边缘()
    {
        var workArea = new PixelRect(0, 0, 1920, 1080);

        PixelRect clamped = LayoutMath.Clamp(new PixelRect(100, -50, 360, 420), workArea, MinWidthPx, MinHeightPx);

        Assert.Equal(0, clamped.Y);
    }

    [Fact]
    public void 窗口被推到下边之外_把下边缘贴到工作区下侧()
    {
        var workArea = new PixelRect(0, 0, 1920, 1080);

        PixelRect clamped = LayoutMath.Clamp(new PixelRect(100, 1070, 360, 420), workArea, MinWidthPx, MinHeightPx);

        Assert.Equal(660, clamped.Y);
    }

    [Fact]
    public void 完全在工作区内_一个像素都不动()
    {
        var workArea = new PixelRect(0, 0, 1920, 1080);
        var rect = new PixelRect(100, 200, 360, 420);

        Assert.Equal(rect, LayoutMath.Clamp(rect, workArea, MinWidthPx, MinHeightPx));
    }

    [Fact]
    public void 只露出一小部分但仍然够抓_不做修正()
    {
        // 夹取只保证「抓得住」，不保证「全在屏幕里」。用户可能就是故意把便签挂一半在外面。
        var workArea = new PixelRect(0, 0, 1920, 1080);
        var rect = new PixelRect(-100, 200, 360, 420);

        Assert.Equal(rect, LayoutMath.Clamp(rect, workArea, MinWidthPx, MinHeightPx));
    }

    [Fact]
    public void 副屏在主屏左侧时_负坐标是合法值不是越界()
    {
        // 这是最容易写错的一条：把 x < 0 一律当「跑到屏幕外」会让左上方的副屏完全没法用。
        var workArea = new PixelRect(-1920, 0, 1920, 1080);
        var rect = new PixelRect(-1000, 100, 360, 420);

        Assert.Equal(rect, LayoutMath.Clamp(rect, workArea, MinWidthPx, MinHeightPx));
    }

    // ---- 最小尺寸守卫 ----

    [Fact]
    public void 宽度小于最小值_被撑到最小宽度()
    {
        // 手改坏的 layout.json 里 width: 0 既不越界也不超工作区，
        // 四条位置修正一条都不会命中，于是恢复出一个看不见也抓不到的窗口。
        var workArea = new PixelRect(0, 0, 1920, 1080);

        PixelRect clamped = LayoutMath.Clamp(new PixelRect(100, 200, 0, 420), workArea, MinWidthPx, MinHeightPx);

        Assert.Equal(240, clamped.Width);
        Assert.Equal(100, clamped.X);
    }

    [Fact]
    public void 高度小于最小值_被撑到最小高度()
    {
        var workArea = new PixelRect(0, 0, 1920, 1080);

        PixelRect clamped = LayoutMath.Clamp(new PixelRect(100, 200, 360, 0), workArea, MinWidthPx, MinHeightPx);

        Assert.Equal(120, clamped.Height);
        Assert.Equal(200, clamped.Y);
    }

    [Fact]
    public void 最小尺寸比工作区还大时_以工作区为准()
    {
        // 顺序不能反：先撑到最小值再按工作区封顶，而不是反过来。
        // 反过来的话 240 DIP 的窗口落在 200px 宽的工作区里会变成 240 宽，反而超出屏幕。
        var workArea = new PixelRect(0, 0, 200, 100);

        PixelRect clamped = LayoutMath.Clamp(new PixelRect(0, 0, 50, 50), workArea, MinWidthPx, MinHeightPx);

        Assert.Equal(200, clamped.Width);
        Assert.Equal(100, clamped.Height);
    }

    [Fact]
    public void 尺寸本来就够大_最小值不参与运算()
    {
        var workArea = new PixelRect(0, 0, 1920, 1080);
        var rect = new PixelRect(100, 200, 360, 420);

        Assert.Equal(rect, LayoutMath.Clamp(rect, workArea, MinWidthPx, MinHeightPx));
    }

    // ================= Cascade：层叠摆放 =================

    [Fact]
    public void 层叠第一张偏移二十四像素()
    {
        PixelRect placed = LayoutMath.Cascade(new PixelRect(0, 0, 1920, 1080), 0, 360, 420);

        Assert.Equal(new PixelRect(24, 24, 360, 420), placed);
    }

    [Fact]
    public void 层叠第二张多偏二十八像素()
    {
        PixelRect placed = LayoutMath.Cascade(new PixelRect(0, 0, 1920, 1080), 1, 360, 420);

        Assert.Equal(new PixelRect(52, 52, 360, 420), placed);
    }

    [Fact]
    public void 层叠第六张仍在递增段内()
    {
        PixelRect placed = LayoutMath.Cascade(new PixelRect(0, 0, 1920, 1080), 5, 360, 420);

        Assert.Equal(24 + (5 * 28), placed.X);
    }

    [Fact]
    public void 层叠第七张回到起点并多偏八像素()
    {
        // 不无限递增是因为十几张之后偏移就大到把便签推出屏幕了。
        PixelRect placed = LayoutMath.Cascade(new PixelRect(0, 0, 1920, 1080), 6, 360, 420);

        Assert.Equal(24 + 8, placed.X);
        Assert.Equal(24 + 8, placed.Y);
    }

    [Fact]
    public void 层叠绕第二圈时偏移继续累加()
    {
        PixelRect placed = LayoutMath.Cascade(new PixelRect(0, 0, 1920, 1080), 12, 360, 420);

        Assert.Equal(24 + 16, placed.X);
    }

    [Fact]
    public void 层叠从工作区左上角起算_而不是虚拟屏幕原点()
    {
        PixelRect placed = LayoutMath.Cascade(new PixelRect(1920, 0, 2560, 1400), 0, 360, 420);

        Assert.Equal(1920 + 24, placed.X);
        Assert.Equal(24, placed.Y);
    }

    [Fact]
    public void 层叠序号为负_抛异常() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => LayoutMath.Cascade(new PixelRect(0, 0, 1920, 1080), -1, 360, 420));

    // ================= CollapsedHeight =================

    [Fact]
    public void 显示状态条时折叠高度是四十四() =>
        Assert.Equal(44, LayoutMath.CollapsedHeight(showStatusBar: true));

    [Fact]
    public void 隐藏状态条时折叠高度只剩标题条() =>
        Assert.Equal(LayoutMath.TitleBarHeight, LayoutMath.CollapsedHeight(showStatusBar: false));

    [Fact]
    public void 折叠高度不超过展开态的最小高度() =>
        Assert.True(LayoutMath.CollapsedHeight(showStatusBar: true) < LayoutMath.MinimumExpandedHeight);

    // ================= FindDisplayContaining =================

    [Fact]
    public void 点落在主屏上_命中主屏()
    {
        List<DisplaySnapshot> displays = Displays.PrimaryAndHighDpiSecondary();

        DisplaySnapshot? found = LayoutMath.FindDisplayContaining(displays, 500, 500);

        Assert.Equal(PrimaryId, found?.DeviceId);
    }

    [Fact]
    public void 点落在副屏上_命中副屏()
    {
        List<DisplaySnapshot> displays = Displays.PrimaryAndHighDpiSecondary();

        DisplaySnapshot? found = LayoutMath.FindDisplayContaining(displays, 3000, 500);

        Assert.Equal(SecondaryId, found?.DeviceId);
    }

    [Fact]
    public void 两台显示器的接缝上_只命中右边那一台()
    {
        // 左闭右开。闭区间会让接缝上的点同时属于两台显示器，
        // 于是同一份布局在不同代码路径下可能落到不同的屏上。
        List<DisplaySnapshot> displays = Displays.PrimaryAndHighDpiSecondary();

        Assert.Equal(SecondaryId, LayoutMath.FindDisplayContaining(displays, 1920, 500)?.DeviceId);
    }

    [Fact]
    public void 点落在所有显示器之外_返回空()
    {
        // 这条就是「保存时所在的显示器已被拔掉」的探测器。若照 MonitorFromPoint 的
        // DEFAULTTONEAREST 语义返回最近的一台，调用方就分不清「本来就在主屏」和「屏没了」。
        List<DisplaySnapshot> displays = Displays.PrimaryAndHighDpiSecondary();

        Assert.Null(LayoutMath.FindDisplayContaining(displays, 9000, 500));
        Assert.Null(LayoutMath.FindDisplayContaining(displays, 500, -400));
    }

    [Fact]
    public void 一台显示器都没有时_返回空() =>
        Assert.Null(LayoutMath.FindDisplayContaining([], 0, 0));

    // ================= Restore：完整恢复算法 =================

    [Fact]
    public void 同屏同DPI_位置尺寸原样恢复且不标记为已修正()
    {
        WindowPlacement placement = LayoutMath.Restore(
            NewLayout(x: 100, y: 200, width: 360, height: 420),
            [Displays.Create(PrimaryId)],
            cascadeIndex: 0,
            showStatusBar: true);

        Assert.Equal(new PixelRect(100, 200, 360, 420), placement.Bounds);
        Assert.Equal(96u, placement.Dpi);
        Assert.Equal(PrimaryId, placement.DisplayId);
        Assert.False(placement.WasAdjusted);
    }

    [Fact]
    public void 跨DPI拖到高缩放屏_尺寸按比例放大且标记为已修正()
    {
        // 100% 屏上存的 360 宽，150% 屏上要变成 540，视觉宽度才一致。
        List<DisplaySnapshot> displays = Displays.PrimaryAndHighDpiSecondary();
        NoteLayout saved = NewLayout(x: 2000, y: 100, width: 360, height: 420, dpi: 96);

        WindowPlacement placement = LayoutMath.Restore(saved, displays, cascadeIndex: 0, showStatusBar: true);

        Assert.Equal(SecondaryId, placement.DisplayId);
        Assert.Equal(144u, placement.Dpi);
        Assert.Equal(540, placement.Bounds.Width);
        Assert.Equal(630, placement.Bounds.Height);
        Assert.Equal(2000, placement.Bounds.X);
        Assert.True(placement.WasAdjusted);
    }

    [Fact]
    public void 从高缩放屏回到低缩放屏_尺寸按比例缩小()
    {
        List<DisplaySnapshot> displays = Displays.PrimaryAndHighDpiSecondary();
        NoteLayout saved = NewLayout(x: 2000, y: 100, width: 540, height: 630, dpi: 144);

        // 目标仍是副屏（150%），所以这里换成主屏上的一张便签来验证反方向。
        NoteLayout onPrimary = NewLayout(x: 100, y: 100, width: 540, height: 630, dpi: 144);

        WindowPlacement placement = LayoutMath.Restore(onPrimary, displays, cascadeIndex: 0, showStatusBar: true);

        Assert.Equal(PrimaryId, placement.DisplayId);
        Assert.Equal(360, placement.Bounds.Width);
        Assert.Equal(420, placement.Bounds.Height);

        // 副屏方向同样还原成保存值，说明两个方向都通。
        Assert.Equal(540, LayoutMath.Restore(saved, displays, cascadeIndex: 0, showStatusBar: true).Bounds.Width);
    }

    [Fact]
    public void 展开高度取自ExpandedHeight而不是Height()
    {
        // 折叠过的便签 Height 里存的是折叠高度，拿它恢复展开态会让窗口一开就是一条。
        NoteLayout saved = NewLayout(x: 100, y: 100, width: 360, height: 44);

        saved.ExpandedHeight = 420;

        WindowPlacement placement = LayoutMath.Restore(
            saved,
            [Displays.Create(PrimaryId)],
            cascadeIndex: 0,
            showStatusBar: true);

        Assert.Equal(420, placement.Bounds.Height);
    }

    [Fact]
    public void 折叠态的便签_高度换成折叠高度()
    {
        NoteLayout saved = NewLayout(x: 100, y: 100, width: 360, height: 420);

        saved.IsCollapsed = true;

        WindowPlacement placement = LayoutMath.Restore(
            saved,
            [Displays.Create(PrimaryId)],
            cascadeIndex: 0,
            showStatusBar: true);

        Assert.Equal(44, placement.Bounds.Height);
    }

    [Fact]
    public void 折叠态跨DPI_高度按当前显示器的DIP换算而不是按保存DPI的比值()
    {
        // 这是本项目最容易算错的一处。保存时 150%、当前 100%：
        //   正确：44 DIP × (96/96)  = 44     ← 折叠高度是界面常量
        //   错误：44      × (96/144) = 29.33  ← 误用「保存尺寸」的缩放比，标题条会被压扁
        NoteLayout saved = NewLayout(x: 100, y: 100, width: 360, height: 44, dpi: 144);

        saved.IsCollapsed = true;
        saved.ExpandedHeight = 420;

        WindowPlacement placement = LayoutMath.Restore(
            saved,
            [Displays.Create(PrimaryId, dpi: 96)],
            cascadeIndex: 0,
            showStatusBar: true);

        Assert.Equal(44, placement.Bounds.Height);
    }

    [Fact]
    public void 折叠态在高缩放屏上_折叠高度跟着放大()
    {
        NoteLayout saved = NewLayout(x: 2000, y: 100, width: 360, height: 44, dpi: 96);

        saved.IsCollapsed = true;
        saved.ExpandedHeight = 420;

        WindowPlacement placement = LayoutMath.Restore(
            saved,
            Displays.PrimaryAndHighDpiSecondary(),
            cascadeIndex: 0,
            showStatusBar: true);

        // 44 DIP 在 150% 屏上是 66 物理像素，视觉高度与 100% 屏上的 44 一致。
        Assert.Equal(66, placement.Bounds.Height);
    }

    [Fact]
    public void 隐藏状态条时_折叠高度退回标题条()
    {
        NoteLayout saved = NewLayout(x: 100, y: 100, width: 360, height: 420);

        saved.IsCollapsed = true;

        WindowPlacement placement = LayoutMath.Restore(
            saved,
            [Displays.Create(PrimaryId)],
            cascadeIndex: 0,
            showStatusBar: false);

        Assert.Equal(36, placement.Bounds.Height);
    }

    [Fact]
    public void 保存DPI为零_按九十六处理而不是产生无穷大()
    {
        NoteLayout saved = NewLayout(x: 100, y: 100, width: 360, height: 420, dpi: 0);

        WindowPlacement placement = LayoutMath.Restore(
            saved,
            [Displays.Create(PrimaryId, dpi: 96)],
            cascadeIndex: 0,
            showStatusBar: true);

        Assert.Equal(360, placement.Bounds.Width);
        Assert.False(double.IsNaN(placement.Bounds.Width));
    }

    [Fact]
    public void 尺寸超过工作区_被夹到工作区大小()
    {
        NoteLayout saved = NewLayout(x: 10, y: 10, width: 5000, height: 4000);

        WindowPlacement placement = LayoutMath.Restore(
            saved,
            [Displays.Create(PrimaryId)],
            cascadeIndex: 0,
            showStatusBar: true);

        Assert.Equal(1920, placement.Bounds.Width);
        Assert.Equal(1080, placement.Bounds.Height);
        Assert.True(placement.WasAdjusted);
    }

    // ---- 最小尺寸守卫（§15.1） ----

    [Fact]
    public void 保存的宽度是零_窗口被撑到最小宽度而不是恢复到看不见()
    {
        // 手改坏的布局文件能造成的最坏后果：窗口存在、能被聚焦、就是看不见也抓不到，
        // 而用户除了再手改一次 JSON 没有任何恢复途径。
        NoteLayout saved = NewLayout(x: 100, y: 100, width: 0, height: 0);

        WindowPlacement placement = LayoutMath.Restore(
            saved,
            [Displays.Create(PrimaryId)],
            cascadeIndex: 0,
            showStatusBar: true);

        Assert.Equal(240, placement.Bounds.Width);
        Assert.Equal(120, placement.Bounds.Height);

        // 尺寸被修正同样算「修正过」，日志里要留得下这条线索。
        Assert.True(placement.WasAdjusted);
    }

    [Fact]
    public void 最小尺寸按目标显示器的DPI换算而不是当成原始像素()
    {
        // 240 直接当像素用的话，在 150% 屏上只有 160 DIP，标题照旧被截断到认不出来。
        NoteLayout saved = NewLayout(x: 2000, y: 100, width: 0, height: 0, dpi: 96);

        WindowPlacement placement = LayoutMath.Restore(
            saved,
            Displays.PrimaryAndHighDpiSecondary(),
            cascadeIndex: 0,
            showStatusBar: true);

        Assert.Equal(SecondaryId, placement.DisplayId);
        Assert.Equal(360, placement.Bounds.Width);
        Assert.Equal(180, placement.Bounds.Height);
    }

    [Fact]
    public void 折叠态的零尺寸便签_不会被撑到展开态的最小高度()
    {
        // 折叠态就是 44 DIP 本身，比展开态的 120 还矮。最小值写死成 120 的话，
        // 每张折叠的便签一重启就被撑回 120，折叠状态等于白存。
        NoteLayout saved = NewLayout(x: 100, y: 100, width: 0, height: 0);

        saved.IsCollapsed = true;

        WindowPlacement placement = LayoutMath.Restore(
            saved,
            [Displays.Create(PrimaryId)],
            cascadeIndex: 0,
            showStatusBar: true);

        Assert.Equal(44, placement.Bounds.Height);
        Assert.Equal(240, placement.Bounds.Width);
    }

    [Fact]
    public void 原显示器已拔掉时_零尺寸同样被撑到最小尺寸()
    {
        // 层叠那条路径的尺寸来自 Cascade，也是把保存的宽高原样搬过来的，
        // 因此零尺寸在这一路上同样会产出看不见的窗口。
        NoteLayout saved = NewLayout(x: 9000, y: 100, width: 0, height: 0);

        WindowPlacement placement = LayoutMath.Restore(
            saved,
            [Displays.Create(PrimaryId)],
            cascadeIndex: 0,
            showStatusBar: true);

        Assert.Equal(new PixelRect(24, 24, 240, 120), placement.Bounds);
    }

    // ---- 显示器不存在时 ----

    [Fact]
    public void 原显示器已被拔掉_层叠到主显示器并标记为已修正()
    {
        // 坐标落在虚拟屏幕的空洞处（这一带曾经是那台显示器）。照搬过去的结果是
        // 窗口存在但看不见，用户只会觉得「便签丢了」。
        NoteLayout saved = NewLayout(x: 9000, y: 100, width: 360, height: 420, dpi: 96);
        List<DisplaySnapshot> displays = [Displays.Create(PrimaryId)];

        WindowPlacement placement = LayoutMath.Restore(saved, displays, cascadeIndex: 0, showStatusBar: true);

        Assert.Equal(PrimaryId, placement.DisplayId);
        Assert.Equal(new PixelRect(24, 24, 360, 420), placement.Bounds);
        Assert.True(placement.WasAdjusted);
    }

    [Fact]
    public void 拔掉显示器后_多张便签按序号错开而不是完全重叠()
    {
        NoteLayout saved = NewLayout(x: 9000, y: 100, width: 360, height: 420);
        List<DisplaySnapshot> displays = [Displays.Create(PrimaryId)];

        WindowPlacement first = LayoutMath.Restore(saved, displays, cascadeIndex: 0, showStatusBar: true);
        WindowPlacement second = LayoutMath.Restore(saved, displays, cascadeIndex: 1, showStatusBar: true);

        Assert.Equal(new PixelRect(24, 24, 360, 420), first.Bounds);
        Assert.Equal(new PixelRect(52, 52, 360, 420), second.Bounds);
    }

    [Fact]
    public void 拔掉显示器后_位置跟着缩放_避免在新屏上突然变大变小()
    {
        // 原屏 150%，落到 100% 的主屏上，尺寸要跟着缩回去。
        NoteLayout saved = NewLayout(x: 9000, y: 100, width: 540, height: 630, dpi: 144);

        WindowPlacement placement = LayoutMath.Restore(
            saved,
            [Displays.Create(PrimaryId, dpi: 96)],
            cascadeIndex: 0,
            showStatusBar: true);

        Assert.Equal(360, placement.Bounds.Width);
        Assert.Equal(420, placement.Bounds.Height);
    }

    [Fact]
    public void 拔掉显示器后_层叠结果同样会被夹取()
    {
        // 一台很小的主屏上，层叠第 20 张的偏移已经接近 100px，尺寸又超出工作区，
        // 夹取必须仍然生效，否则便签会被摆到工作区外。
        NoteLayout saved = NewLayout(x: 9000, y: 100, width: 360, height: 420);
        List<DisplaySnapshot> displays = [Displays.Create(PrimaryId, width: 320, height: 240)];

        WindowPlacement placement = LayoutMath.Restore(saved, displays, cascadeIndex: 20, showStatusBar: true);

        Assert.Equal(320, placement.Bounds.Width);
        Assert.Equal(240, placement.Bounds.Height);
        Assert.InRange(placement.Bounds.X, 0, 320);
        Assert.InRange(placement.Bounds.Y, 0, 240);
    }

    [Fact]
    public void 没有主显示器标记时_退回列表里第一台()
    {
        NoteLayout saved = NewLayout(x: 9000, y: 100, width: 360, height: 420);
        List<DisplaySnapshot> displays = [Displays.Create(SecondaryId, isPrimary: false)];

        WindowPlacement placement = LayoutMath.Restore(saved, displays, cascadeIndex: 0, showStatusBar: true);

        Assert.Equal(SecondaryId, placement.DisplayId);
    }

    [Fact]
    public void 一台显示器都没有_抛异常而不是静默返回零矩形()
    {
        // 静默返回 (0,0,0,0) 会让所有便签缩成一个看不见的点，而且没有任何错误可查。
        Assert.Throws<ArgumentException>(() => LayoutMath.Restore(
            NewLayout(0, 0, 360, 420),
            [],
            cascadeIndex: 0,
            showStatusBar: true));
    }

    [Fact]
    public void 恢复算法不修改传入的布局对象()
    {
        // 原始坐标必须留着：用户把显示器插回来之后还要靠它恢复原样。
        NoteLayout saved = NewLayout(x: 9000, y: 100, width: 360, height: 420);

        LayoutMath.Restore(saved, [Displays.Create(PrimaryId)], cascadeIndex: 0, showStatusBar: true);

        Assert.Equal(9000, saved.X);
        Assert.Equal(100, saved.Y);
        Assert.Equal(360, saved.Width);
    }

    // ---- 辅助 ----

    private static NoteLayout NewLayout(
        double x,
        double y,
        double width,
        double height,
        uint dpi = 96)
        => new()
        {
            NoteId = Guid.Parse("6f1d0a2e-1111-2222-3333-444455556666"),
            X = x,
            Y = y,
            Width = width,
            Height = height,
            ExpandedHeight = height,
            Dpi = dpi,
        };

    /// <summary>
    /// 接缝左侧仍然属于左边那台显示器——与「接缝上归右边」配对，一起把左闭右开钉死。
    /// </summary>
    [Fact]
    public void 接缝左侧属于左边那台显示器()
    {
        List<DisplaySnapshot> displays = Displays.PrimaryAndHighDpiSecondary();

        Assert.Equal(PrimaryId, LayoutMath.FindDisplayContaining(displays, 1919.9, 500)?.DeviceId);
    }
}
