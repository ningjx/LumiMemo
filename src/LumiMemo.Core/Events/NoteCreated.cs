using LumiMemo.Core.Models;

namespace LumiMemo.Core.Events;

/// <summary>
/// 一张便签被创建（应用内新建，或外部新增的 <c>.md</c> 被扫描到）。
/// </summary>
/// <remarks>
/// <para>
/// 由 <c>NoteService</c> 在完成一次完整操作后发出，<strong>不是</strong>由 <c>NoteStore</c> 发出——
/// 「改了一半就通知」是必须避免的（§9.1）。
/// </para>
/// <para>
/// 订阅方（搜索索引、管理器列表、托盘菜单）必须已经在 UI 线程上（§3.4）。
/// </para>
/// </remarks>
/// <param name="Note"><c>NoteStore</c> 中的那个实例，不是副本。</param>
public sealed record NoteCreated(Note Note);
