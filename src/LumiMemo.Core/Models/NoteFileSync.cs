namespace LumiMemo.Core.Models;

/// <summary>
/// 一次「按路径读盘」的结果，以及它与<strong>本程序上次同步这个文件时的状态</strong>的差异（§10、§11.4）。
/// </summary>
/// <param name="Note">
/// 磁盘上此刻那一版；文件已经不在时是 <see langword="null"/>。
/// </param>
/// <param name="DiskChanged">
/// 磁盘是否真的变了。
/// </param>
/// <remarks>
/// <para>
/// <strong><see cref="DiskChanged"/> 是 §10.3「自写抑制」与 §11.4「三路比较」共用的那一格。</strong>
/// 它由仓储层算出来，判据只有一条：<em>磁盘此刻的字节是否等于本程序上次读或写这个路径时的字节</em>
/// （那份哈希是 <c>MarkdownNoteRepository</c> 一直在维护的 <c>NoteFileState.ContentHash</c>）。
/// 相等就说明这次事件是我们自己写盘引起的，或者是同一份内容的重复通知，直接丢掉。
/// </para>
/// <para>
/// <strong>为什么不按文档的「写入抑制窗口」做</strong>：文档 §10.3 的第一层是「自己写完 3 秒内
/// 收到的事件一律丢弃」，那会连同「我们保存后 3 秒内外部对同一个文件的真实修改」一起丢掉——
/// 正是本模块要防的那种数据丢失（典型例子：保存完立刻 <c>git checkout</c>，整批变更被吞掉）。
/// 按字节比则不会：它比的是<strong>当前</strong>磁盘状态，迟到的旧事件再比一次也只会得出同样的结论。
/// 理由详见开发方案 §10 的实现说明。
/// </para>
/// </remarks>
public sealed record NoteFileSync(Note? Note, bool DiskChanged);
