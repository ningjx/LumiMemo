using LumiText.Core.Documents;

namespace LumiText.Core.Editing;

/// <summary>
/// 编辑内核（Phase 2 设计 §3.2/§5.3）：会话态 = 当前文档快照 + 选区 + 撤销栈。
/// 命令经 <see cref="ApplyCommand"/> 进入，事件经 <see cref="DocumentChanged"/> /
/// <see cref="SelectionChanged"/> 暴露给宿主（组字期抑制逻辑在 TSF 层实现，不在此）。
/// </summary>
public sealed class EditorCore
{
    private EditorState _state;
    private readonly UndoStack _undo = new();

    public EditorCore(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _state = EditorState.Initial(document);
    }

    /// <summary>当前权威文档快照（不可变 record）。</summary>
    public Document Document => _state.Document;

    /// <summary>当前选区。</summary>
    public TextRange Selection => _state.Selection;

    public UndoStack History => _undo;

    /// <summary>文档内容变化（触发自动保存）。组字期由 TSF 层保证不调用本类的方法。</summary>
    public event EventHandler? DocumentChanged;

    /// <summary>选区变化（触发光标重绘、IME 候选窗跟随）。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>
    /// 执行一条编辑命令：文档演化 + 选区更新 + 撤销栈记录。
    /// 快照式撤销：执行前的 (document, selection) 作为历史条目的 Before 入栈。
    /// </summary>
    public void ApplyCommand(IEditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var before = _state;
        var after = command.Apply(before);

        bool documentChanged = !ReferenceEquals(before.Document, after.Document);
        bool selectionChanged = before.Selection != after.Selection;

        if (!documentChanged && !selectionChanged)
        {
            return; // 命令未产生实际变化（如空选区的样式切换）——不进历史
        }

        _state = after;
        if (documentChanged)
        {
            _undo.Push(command, before, after);
            DocumentChanged?.Invoke(this, EventArgs.Empty);
        }
        if (selectionChanged)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>撤销一步。返回是否有可撤销项。</summary>
    public bool Undo()
    {
        var restored = _undo.Undo();
        if (restored is null)
        {
            return false;
        }
        var before = _state;
        _state = restored;
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        if (before.Selection != restored.Selection)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        return true;
    }

    /// <summary>重做一步。返回是否有可重做项。</summary>
    public bool Redo()
    {
        var restored = _undo.Redo();
        if (restored is null)
        {
            return false;
        }
        var before = _state;
        _state = restored;
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        if (before.Selection != restored.Selection)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        return true;
    }

    /// <summary>
    /// 纯选区变更（光标移动、鼠标点选、Shift+方向键）——不进撤销栈，只发 SelectionChanged。
    /// </summary>
    public void SetSelection(TextRange selection)
    {
        if (_state.Selection == selection)
        {
            return;
        }
        _state = _state with { Selection = selection };
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>整体替换文档（加载 .lumi 时）：清空撤销栈，选区归位文档首。</summary>
    public void ResetDocument(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _state = EditorState.Initial(document);
        _undo.Clear();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>纯文本投影（§3.4）：块间 \n 分隔；bullet 段带 • 前缀；TodoBlock 带 ☐/☑ 前缀（O2）。</summary>
    public string GetPlainText()
    {
        var blocks = _state.Document.Blocks;
        var builder = new System.Text.StringBuilder();
        for (int i = 0; i < blocks.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }
            switch (blocks[i])
            {
                case TodoBlock t:
                    builder.Append(t.Checked ? "☑ " : "☐ ");
                    builder.Append(t.PlainText);
                    break;
                case ParagraphBlock p when p.IsBullet:
                    builder.Append("• ");
                    builder.Append(p.PlainText);
                    break;
                case ParagraphBlock p:
                    builder.Append(p.PlainText);
                    break;
                case HeadingBlock h:
                    builder.Append(h.PlainText);
                    break;
                // DividerBlock / ImageBlock：纯文本投影为空行
            }
        }
        return builder.ToString();
    }

    /// <summary>
    /// 提取选区覆盖的内容为子文档（剪贴板复制/剪切用）：
    /// 跨块选区裁剪首尾块到选区边界、中间整块保留；单块选区裁剪到字符区间。
    /// 坍缩选区返回 null。
    /// </summary>
    public Document? ExtractSelection()
    {
        var sel = _state.Selection;
        if (sel.IsCollapsed)
        {
            return null;
        }
        var (start, end) = (sel.Start, sel.End);
        var blocks = _state.Document.Blocks;
        var result = new List<Block>();

        if (start.BlockIndex == end.BlockIndex)
        {
            var block = blocks[start.BlockIndex];
            int count = end.CharIndex - start.CharIndex;
            var runs = BlockTextOps.SliceRuns(block, start.CharIndex, count);
            result.Add(BlockTextOps.WithRuns(block, runs));
            return new Document(result);
        }

        for (int i = start.BlockIndex; i <= end.BlockIndex; i++)
        {
            var block = blocks[i];
            if (!BlockTextOps.IsTextBlock(block))
            {
                // 中间的非文本块（Divider/Image）整块保留
                if (i > start.BlockIndex && i < end.BlockIndex)
                {
                    result.Add(block);
                }
                continue;
            }
            int blockStart = i == start.BlockIndex ? start.CharIndex : 0;
            int blockEnd = i == end.BlockIndex ? end.CharIndex : BlockTextOps.GetTextLength(block);
            if (blockEnd <= blockStart)
            {
                continue;
            }
            var runs = BlockTextOps.SliceRuns(block, blockStart, blockEnd - blockStart);
            result.Add(BlockTextOps.WithRuns(block, runs));
        }
        return result.Count > 0 ? new Document(result) : null;
    }

    /// <summary>选区的纯文本投影（块间 \n；bullet 段带 • 前缀；TodoBlock 带前缀）。坍缩选区返回空串。</summary>
    public string GetSelectionPlainText()
    {
        var fragment = ExtractSelection();
        if (fragment is null)
        {
            return string.Empty;
        }
        var builder = new System.Text.StringBuilder();
        for (int i = 0; i < fragment.Blocks.Count; i++)
        {
            if (i > 0)
            {
                builder.Append('\n');
            }
            switch (fragment.Blocks[i])
            {
                case TodoBlock t:
                    builder.Append(t.Checked ? "☑ " : "☐ ");
                    builder.Append(t.PlainText);
                    break;
                case ParagraphBlock p when p.IsBullet:
                    builder.Append("• ");
                    builder.Append(p.PlainText);
                    break;
                case ParagraphBlock p:
                    builder.Append(p.PlainText);
                    break;
                case HeadingBlock h:
                    builder.Append(h.PlainText);
                    break;
            }
        }
        return builder.ToString();
    }
}
