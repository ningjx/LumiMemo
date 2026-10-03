using Windows.ApplicationModel.DataTransfer;
using LumiText.Core.Documents;
using LumiText.Core.Editing;

namespace LumiText.WinUI.Editing;

/// <summary>
/// 剪贴板互通（Phase 2 设计 §7）：适配 <see cref="Clipboard"/>，按现产品 OnPaste 语义查询。
/// 只在 UI 线程被调（§7.3 DispatcherQueue 要求天然满足）。
/// </summary>
public static class WinClipboard
{
    /// <summary>复制：选区 → 纯文本（必有）+ RTF（有样式时）。返回是否写入了内容。</summary>
    public static bool Copy(EditorCore core)
    {
        ArgumentNullException.ThrowIfNull(core);
        string plain = core.GetSelectionPlainText();
        if (plain.Length == 0)
        {
            return false;
        }
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(plain);

        var fragment = core.ExtractSelection();
        if (fragment is not null)
        {
            string rtf = RtfProjection.ToRtf(fragment);
            // 有实际样式时才带 RTF（纯文本片段不带，避免目标应用误当成富文本）
            if (HasAnyStyle(fragment))
            {
                package.SetRtf(rtf);
            }
        }
        Clipboard.SetContent(package);
        return true;
    }

    /// <summary>剪切：复制 + 删除选区。返回是否有内容被剪切。</summary>
    public static bool Cut(EditorCore core)
    {
        if (!Copy(core))
        {
            return false;
        }
        core.ApplyCommand(new Core.Editing.Commands.DeleteRangeCommand(core.Selection));
        return true;
    }

    /// <summary>
    /// 粘贴：按现产品语义查询——RTF 优先（富内容），其次纯文本。
    /// 「纯 Bitmap 不含文本」的插图拦截属 M6 内嵌图片，这里只处理文本两路。
    /// 返回是否消费了剪贴板内容。
    /// </summary>
    public static async Task<bool> PasteAsync(EditorCore core)
    {
        ArgumentNullException.ThrowIfNull(core);
        var view = Clipboard.GetContent();

        // 1. RTF → 解析为模型命令序列
        if (view.Contains(StandardDataFormats.Rtf))
        {
            try
            {
                string rtf = await view.GetRtfAsync();
                var doc = RtfProjection.FromRtf(rtf);
                InsertDocument(core, doc);
                return true;
            }
            catch
            {
                // RTF 解析失败降级到纯文本
            }
        }

        // 2. 纯文本
        if (view.Contains(StandardDataFormats.Text))
        {
            string text = await view.GetTextAsync();
            if (text.Length > 0)
            {
                // 纯文本按 \n 分段（RTF 投影的对偶：块间 \n）
                var doc = PlainTextToDocument(text);
                InsertDocument(core, doc);
                return true;
            }
        }
        return false;
    }

    /// <summary>把一篇文档片段插入编辑器（逐块插入，块间分块）。</summary>
    private static void InsertDocument(EditorCore core, Document doc)
    {
        var blocks = doc.Blocks;
        if (blocks.Count == 0)
        {
            return;
        }
        // 首块与当前块合并（在当前光标处插入其文本），后续块逐个 SplitBlock + 插入。
        for (int i = 0; i < blocks.Count; i++)
        {
            if (!BlockTextOps.IsTextBlock(blocks[i]))
            {
                continue;
            }
            if (i > 0)
            {
                core.ApplyCommand(new Core.Editing.Commands.SplitBlockCommand(core.Selection.Active));
            }
            // 逐 run 插入（保留样式）：先插文本，再对刚插入的区间应用样式
            InsertRuns(core, BlockTextOps.GetRuns(blocks[i]));
        }
    }

    private static void InsertRuns(EditorCore core, IReadOnlyList<TextRun> runs)
    {
        foreach (var run in runs)
        {
            if (run.Text.Length == 0)
            {
                continue;
            }
            var insertAt = core.Selection.Active;
            core.ApplyCommand(new Core.Editing.Commands.InsertTextCommand(run.Text));
            if (run.Style is { } style && style != new InlineStyle())
            {
                var end = core.Selection.Active;
                // 覆盖式置位：把刚插入的文本样式精确设为源 run 的样式
                core.ApplyCommand(new Core.Editing.Commands.SetInlineStyleCommand(
                    new TextRange(insertAt, end), style));
            }
        }
    }

    private static Document PlainTextToDocument(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var blocks = new List<Block>(lines.Length);
        foreach (var line in lines)
        {
            blocks.Add(new ParagraphBlock(line));
        }
        return new Document(blocks);
    }

    private static bool HasAnyStyle(Document doc)
    {
        foreach (var block in doc.Blocks)
        {
            if (!BlockTextOps.IsTextBlock(block))
            {
                continue;
            }
            foreach (var run in BlockTextOps.GetRuns(block))
            {
                if (run.Style is { } s && (s.Bold || s.Italic || s.Strikethrough || s.Underline || s.Color is not null))
                {
                    return true;
                }
            }
        }
        return false;
    }
}
