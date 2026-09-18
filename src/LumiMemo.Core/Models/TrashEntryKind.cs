namespace LumiMemo.Core.Models;

/// <summary>
/// 回收站条目的类型（§7.2）。
/// </summary>
/// <remarks>
/// 目录级删除是整体移动（不是逐个移文件），因此索引里需要区分两种条目——
/// 恢复时的行为完全不同：文件级是 <c>File.Move</c>，目录级是 <c>Directory.Move</c>。
/// </remarks>
public enum TrashEntryKind
{
    /// <summary>单文件删除。</summary>
    File,

    /// <summary>目录级删除，整个文件夹连同其中的便签一起进回收站（§5.7）。</summary>
    Directory,
}
