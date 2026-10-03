namespace LumiText.Core.Editing;

/// <summary>
/// 撤销栈（Phase 2 设计 §5.2）：双栈结构，容量上限 1000 条。
/// 条目 = (命令, 执行前快照, 执行后快照)——撤销 = 恢复执行前快照，重做 = 恢复执行后快照。
/// 快照式撤销避免了「逆命令需要被删内容」的循环依赖（DeleteRangeCommand 等）。
/// 命令合并：连续同 Kind 且可合并的键入合并为一条历史条目。
/// </summary>
public sealed class UndoStack
{
    /// <summary>历史条目容量上限（O3，2026-10-03 拍板）。</summary>
    public const int Capacity = 1000;

    private readonly LinkedList<HistoryEntry> _undo = new();
    private readonly Stack<HistoryEntry> _redo = new();

    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// 压入一条已执行的命令记录。before/after 快照由 EditorCore 在 Apply 前后抓取。
    /// 若栈顶条目可与本条合并（见 <see cref="TryMerge"/>），则合并而非新增。
    /// </summary>
    public void Push(IEditCommand command, EditorState before, EditorState after)
    {
        if (_undo.Last is { } last && TryMerge(last.Value, command, before, after, out var merged))
        {
            last.Value = merged;
        }
        else
        {
            _undo.AddLast(new HistoryEntry(command, before, after));
            if (_undo.Count > Capacity)
            {
                _undo.RemoveFirst();
            }
        }
        _redo.Clear(); // 新命令使重做栈失效
    }

    /// <summary>撤销一步：返回执行前快照；无可撤销时返回 null。</summary>
    public EditorState? Undo()
    {
        if (_undo.Last is not { } last)
        {
            return null;
        }
        _undo.RemoveLast();
        _redo.Push(last.Value);
        return last.Value.Before;
    }

    /// <summary>重做一步：返回执行后快照；无可重做时返回 null。</summary>
    public EditorState? Redo()
    {
        if (_redo.Count == 0)
        {
            return null;
        }
        var entry = _redo.Pop();
        _undo.AddLast(entry);
        return entry.After;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>
    /// 命令合并规则（§5.2）：连续 InsertTextCommand 且 Kind 相同、光标连续、无选区跳变。
    /// 「光标连续」= 本次命令的执行前选区 == 上一条的执行后选区（键入紧接上一次键入末尾）。
    /// Enter/粘贴/IME 提交由上层保证以不同命令或显式 BeginGroup 分隔，不会误入此合并。
    /// </summary>
    private static bool TryMerge(HistoryEntry previous, IEditCommand command,
        EditorState before, EditorState after, out HistoryEntry merged)
    {
        merged = default;
        if (previous.Command.Kind != command.Kind)
        {
            return false;
        }
        if (command is not InsertTextMarker && previous.Command is not InsertTextMarker)
        {
            return false; // 首版只合并连续键入；删除/样式等不合并
        }
        // 光标连续：本次 before 的选区必须等于上次 after 的选区（且均为坍缩态）
        if (!previous.After.Selection.IsCollapsed || !before.Selection.IsCollapsed)
        {
            return false;
        }
        if (!previous.After.Selection.Active.Equals(before.Selection.Active))
        {
            return false;
        }
        // 合并：before 沿用上次的，after 用本次的
        merged = new HistoryEntry(previous.Command, previous.Before, after);
        return true;
    }

    /// <summary>历史条目：命令 + 执行前/后快照（record struct，存链表节点值）。</summary>
    private readonly record struct HistoryEntry(
        IEditCommand Command,
        EditorState Before,
        EditorState After);
}

/// <summary>标记接口：可合并的键入命令（InsertTextCommand 实现）。</summary>
public interface InsertTextMarker
{
}
