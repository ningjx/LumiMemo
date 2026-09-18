using LumiMemo.Core.Models;

namespace LumiMemo.Core.Events;

/// <summary>
/// 一张便签的内容或元数据发生变化——可能是用户编辑、也可能来自外部编辑器（§3.3 流 2）。
/// </summary>
/// <remarks>
/// <para>
/// 订阅方用本事件应用业务规则：颜色变了吗？标题变了吗？需要更新索引吗？
/// 然后由 ViewModel 更新属性，靠 <c>PropertyChanged</c> 让 WPF 绑定刷新。
/// </para>
/// <para>
/// 因为传的是 <c>NoteStore</c> 里的同一个实例，订阅方看到的就是最新值，
/// 不存在「事件里的内容比 Store 旧」这种时序问题（§18.4）。
/// </para>
/// </remarks>
/// <param name="Note"><c>NoteStore</c> 中的那个实例，不是副本。</param>
public sealed record NoteChanged(Note Note);
