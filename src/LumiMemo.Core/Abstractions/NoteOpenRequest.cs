using LumiMemo.Core.Models;

namespace LumiMemo.Core.Abstractions;

/// <summary>
/// App 层开窗所需的最小信息：显示哪个便签、用哪份布局（§14.2）。
/// </summary>
/// <remarks>
/// <para>
/// 存在的意义是把「业务判断」和「开窗」解耦：<see cref="INoteService"/> 交出本结构，
/// App 层拿去构造 ViewModel 并开窗，两边谁都不需要认识对方的类型。
/// </para>
/// <para>
/// 这也是绕开构造循环的关键——<c>NoteService</c> 在 Core、<c>NoteViewModelFactory</c> 与
/// <c>WindowManager</c> 在 App。若让前者直接调后者，Core 就引用了 App（违反 §3.1 规则 1）；
/// 若让后者反过来依赖前者，就成了 DI 容器无法解析的循环依赖（§14.1）。
/// </para>
/// </remarks>
/// <param name="Note">要显示的便签，来自 <c>NoteStore</c> 的唯一实例。</param>
/// <param name="Layout">该便签的布局，<c>IsOpen</c> 已被置为 true。</param>
public readonly record struct NoteOpenRequest(Note Note, NoteLayout Layout);
