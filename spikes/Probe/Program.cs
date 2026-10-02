using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;

// 探针：弄清楚 CanvasTextLayout.GetCaretPosition(i).Y 的坐标语义，
// 定位 Win2DTextMeasurer.CountFirstLineChars 消费字符数异常的原因。
const string text = "LumiText 是一个为毛玻璃窗口而生的文本渲染内核。它不依赖任何宿主产品代码，排版引擎与渲染层完全分离。";
const float width = 1100f;

var device = CanvasDevice.GetSharedDevice();
using var format = new CanvasTextFormat { FontFamily = "Segoe UI", FontSize = 15f, WordWrapping = CanvasWordWrapping.Wrap };
using var layout = new CanvasTextLayout(device, text, format, width, 4096f);

Console.WriteLine($"LayoutBounds: {layout.LayoutBounds}");
for (int i = 0; i <= 30; i++)
{
    var p = layout.GetCaretPosition(i, false);
    Console.WriteLine($"caret({i}) = ({p.X:F2}, {p.Y:F2})");
}
foreach (var i in new[] { 40, 50, 55, 56, 57, 58, 60, text.Length })
{
    var p = layout.GetCaretPosition(i, false);
    Console.WriteLine($"caret({i}) = ({p.X:F2}, {p.Y:F2})");
}
var regions = layout.GetCharacterRegions(0, 10);
foreach (var r in regions)
{
    Console.WriteLine($"region[0..10]: bounds={r.LayoutBounds}");
}
