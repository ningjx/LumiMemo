namespace LumiMemo.App.Text;

/// <summary>一次 Markdown 文本编辑后的内容与选区。</summary>
/// <param name="Text">编辑后的完整文本。</param>
/// <param name="SelectionStart">编辑后选区的起点。</param>
/// <param name="SelectionLength">编辑后选区的长度。</param>
public readonly record struct MarkdownTextEdit(string Text, int SelectionStart, int SelectionLength);

/// <summary>为纯文本编辑器提供不依赖 WPF 控件的 Markdown 格式操作。</summary>
public static class MarkdownTextEditing
{
    /// <summary>切换选区两侧的行内标记；没有选区时插入一对标记并把光标放在中间。</summary>
    /// <param name="text">完整文本。</param>
    /// <param name="selectionStart">选区起点。</param>
    /// <param name="selectionLength">选区长度。</param>
    /// <param name="opening">起始标记。</param>
    /// <param name="closing">结束标记。</param>
    /// <returns>编辑结果。</returns>
    public static MarkdownTextEdit ToggleInline(
        string text,
        int selectionStart,
        int selectionLength,
        string opening,
        string closing)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(opening);
        ArgumentException.ThrowIfNullOrEmpty(closing);
        ValidateSelection(text, selectionStart, selectionLength);

        if (selectionLength == 0)
        {
            string inserted = opening + closing;
            string result = text.Insert(selectionStart, inserted);
            return new MarkdownTextEdit(result, selectionStart + opening.Length, 0);
        }

        int selectionEnd = selectionStart + selectionLength;
        bool markersSurroundSelection = selectionStart >= opening.Length
            && selectionEnd + closing.Length <= text.Length
            && text.AsSpan(selectionStart - opening.Length, opening.Length).SequenceEqual(opening)
            && text.AsSpan(selectionEnd, closing.Length).SequenceEqual(closing);

        if (markersSurroundSelection)
        {
            string result = text.Remove(selectionEnd, closing.Length)
                .Remove(selectionStart - opening.Length, opening.Length);
            return new MarkdownTextEdit(result, selectionStart - opening.Length, selectionLength);
        }

        ReadOnlySpan<char> selected = text.AsSpan(selectionStart, selectionLength);
        bool selectionIncludesMarkers = selected.StartsWith(opening, StringComparison.Ordinal)
            && selected.EndsWith(closing, StringComparison.Ordinal)
            && selectionLength >= opening.Length + closing.Length;

        if (selectionIncludesMarkers)
        {
            string inner = selected[opening.Length..^closing.Length].ToString();
            string result = text.Remove(selectionStart, selectionLength).Insert(selectionStart, inner);
            return new MarkdownTextEdit(result, selectionStart, inner.Length);
        }

        string wrapped = opening + selected.ToString() + closing;
        string wrappedResult = text.Remove(selectionStart, selectionLength).Insert(selectionStart, wrapped);
        return new MarkdownTextEdit(wrappedResult, selectionStart + opening.Length, selectionLength);
    }

    /// <summary>在选区覆盖的整行上切换列表前缀，并在列表类型之间直接转换。</summary>
    /// <param name="text">完整文本。</param>
    /// <param name="selectionStart">选区起点。</param>
    /// <param name="selectionLength">选区长度。</param>
    /// <param name="prefix">目标行前缀，例如 <c>- </c> 或 <c>- [ ] </c>。</param>
    /// <returns>编辑结果。</returns>
    public static MarkdownTextEdit ToggleLinePrefix(
        string text,
        int selectionStart,
        int selectionLength,
        string prefix)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        ValidateSelection(text, selectionStart, selectionLength);

        int lineStart = selectionStart == 0 ? 0 : text.LastIndexOf('\n', selectionStart - 1) + 1;
        int selectionEnd = selectionStart + selectionLength;
        int lastSelectedCharacter = selectionLength > 0 && selectionEnd > lineStart && text[selectionEnd - 1] == '\n'
            ? selectionEnd - 1
            : selectionEnd;
        int nextNewLine = text.IndexOf('\n', lastSelectedCharacter);
        int lineEnd = nextNewLine < 0 ? text.Length : nextNewLine;

        string block = text[lineStart..lineEnd];
        if (selectionLength == 0 && string.IsNullOrWhiteSpace(block))
        {
            string emptyLineReplacement = block + prefix;
            string emptyLineResult = text.Remove(lineStart, lineEnd - lineStart).Insert(lineStart, emptyLineReplacement);
            return new MarkdownTextEdit(emptyLineResult, selectionStart + prefix.Length, 0);
        }

        string[] lines = block.Split('\n');
        bool removeTarget = lines.Where(static line => !string.IsNullOrWhiteSpace(line))
            .All(line => HasPrefixAfterIndent(line, prefix));

        var transformed = new string[lines.Length];
        for (int index = 0; index < lines.Length; index++)
        {
            transformed[index] = TransformLine(lines[index], prefix, removeTarget);
        }

        string replacement = string.Join('\n', transformed);
        string result = text.Remove(lineStart, lineEnd - lineStart).Insert(lineStart, replacement);

        if (selectionLength == 0)
        {
            int delta = replacement.Length - block.Length;
            int caret = Math.Clamp(selectionStart + delta, lineStart, lineStart + replacement.Length);
            return new MarkdownTextEdit(result, caret, 0);
        }

        return new MarkdownTextEdit(result, lineStart, replacement.Length);
    }

    private static string TransformLine(string line, string targetPrefix, bool removeTarget)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return line;
        }

        int indentLength = 0;
        while (indentLength < line.Length && line[indentLength] is ' ' or '\t')
        {
            indentLength++;
        }

        string indent = line[..indentLength];
        string content = line[indentLength..];

        if (removeTarget && content.StartsWith(targetPrefix, StringComparison.Ordinal))
        {
            return indent + content[targetPrefix.Length..];
        }

        content = RemoveKnownListPrefix(content);
        return indent + targetPrefix + content;
    }

    private static bool HasPrefixAfterIndent(string line, string prefix)
    {
        int indentLength = 0;
        while (indentLength < line.Length && line[indentLength] is ' ' or '\t')
        {
            indentLength++;
        }

        return line.AsSpan(indentLength).StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string RemoveKnownListPrefix(string content)
    {
        foreach (string prefix in new[] { "- [ ] ", "- [x] ", "- [X] ", "- ", "* ", "+ " })
        {
            if (content.StartsWith(prefix, StringComparison.Ordinal))
            {
                return content[prefix.Length..];
            }
        }

        return content;
    }

    private static void ValidateSelection(string text, int selectionStart, int selectionLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(selectionStart);
        ArgumentOutOfRangeException.ThrowIfNegative(selectionLength);

        if (selectionStart > text.Length || selectionLength > text.Length - selectionStart)
        {
            throw new ArgumentOutOfRangeException(nameof(selectionLength), "选区必须位于文本范围内。");
        }
    }
}
