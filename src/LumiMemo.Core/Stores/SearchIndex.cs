using System.Text;
using System.Text.RegularExpressions;
using LumiMemo.Core.Models;

namespace LumiMemo.Core.Stores;

/// <summary>
/// 从 <see cref="NoteStore"/> 派生的辅助索引，用于加速搜索与标签浏览（§9.4）。
/// </summary>
/// <remarks>
/// <para>
/// 纯内存的派生数据，<strong>不落盘</strong>——标签和纯文本都能从 Markdown 完全重建。
/// 维护时机与 <c>NoteStore</c> 的所有变更同步：<c>NoteService</c> 在完成一次修改后同时更新两者。
/// </para>
/// <para>
/// 维护本索引<strong>只能在 UI 线程</strong>（§3.4 规则 T1、T5）。
/// </para>
/// </remarks>
public sealed partial class SearchIndex
{
    /// <summary>标签（忽略大小写）→ 便签集合。</summary>
    private readonly Dictionary<string, HashSet<Guid>> _byTag = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>便签 id → 去掉 Markdown 标记的纯文本缓存。</summary>
    private readonly Dictionary<Guid, string> _plainText = [];

    /// <summary>全量重建。启动建索引时调用（§5.8）。</summary>
    public void Rebuild(IReadOnlyList<Note> notes)
    {
        ArgumentNullException.ThrowIfNull(notes);

        _byTag.Clear();
        _plainText.Clear();

        foreach (var note in notes)
        {
            OnNoteAdded(note);
        }
    }

    public void OnNoteAdded(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);

        _plainText[note.Id] = ToPlainText(note.Content);

