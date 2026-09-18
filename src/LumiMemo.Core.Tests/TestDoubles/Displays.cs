using LumiMemo.Core.Models;

namespace LumiMemo.Core.Tests.TestDoubles;

/// <summary>
/// 构造 <see cref="DisplaySnapshot"/> 的测试辅助方法。
/// </summary>
/// <remarks>
/// 显示器快照有五个字段，其中四个是矩形与 DPI，逐个 <c>new</c> 会让测试用例被参数淹没，
/// 看不出「这个用例到底在测什么」。这里给几个带默认值的构造入口。
/// </remarks>
internal static class Displays
{
    /// <summary>一台 1920×1080、工作区等同整屏的显示器。</summary>
    public static DisplaySnapshot Create(
        string deviceId = @"\\.\DISPLAY1",
        uint dpi = 96,
        bool isPrimary = true,
        double x = 0,
        double y = 0,
        double width = 1920,
        double height = 1080)
        => Create(deviceId, new PixelRect(x, y, width, height), dpi, isPrimary, workArea: null);

    /// <summary>工作区与整屏不同的显示器（例如底部被任务栏占掉一条）。</summary>
    public static DisplaySnapshot Create(
        string deviceId,
        PixelRect bounds,
        uint dpi = 96,
        bool isPrimary = false,
        PixelRect? workArea = null)
        => new(deviceId, deviceId, bounds, workArea ?? bounds, dpi)
        {
            IsPrimary = isPrimary,
        };

    /// <summary>
    /// 一套常见的双屏环境：主屏 1920×1080@100% 在原点，副屏 2560×1440@150% 摆在右侧。
    /// </summary>
    public static List<DisplaySnapshot> PrimaryAndHighDpiSecondary() =>
    [
        Create(@"\\.\DISPLAY1", dpi: 96, isPrimary: true),
        Create(
            @"\\.\DISPLAY2",
            new PixelRect(1920, 0, 2560, 1440),
            dpi: 144,
            isPrimary: false,
            workArea: new PixelRect(1920, 0, 2560, 1400)),
    ];
}
