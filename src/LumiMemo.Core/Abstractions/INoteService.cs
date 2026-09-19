using LumiMemo.Core.Models;

namespace LumiMemo.Core.Abstractions;

/// <summary>
/// 便签业务的唯一入口（§14.2）。所有「该不该开窗、该不该落盘」的判断都在这里。
/// </summary>
/// <remarks>
/// <para>
/// 分工要点：本接口<strong>不构造 ViewModel、不碰窗口</strong>，也<strong>不直接移动回收站文件</strong>
/// （那是 <c>TrashService</c> 的事），它只编排顺序与内存状态。
/// </para>
/// <para>
/// 开窗为什么拆成「Core 判断 + App 开窗」两步，见 <see cref="NoteOpenRequest"/> 的说明。
/// </para>
/// </remarks>
public interface INoteService
{
    // ---- 磁盘 → 内存 ----

    /// <summary>
    /// 扫描笔记目录并把结果<strong>合并进</strong> <c>NoteStore</c>（§5.5 的启动补给流程、§17.1 的启动顺序）。
    /// </summary>
    /// <param name="ct">取消标记。</param>
    /// <returns>
    /// 内存相对磁盘发生过的变化，逐条列出（新增 / 重载 / 删除 / 冲突）。
    /// 启动时 Store 是空的，因此它整份都是「新增」，调用方直接丢掉即可。
    /// </returns>
    /// <remarks>
    /// <para>
    /// 实现必须遵守 §5.5 的顺序：解析 → 补写缺失的 id → 处理 id 冲突 → 建 Store。
    /// 注意 <strong>watcher 由调用方在这之后才启动</strong>，因为本方法会修改一批用户文件，
    /// 若 watcher 已在运行会立刻收到一堆 Changed 事件，与自写抑制逻辑叠加后时序极难调试。
    /// </para>
    /// <para>
    /// <strong>是「合并」而不是「清空重建」</strong>（§10.4 的差异比对就是它）：
    /// 同 id 的便签把磁盘版的字段搬进<strong>内存里那个实例</strong>，只有磁盘上多出来的才新建、
    /// 已经不在磁盘上的才摘除。清空重建会换掉 <c>Note</c> 对象，而便签窗口绑的正是那个引用——
    /// 用户在窗口里改的字既进不了 Store 也存不下去，等于「重新加载全部便签」在有未保存改动的
    /// 便签上丢数据。这是本轮修掉的一个既有缺陷。
    /// </para>
    /// <para>
    /// 有未落盘改动、且磁盘版本不同的便签<strong>会被报成冲突而不是被覆盖</strong>，
    /// 由调用方去问用户——与文件监听走同一套判定。
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<ExternalChangeResult>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>
    /// 把一个外部文件变化应用到内存状态（§3.3 流 2 的终点、§10.2、§11.4）。
    /// </summary>
    /// <param name="path">文件的完整路径，用于定位 <c>NoteStore</c> 中的便签。</param>
    /// <param name="fileSync">这次读盘的结果，以及它与本程序上次同步这个文件的差异。</param>
    /// <returns>
    /// 内存变成了什么样。<see cref="ExternalChangeKind.None"/> 表示磁盘其实没变——
    /// 自写事件、或者同一份内容的重复通知，调用方可以什么都不做。
    /// </returns>
    /// <remarks>
    /// <para>
    /// <strong>判定不靠时间戳，靠三样东西</strong>（§11.4）：磁盘现在是什么
    /// （<paramref name="fileSync"/>）、本程序上次同步这个文件时它是什么
    /// （<paramref name="fileSync"/> 的 <c>DiskChanged</c>）、以及内存这一份有没有没落盘的改动。
    /// 三者组合出「忽略 / 静默重载 / 冲突」三种结论，见 <see cref="ExternalChangeKind"/>。
    /// </para>
    /// <para>
    /// <strong>它只改内存，不弹任何界面</strong>：冲突时它停下来，把两边都交回给调用方，
    /// 由 App 层去问用户（Core 不认识对话框与窗口，§3.1）。
    /// </para>
    /// </remarks>
    ExternalChangeResult ApplyExternalChange(string path, NoteFileSync fileSync);

    /// <summary>
    /// §11.4 冲突处置的第一档「重新加载」：采用磁盘那一版，丢掉本地的改动。
    /// </summary>
    /// <remarks>
    /// 内容来自 <paramref name="conflict"/> 里带着的那一版磁盘便签——它是发现冲突时读到的，
    /// 不是现在重读的。这中间用户又改了文件的话，下一次事件会再走一遍判定，不会漏。
    /// </remarks>
    void ResolveConflictByReload(ExternalChangeResult conflict);

