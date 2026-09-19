using LumiMemo.Core.Models;

namespace LumiMemo.Core.Abstractions;

/// <summary>
/// 便签的磁盘读写（§3.3 的两条数据流）。实现是 <c>MarkdownNoteRepository</c>，在 Infrastructure 层。
/// </summary>
/// <remarks>
/// <para>
/// 实现必须走 <c>AtomicFileWriter</c>（§11.2），<strong>不允许</strong>
/// 直接用 <c>File.WriteAllText</c> 保存便签（§24.3 明令禁止）——
/// 否则断电或强杀进程会留下空文件或半截文件。
/// </para>
/// <para>
/// 写入还必须遵守 §5.9 的「四者原样保留」：编码、行尾、BOM、末尾换行。
/// 只要有一项被顺手规范化，用户的 git diff 就会显示整个文件被改写。
/// </para>
/// </remarks>
public interface INoteRepository
{
    /// <summary>
    /// 扫描笔记目录，解析全部 <c>.md</c> 文件（§5.5 启动流程的第 1–2 步）。
    /// </summary>
    /// <remarks>
    /// 必须跳过所有以 <c>.</c> 开头的目录（<c>.lumimemo</c>、用户的 <c>.obsidian</c>、<c>.git</c>）
    /// 以及保留的附件目录（§5.7）。解析失败的文件按 §5.10 的降级矩阵处理，
    /// <strong>不能因为一个文件坏了就整体失败</strong>。
    /// </remarks>
    Task<IReadOnlyList<Note>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>重新读取单个文件，用于外部修改后同步（§3.3 流 2）。</summary>
    /// <returns>文件已被外部删除时返回 <c>null</c>。</returns>
    Task<Note?> ReloadAsync(string path, CancellationToken ct = default);

    /// <summary>把便签写回磁盘，含 Front Matter（§11.2 的原子保存）。</summary>
    Task SaveAsync(Note note, CancellationToken ct = default);

    /// <summary>
    /// 在笔记目录里建一张新的空白便签，并立刻落盘（§5.6、§3.3 流 3）。
    /// </summary>
    /// <param name="color">颜色；<see langword="null"/> 时用设置里的默认色。</param>
    /// <param name="targetFolder">相对笔记目录根的目标文件夹；<see langword="null"/> 表示放在根目录。</param>
    /// <param name="ct">取消标记。</param>
    /// <returns>已经存在于磁盘上的那张便签。</returns>
    /// <remarks>
    /// <para>
    /// 「新建」之所以落在仓储而不是 <c>NoteService</c>：它要做的是三件纯文件系统的事——
    /// 在笔记目录里分配一个没被占用的文件名（§5.6）、探一次路径是否已存在、
    /// 把第一次内容写出去。Core 不碰文件系统（§3.1），所以这里只能是仓储的活。
    /// </para>
    /// <para>
    /// 文件名按 §5.6 的 <c>{标题摘要}-{创建日期}-{短ID}.md</c> 分配。
    /// 初始正文为空，因此标题派生结果是 <c>无标题</c>。
    /// 图片等资源目录（<c>attachments/</c>）不在这里建（§5.7）。
    /// </para>
    /// <para>
    /// 笔记目录尚未配置、或配置的路径已经不存在时，实现应当抛
    /// <see cref="InvalidOperationException"/>，<strong>而不是默默重建目录</strong>——
    /// 后者会在被拔掉的移动盘上凭空造一个空目录，而用户的便签都在别处（§11.5）。
    /// </para>
    /// </remarks>
    Task<Note> CreateAsync(NoteColor? color = null, string? targetFolder = null,
        CancellationToken ct = default);
}
