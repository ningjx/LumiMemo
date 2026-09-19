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
    /// 扫描笔记目录并建立 <c>NoteStore</c>（§5.5 的启动补给流程、§17.1 的启动顺序）。
    /// </summary>
    /// <remarks>
    /// 实现必须遵守 §5.5 的顺序：解析 → 补写缺失的 id → 处理 id 冲突 → 建 Store。
    /// 注意 <strong>watcher 由调用方在这之后才启动</strong>，因为本方法会修改一批用户文件，
    /// 若 watcher 已在运行会立刻收到一堆 Changed 事件，与自写抑制逻辑叠加后时序极难调试。
    /// </remarks>
    Task LoadAllAsync(CancellationToken ct = default);

    /// <summary>把一个从磁盘读出的解析结果应用到内存状态（§3.3 流 2 的终点）。</summary>
    /// <param name="path">文件的完整路径，用于定位 <c>NoteStore</c> 中的便签。</param>
    /// <param name="readResult">解析器输出，尚未绑定 id 与路径。</param>
    void ApplyExternalChange(string path, NoteReadResult readResult);

    // ---- 编辑 → 内存（不落盘，落盘由 <see cref="SaveNoteAsync"/> 负责）----

    /// <summary>
    /// 应用一次用户编辑。只改内存并更新派生缓存，<strong>不写磁盘</strong>。
    /// </summary>
    void ApplyLocalEdit(Note note, string content);

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