    /// <summary>
    /// §11.4 冲突处置的第二档「覆盖外部版本」：把本地的改动写回磁盘，磁盘那一版先另存一份副本。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>先备份再覆盖</strong>，而且备份失败就不覆盖：这一档是破坏性的
    /// （磁盘上的内容即将消失），留一份副本是它唯一的退路。
    /// </para>
    /// <para>
    /// 备份文件名与位置由 <see cref="INoteRepository.BackupConflictCopyAsync"/> 定。
    /// </para>
    /// </remarks>
    Task ResolveConflictByOverwriteAsync(ExternalChangeResult conflict, CancellationToken ct = default);

    // ---- 编辑 → 内存（不落盘，落盘由 <see cref="SaveNoteAsync"/> 负责）----

    /// <summary>
    /// 应用一次用户编辑。只改内存并更新派生缓存，<strong>不写磁盘</strong>。
    /// </summary>
    void ApplyLocalEdit(Note note, string content);

    /// <summary>
    /// 改一张便签的颜色（§15.8 右键菜单）。只改内存，<strong>不写磁盘</strong>。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ApplyLocalEdit"/> 一样会刷新 <see cref="Note.UpdatedAt"/>：颜色与标签
    /// 都是<strong>用户数据</strong>（§0.2），改了它们就是改了这张便签，管理器的
    /// 「最近更新」排序与 §12.2 的「七天内 +30」都该跟着动。
    /// </remarks>
    void ApplyColorEdit(Note note, NoteColor color);

    /// <summary>
    /// 改一张便签的标签（§15.8 右键菜单）。只改内存，<strong>不写磁盘</strong>。
    /// </summary>
    /// <param name="tags">
    /// 新标签。可以传用户原样输入的内容，实现会按 §5.8 规范化、去空、忽略大小写去重——
    /// 「<c>Note.Tags</c> 里永远是规范形式」这条不变量由本方法守住，
    /// 而不是指望每个调用方都记得先调一次 <c>TagRules</c>。
    /// </param>
    /// <remarks>传空集合就是<strong>清空标签</strong>，不是「不改动」。</remarks>
    void ApplyTagsEdit(Note note, IReadOnlyList<string> tags);

    // ---- 内存 → 磁盘 ----

    Task SaveNoteAsync(Guid noteId);

    Task SaveAllAsync(CancellationToken ct = default);

    // ---- 业务操作 ----

    /// <param name="color">颜色。缺省时用设置里的默认色（§8.2 的 <c>defaultColor</c>）。</param>
    /// <param name="targetFolder">目标文件夹的相对路径。缺省时放笔记目录根。</param>
    Task<Note> CreateNoteAsync(NoteColor? color = null, string? targetFolder = null);

    /// <summary>移动便签到另一个文件夹，并重写正文里的附件相对链接（§6.3）。</summary>
    Task MoveNoteAsync(Guid noteId, string targetFolder);

    /// <summary>
    /// 删除便签，即把 Markdown 文件移到回收站（§7.1）。不等于关闭窗口。
    /// </summary>
    /// <remarks>
    /// 真正的搬运与内存摘除都在 <c>TrashService</c>，这里只转发。
    /// 保留这个入口是为了让「按便签 id 办事」的调用方（管理器）不必认识回收站。
    /// 关窗与撤掉待落盘的那一轮保存由调用方接着做——那两件事一件在 App 层、
    /// 一件在便签自己的 ViewModel 里，都不属于本层。
    /// </remarks>
    Task DeleteNoteAsync(Guid noteId);

    /// <summary>把回收站里的便签恢复到笔记目录（§7.3）。</summary>
    /// <param name="noteId">便签 id。回收站索引里按它找条目。</param>
    /// <param name="targetPath">
    /// 恢复到哪个相对路径。缺省时回到 <c>OriginalRelativePath</c>；
    /// 给一个值就是 §7.3 对话框里「恢复到笔记目录根」那一档。
    /// 目标已被占用时是<strong>重命名</strong>而不是覆盖。
    /// </param>
    Task RestoreFromTrashAsync(Guid noteId, string? targetPath);

    // ---- 窗口开关：业务判断在这里，真正的开/关窗口由 App 层发起方接着调用 IWindowManager ----

    /// <summary>校验存在性、把 <c>layout.IsOpen</c> 置 true，然后交出开窗所需的信息（§3.3 流 3）。</summary>
    /// <returns>便签不存在时返回 <c>null</c>。</returns>
    NoteOpenRequest? OpenNote(Guid noteId);

    /// <summary>托盘菜单与热键的「显示全部」用（§17.6）。</summary>
    IReadOnlyList<NoteOpenRequest> OpenAll();

    /// <summary>关闭便签<strong>窗口</strong>时调用：置 <c>IsOpen = false</c> 并标记布局脏（§17.3）。</summary>
    void MarkNoteClosed(Guid noteId);
}
