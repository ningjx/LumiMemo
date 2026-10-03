using LumiText.Core.Documents;

namespace LumiText.Core.Editing;

/// <summary>
/// 编辑内核的不可变状态快照：当前文档 + 当前选区。
/// 命令的 Apply/Inverse 都以它为输入并产出新快照（record 语义，结构共享）。
/// </summary>
public sealed record EditorState(Document Document, TextRange Selection)
{
    public static EditorState Initial(Document document) =>
        new(document, TextRange.Collapse(new TextPosition(0, 0)));
}