        foreach (var tag in note.Tags)
        {
            if (_byTag.TryGetValue(tag, out var ids))
            {
                ids.Add(note.Id);
            }
            else
            {
                _byTag[tag] = [note.Id];
            }
        }
    }

    /// <summary>
    /// 增量更新。
    /// </summary>
    /// <remarks>
    /// 标签没有做「摘旧加新」的差分，而是整体摘除再重建。单张便签的标签数很小，
    /// 这样实现最简且不会因为漏摘而残留旧标签——后者会表现为「搜到的便签已经没有这个标签了」。
    /// </remarks>
    public void OnNoteUpdated(Note note)
    {
        ArgumentNullException.ThrowIfNull(note);

        OnNoteRemoved(note.Id);
        OnNoteAdded(note);
    }

    public void OnNoteRemoved(Guid id)
    {
        _plainText.Remove(id);

        foreach (var ids in _byTag.Values)
        {
            ids.Remove(id);
        }

        var emptyTags = _byTag.Where(static kv => kv.Value.Count == 0)
                              .Select(static kv => kv.Key)
                              .ToList();

        foreach (var emptyTag in emptyTags)
        {
            _byTag.Remove(emptyTag);
        }
    }

    /// <summary>带某个标签的全部便签 id；没有便签使用该标签时返回 <c>null</c>。</summary>
    public IReadOnlySet<Guid>? NotesWithTag(string tag)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);

        return _byTag.TryGetValue(tag, out var ids) ? ids : null;
    }

    /// <summary>全部标签。用于管理器的标签浏览面板（§15.8）。</summary>
    public IReadOnlyCollection<string> AllTags => _byTag.Keys;

    /// <summary>取便签的纯文本形式。没有记录时返回空串。</summary>
    public string GetPlainText(Guid id) => _plainText.GetValueOrDefault(id, string.Empty);

    /// <summary>
    /// 把 Markdown 正文转成用于匹配与摘要的纯文本（§9.4）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>为什么必须有这一步</strong>：直接拿 Markdown 原文匹配会让搜 <c>docker</c>
    /// 命中 <c>```docker</c> 这种围栏标记，也会让搜「标题」因为正文里的 <c>#</c> 而漏掉。
    /// </para>
    /// <para>
    /// 这里用正则做，是为了守住 <c>LumiMemo.Core</c> 的零第三方依赖（§4.1）——
    /// Markdig 在 Infrastructure 层，Core 不能引用它。代价是它不构建真正的 AST，
    /// 对嵌套结构（例如围栏代码块里的列表符号）不如 Markdig 精确。
    /// 若后续发现正则造成的漏匹配成为实际问题（§12.1 定义了匹配要求），
    /// 应改为由 Infrastructure 层预先算好纯文本再送进来，而不是在 Core 里引入 Markdig。
    /// </para>
    /// </remarks>
    private static string ToPlainText(string markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        // 围栏代码块整段丢弃，含围栏行本身。按行处理而不是用正则：
        // 正则要跨行匹配「围栏行 → 围栏行」，写出来既难读又容易在 \s 是否吃换行上出错。
        var withoutFences = new StringBuilder(markdown.Length);
        var insideFence = false;

        foreach (var lineSpan in markdown.AsSpan().EnumerateLines())
        {
            var line = lineSpan.ToString();
            var trimmed = line.AsSpan().TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                insideFence = !insideFence;
                withoutFences.AppendLine();
                continue;
            }

            if (insideFence)
            {
                withoutFences.AppendLine();
                continue;
            }

            withoutFences.AppendLine(line);
        }

        var text = withoutFences.ToString();

        // 顺序有讲究：先把链接与图片的「目标」丢掉只留文字，再清行首标记，最后统一压空白。
        text = InlineCode().Replace(text, "$1");
        text = Image().Replace(text, "$1");
        text = Link().Replace(text, "$1");
        text = HtmlTag().Replace(text, " ");
        text = HorizontalRule().Replace(text, " ");
        text = HeadingMarker().Replace(text, string.Empty);
        text = BlockquoteMarker().Replace(text, string.Empty);
        text = TaskCheckbox().Replace(text, string.Empty);
        text = ListMarker().Replace(text, string.Empty);
        text = EmphasisMarker().Replace(text, string.Empty);
        text = WhitespaceRun().Replace(text, " ");

        return text.Trim();
    }

    // 多行模式下所有「行内空白」都用 [ \t] 而不是 \s —— 后者会把换行也吃掉，
    // 导致 ^ 与 $ 的锚点行为与预期不符。

    [GeneratedRegex(@"^#{1,6}[ \t]*", RegexOptions.Multiline)]
    private static partial Regex HeadingMarker();

    [GeneratedRegex(@"^(?:>[ \t]*)+", RegexOptions.Multiline)]
    private static partial Regex BlockquoteMarker();

    [GeneratedRegex(@"^(?:[-*+][ \t]+|\d+[.)][ \t]+)", RegexOptions.Multiline)]
    private static partial Regex ListMarker();

    [GeneratedRegex(@"^\[[ xX]\][ \t]*", RegexOptions.Multiline)]
    private static partial Regex TaskCheckbox();

    /// <summary>三个及以上的 <c>-</c>、<c>*</c>、<c>_</c> 单独成行，是分隔线。</summary>
    [GeneratedRegex(@"^[ \t]*(?:[-*_][ \t]*){3,}$", RegexOptions.Multiline)]
    private static partial Regex HorizontalRule();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)", RegexOptions.None)]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)", RegexOptions.None)]
    private static partial Regex Link();

    [GeneratedRegex(@"</?[A-Za-z][^>]*>", RegexOptions.None)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"`([^`]*)`", RegexOptions.None)]
    private static partial Regex InlineCode();

    /// <summary>强调与删除线的符号本身。不需要成对匹配，逐个去掉即可。</summary>
    [GeneratedRegex(@"(\*\*|__|~~|\*|_)", RegexOptions.None)]
    private static partial Regex EmphasisMarker();

    /// <summary>
    /// 最后一步把所有空白（含换行）压成单个空格，让纯文本是单行——
    /// §12.3 的摘要与高亮基于它做片段截取，单行比多行好处理得多。
    /// 因为是收尾操作，这里用 <c>\s</c> 是安全的，不存在破坏锚点的问题。
    /// </summary>
    [GeneratedRegex(@"\s+", RegexOptions.None)]
    private static partial Regex WhitespaceRun();
}
