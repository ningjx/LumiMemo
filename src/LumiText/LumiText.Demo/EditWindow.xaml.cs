using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using LumiText.Core.Documents;

namespace LumiText.Demo;

/// <summary>
/// M3/M4 冒烟窗口：<see cref="LumiText.WinUI.Controls.LumiEditor"/> 的可视化验收入口——
/// 键入/删除/选区/光标闪烁/撤销重做，以及 M4 的中文 IME 组字。
/// </summary>
public sealed partial class EditWindow : Window
{
    private readonly DesktopAcrylicController? _backdrop;

    public EditWindow()
    {
        InitializeComponent();
        _backdrop = DemoBackdrop.Apply(this, RootGrid);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(760, 640));

        Editor.SetDocument(BuildSampleDocument());
        Editor.HostWindow = this; // IME 候选窗屏幕定位需要窗口 HWND
        Editor.LayoutStatsChanged += ms =>
            StatsText.Text = $"排版耗时：{ms:F2} ms";
        Editor.UserEdited += (_, _) =>
            StatsText.Text = $"已编辑（{Editor.PlainText.Length} 字符）";
        DebugToggle.Checked += (_, _) => Editor.DebugOverlay = true;
        DebugToggle.Unchecked += (_, _) => Editor.DebugOverlay = false;

        Closed += (_, _) => _backdrop?.Dispose();
    }

    private static Document BuildSampleDocument() => new([
        new ParagraphBlock([
            new TextRun("普通文字，"),
            new TextRun("加粗", new InlineStyle(Bold: true)),
            new TextRun("，"),
            new TextRun("斜体", new InlineStyle(Italic: true)),
            new TextRun("。"),
        ]),
        new HeadingBlock("标题 H1", 1),
        new TodoBlock([new TextRun("已完成的待办")], @checked: true),
        new TodoBlock([new TextRun("待办的待办")], @checked: false),
        new DividerBlock(),
        new ParagraphBlock("第二段。在这里试试退格合并到上一段。"),
        new ParagraphBlock("第三段。Ctrl+B/I/U 切换样式，Ctrl+Z/Y 撤销重做。"),
    ]);
}
