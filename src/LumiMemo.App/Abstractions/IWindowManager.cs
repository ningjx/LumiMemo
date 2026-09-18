using LumiMemo.Core.Models;
using LumiMemo.App.ViewModels;

namespace LumiMemo.App.Abstractions;

/// <summary>
/// 便签窗口的物理管理：创建、显示、隐藏、销毁、摆放（§14.2）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>接口位置的设计修正</strong>：§3.1 与 §4.2 把 <see cref="IWindowManager"/> 列在 Core 的
/// <c>Abstractions/</c> 下，但 §14.2 的签名里出现了 <c>NoteViewModel</c>——那是 App 层的类型。
/// 把接口放 Core 会迫使 Core 引用 App，直接违反 §3.1 规则 1（依赖只能向下）。
/// 因此本接口放在 App 层，与同样「只为可测性存在」的 <see cref="IDispatcher"/>、
/// <see cref="IDialogService"/> 并列。
/// </para>
/// <para>
/// <strong>本接口不做业务判断</strong>：不创建便签、不删除便签、不写 <c>layout.json</c>。
/// 「该不该开窗」由 <c>INoteService</c> 决定（§14.1）。<see cref="CloseNote"/> 只关窗口，
/// 置 <c>IsOpen = false</c> 与标记 layout 脏由 <c>INoteService.MarkNoteClosed</c> 负责（§17.3）。
/// </para>
/// <para>
/// <strong>它自己不维护注册表</strong>：<c>Guid → NoteWindow</c> 的映射在实现内部（§14.3），
/// 外部不需要也不应该插手，因此这里没有 Register / Unregister 之类的方法（§3.3）。
/// </para>
/// <para>
/// 接口存在的理由只有两个：让 ViewModel 依赖抽象而不是具体窗口层（§18.6），
/// 以及在测试里换成记录型替身。实现类本身没有第二个实现（§14.2）。
/// </para>
/// </remarks>
public interface IWindowManager
{
    /// <summary>为指定的 ViewModel 创建并显示一个便签窗口。若该便签已有窗口，则激活并返回。</summary>
    void ShowNote(NoteViewModel viewModel, NoteLayout layout);

    /// <summary>
    /// 关闭便签窗口。<strong>只动窗口，不改数据</strong>——不删除便签，也不写 layout。
    /// </summary>
    void CloseNote(Guid noteId);

    /// <summary>关闭所有便签窗口（不改变各自的 <c>IsOpen</c> 状态，用于会话级隐藏）。</summary>
    void HideAllNotes();

    /// <summary>
    /// 把<strong>已经存在</strong>的便签窗口全部恢复显示（被最小化的还原），并激活第一个。
    /// </summary>
    /// <remarks>
    /// 它不知道该显示哪些便签——「哪些便签应该打开」是 <c>INoteService.OpenAll</c> 的判断（§17.6）。
    /// </remarks>
    void ShowAllNotes();

    /// <summary>把窗口的当前几何信息写回 <paramref name="target"/>（物理像素 + DPI）。</summary>
    void CaptureGeometry(Guid noteId, NoteLayout target);

    /// <summary>应用折叠/展开后的尺寸变化。</summary>
    void ApplyCollapsed(Guid noteId, bool collapsed);

    /// <summary>应用置顶变化。</summary>
    void ApplyTopMost(Guid noteId, bool topMost);

    /// <summary>应用锁定变化。</summary>
    void ApplyLocked(Guid noteId, bool locked);

    /// <summary>尝试启用/禁用点击穿透。返回 <c>false</c> 表示当前环境不支持（§13.7 原型失败）。</summary>
    bool TryApplyClickThrough(Guid noteId, bool enabled);

    /// <summary>显示器配置发生变化（<c>WM_DISPLAYCHANGE</c>）时的重排。</summary>
    void OnDisplayConfigurationChanged();

    /// <summary>查询某个便签是否有打开的窗口。</summary>
    bool IsNoteOpen(Guid noteId);
}
