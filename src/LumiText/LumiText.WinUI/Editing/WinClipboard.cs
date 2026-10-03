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

    /// <summary>插图长边上限（dip）——与现产品 MaxInsertImageEdge 对齐。</summary>
    public const float MaxInsertImageEdge = 280f;

    /// <summary>
    /// 粘贴：按现产品 OnPaste 语义查询——
    /// ① 纯 Bitmap（不含 Text/Rtf）→ 拦截插图（现产品的拦截分支，M6）；
    /// ② RTF → 解析为模型命令序列；
    /// ③ 纯文本。
    /// 返回是否消费了剪贴板内容。
    /// </summary>
    public static async Task<bool> PasteAsync(EditorCore core)
    {
        ArgumentNullException.ThrowIfNull(core);
        var view = Clipboard.GetContent();

        // ① 纯图片拦截（沿用旧产品的粘贴语义）：
        // 含 Bitmap 且不含 Text/Rtf → 自己走插图逻辑，统一 280px 上限
        if (view.Contains(StandardDataFormats.Bitmap)
            && !view.Contains(StandardDataFormats.Text)
            && !view.Contains(StandardDataFormats.Rtf))
        {
            try
            {
                await PasteImageAsync(core, view);
                return true;
            }
            catch
            {
                // 插图失败放行，继续尝试文本路径
            }
        }

        // ② RTF → 解析为模型命令序列
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

        // ③ 纯文本
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

    /// <summary>从剪贴板读位图 → 解码量尺寸（280px 上限）→ InsertImageCommand。</summary>
    private static async Task PasteImageAsync(EditorCore core, DataPackageView view)
    {
        var reference = await view.GetBitmapAsync();
        using var stream = await reference.OpenReadAsync();
        if (await DecodeImageAsync(stream) is not { } decoded)
        {
            return;
        }

        string imageId = $"img-{Guid.NewGuid():N}";
        core.ApplyCommand(new Core.Editing.Commands.InsertImageCommand(
            imageId, decoded.Bytes, decoded.Mime, decoded.Width, decoded.Height));
    }

    /// <summary>
    /// 位图流 → 原始字节 + MIME + 显示尺寸（长边 280dip 上限）。
    /// 剪贴板粘贴与 OS 拖放插图共用（Phase 3 M4 抽出）；空流返回 <see langword="null"/>。
    /// </summary>
    public static async Task<(byte[] Bytes, string Mime, float Width, float Height)?> DecodeImageAsync(
        Windows.Storage.Streams.IRandomAccessStreamWithContentType stream)
    {
        // 读原始字节（存进 ImageResource.Data 作为权威字节）
        byte[] bytes;
        using (var ms = new System.IO.MemoryStream())
        {
            await stream.AsStreamForRead().CopyToAsync(ms);
            bytes = ms.ToArray();
        }
        if (bytes.Length == 0)
        {
            return null;
        }

        // 解码拿原始尺寸（用 Windows.Graphics.Imaging，与现产品同路径）
        stream.Seek(0);
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        double scale = Math.Min(1.0, MaxInsertImageEdge / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        float width = Math.Max(1, (float)Math.Round(decoder.PixelWidth * scale));
        float height = Math.Max(1, (float)Math.Round(decoder.PixelHeight * scale));

        return (bytes, MimeFromCodec(decoder.DecoderInformation.CodecId), width, height);
    }

    private static string MimeFromCodec(Guid codecId)
    {
        if (codecId == Windows.Graphics.Imaging.BitmapDecoder.PngDecoderId) return "image/png";
        if (codecId == Windows.Graphics.Imaging.BitmapDecoder.JpegDecoderId) return "image/jpeg";
        if (codecId == Windows.Graphics.Imaging.BitmapDecoder.GifDecoderId) return "image/gif";
        if (codecId == Windows.Graphics.Imaging.BitmapDecoder.BmpDecoderId) return "image/bmp";
        if (codecId == Windows.Graphics.Imaging.BitmapDecoder.WebpDecoderId) return "image/webp";
        return "image/png"; // 未知按 png（解码器仍能按内容识别）
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
