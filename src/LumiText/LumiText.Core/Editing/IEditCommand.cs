namespace LumiText.Core.Editing;

/// <summary>
/// 编辑命令抽象（Phase 2 设计 §5.1）：Apply 把状态从 before 推到 after 并返回新快照。
/// 撤销采用「快照式」：EditorCore 在 Apply 时把执行前快照存进历史条目，撤销 = 直接恢复——
/// 命令层不需要 Inverse（避免 DeleteRangeCommand 这类「逆命令需要被删内容快照」的尴尬）。
/// Kind 供撤销栈做命令合并判定。
/// </summary>
public interface IEditCommand
{
    /// <summary>执行命令，返回执行后的新快照（含新选区）。state 为执行前快照。</summary>
    EditorState Apply(EditorState state);

    /// <summary>命令种类标识：撤销栈合并判定的 key（同 Kind 才可能合并）。</summary>
    string Kind { get; }
}
