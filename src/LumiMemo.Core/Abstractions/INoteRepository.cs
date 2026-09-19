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
    /// <remarks>
    /// <para>
    /// 返回的不只是「读到了什么」，还有<strong>磁盘相对本程序上次同步这个文件变了没有</strong>
    /// （见 <see cref="NoteFileSync.DiskChanged"/>）。这一格是判断「这次事件是不是我们自己写盘
    /// 引起的」的唯一依据（§10.3），也是 §11.4 三路比较里的基线。做成返回值而不是让调用方
    /// 自己去问，是因为那个判据只有仓储层手里的「上次读/写的字节」算得出来。
    /// </para>
    /// <para>
    /// 文件已被外部删除时 <see cref="NoteFileSync.Note"/> 为 <c>null</c>，
    /// 且 <see cref="NoteFileSync.DiskChanged"/> 为 <see langword="true"/>——
    /// 从「有一份内容」变成「没有」本身就是变化。这不至于误伤：内存里根本没有这个路径时，
    /// 上层拿到「没了」的结论也无事可做。
    /// </para>
    /// <para>
    /// 本方法<strong>不</strong>回写文件：外部编辑是用户自己的动作，我们只同步内存。
    /// 反过来立刻写回会和用户的编辑器抢文件（§10.3）。
    /// </para>
    /// </remarks>
    Task<NoteFileSync> ReloadAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// 这个路径是不是本程序该管的便签文件（§5.7、§10.1）。
    /// </summary>
    /// <remarks>
    /// 给文件监听用。<c>FileSystemWatcher</c> 报的是<strong>目录里发生的一切</strong>，
    /// 而扫描器（<c>LoadAllAsync</c>）收的是另一个集合：它以 <c>.</c> 开头的目录
    /// （用户的 <c>.obsidian</c>、<c>.git</c>）、保留的附件目录、<c>.lumitmp</c> 残留、
    /// 以及非 <c>.md</c> 的文件全都不算便签。两处判据必须是<strong>同一套</strong>——
    /// 监听器比扫描器松的话，用户改一下 Obsidian 的配置就会在便签列表里冒出一张
    /// 重启后又不存在的幽灵便签。
    /// </remarks>
    bool IsNoteFile(string path);

    /// <summary>把便签写回磁盘，含 Front Matter（§11.2 的原子保存）。</summary>
    Task SaveAsync(Note note, CancellationToken ct = default);

    /// <summary>
    /// 把磁盘上这一版另存成一个冲突副本，用于「覆盖外部版本」之前留底（§11.4）。
    /// </summary>
    /// <param name="path">被覆盖的那个便签文件的完整路径。</param>
    /// <param name="ct">取消标记。</param>
    /// <returns>副本的完整路径；原文件已经不在时返回 <c>null</c>。</returns>
    /// <remarks>
    /// <para>
    /// 「覆盖外部版本」是<strong>破坏性</strong>的那一档，而用户点它的时候心里想的是
    /// 「我这份才是对的」——万一他想错了，磁盘上那份就是回来找的唯一线索。
    /// 所以覆盖之前先把它留在旁边，而不是就地抹掉。
    /// </para>
    /// <para>
    /// 副本落在<strong>同一个目录</strong>里
    /// （<c>{无扩展名的文件名}.conflict-{yyyyMMdd-HHmmss}.md</c>）：
    /// 换一个目录用户就永远找不到它。代价是它下次扫描时会作为一张新便签出现
    /// ——这是知情的选择，比悄悄丢掉一边强。
    /// </para>
    /// <para>
    /// <strong>读不出来（权限、被独占锁定）与写不出去都抛异常</strong>，不吞、也不返回
    /// <see langword="null"/>：调用方要靠它决定「副本没留成，那就别覆盖了」。
    /// 只有「原文件已经不在」才是 <see langword="null"/>——那不是失败，是没有东西可留底。
    /// </para>
    /// </remarks>
    Task<string?> BackupConflictCopyAsync(string path, CancellationToken ct = default);

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
