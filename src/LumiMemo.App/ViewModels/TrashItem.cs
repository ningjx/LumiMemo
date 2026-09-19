using System.IO;
using LumiMemo.Core.Models;

namespace LumiMemo.App.ViewModels;

/// <summary>
/// 回收站列表里的一行（§7.4）。
/// </summary>
/// <remarks>
/// <para>
/// 它存在的理由是<strong>格式化只做一次</strong>：列表重建时每行都要算标题、副标题、
/// 大小文案，把这几件事写在 XAML 的绑定里会让模板长出一堆 <c>Converter</c>，
/// 而那些转换器在无头测试里又验不了。
/// </para>
/// <para>
/// 不可变。回收站列表是整体重建的（§15.8 同理），没有「某一行悄悄变了」这种情况。
/// </para>
/// </remarks>
public sealed class TrashItem
{
    /// <summary>1 KB。用 1024 而不是 1000，与资源管理器显示的一致。</summary>
    private const double Kilobyte = 1024;

    public TrashItem(TrashEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        Entry = entry;

        // 目录级条目没有扩展名可言，GetFileNameWithoutExtension 对目录名正好是原样返回。
        Title = Path.GetFileNameWithoutExtension(entry.OriginalRelativePath);

        // 索引里的路径可能是被手改过的、也可能是个空串（条目重建失败）。
        // 空标题的行在列表里就是一个空行，用户完全不知道那是什么。
        if (string.IsNullOrWhiteSpace(Title))
        {
            Title = entry.TrashName;
        }

        SizeText = FormatSize(entry.Size);

        // 时刻一律按用户本地时区显示。索引里存的是带偏移的 DateTimeOffset，
        // 用户看到的必须是「我当时是几点删的」，而不是某个别的时区的钟点。
        DeletedAtText = entry.DeletedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

        LocationText = DescribeLocation(entry);
    }

    /// <summary>原始的索引条目。恢复与冲突判定都要把它交回 <c>TrashService</c>。</summary>
    public TrashEntry Entry { get; }

    /// <summary>主标题：文件名去掉扩展名。</summary>
    public string Title { get; }

    /// <summary>大小，形如 <c>2.1 MB</c>。目录级条目是子树的总大小。</summary>
    public string SizeText { get; }

    /// <summary>删除时刻，形如 <c>2026-09-19 14:30</c>。</summary>
    public string DeletedAtText { get; }

    /// <summary>原来在哪，形如 <c>归档/周报.md</c>。</summary>
    public string LocationText { get; }

    /// <summary>条目所指的是不是一整棵目录（§5.7 的「整个文件夹移到回收站」）。</summary>
    public bool IsDirectory => Entry.Kind == TrashEntryKind.Directory;

    /// <summary>
    /// 文件已经不在 <c>trash/</c> 里了（§7.2 的一致性检查结果）。
    /// </summary>
    /// <remarks>
    /// 这种条目<strong>必须仍然显示出来</strong>，不能被悄悄隐藏：用户手动把文件拖回笔记目录
    /// 之后，索引里这一条就成了孤儿，而「恢复」对它是没有意义的（文件已经在原位了）。
    /// 界面要给的是「这条已经不在回收站里了」，让用户知道该删掉它。
    /// </remarks>
    public bool IsFileMissing => Entry.IsFileMissing;

    /// <summary>类型标签，给列表左侧那个小图标用。</summary>
    public string KindText => IsDirectory ? "文件夹" : "便签";

    /// <summary>把字节数写成人类看得懂的形式。</summary>
    /// <remarks>
    /// 逐级降到合适的单位，超过 1 GB 就不再往上（回收站里的东西以 MB 计，
    /// 出现 TB 说明用户的索引已经不正常了，那时显示什么单位都无所谓）。
    /// </remarks>
    public static string FormatSize(long bytes)
    {
        if (bytes < Kilobyte)
        {
            return $"{bytes} B";
        }

        double value = bytes / Kilobyte;
        string unit = "KB";

        if (value >= Kilobyte)
        {
            value /= Kilobyte;
            unit = "MB";
        }

        if (value >= Kilobyte)
        {
            value /= Kilobyte;
            unit = "GB";
        }

        return $"{value:0.#} {unit}";
    }

    private static string DescribeLocation(TrashEntry entry) =>
        string.IsNullOrWhiteSpace(entry.OriginalRelativePath)
            ? "（原位置未知）"
            : entry.OriginalRelativePath.Replace('\\', '/');
}
