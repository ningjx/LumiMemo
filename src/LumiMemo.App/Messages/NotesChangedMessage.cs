namespace LumiMemo.App.Messages;

/// <summary>
/// 「笔记目录里的便签集合变了」——多了一条或少了<strong>别人</strong>删掉的一条，
/// 持有列表的界面该重读一遍。
/// </summary>
/// <remarks>
/// <para>
/// <strong>为什么需要它</strong>：<c>ManagerViewModel.Notes</c> 是 <c>Refresh()</c> 从
/// <c>NoteStore</c> 拷出来的<strong>快照</strong>，不是 <c>NoteStore</c> 本身。
/// MVVM 的 <c>INotifyCollectionChanged</c> 只负责「我改了这份快照，界面跟着变」，
/// <strong>不管</strong>「别人改了 <c>NoteStore</c>、我要不要重算快照」——
/// 而 <c>NoteStore</c> 与 <c>SearchIndex</c> 都不是可观察的，两边之间没有线。
/// 没有这条消息，回收站里恢复一张便签之后管理器毫无察觉，用户得手动刷新才看得见。
/// </para>
/// <para>
/// <strong>谁发谁不发</strong>：改了自己就刷自己（<c>ManagerViewModel</c> 的
/// 新建 / 删除 / 重扫都直接调 <c>Refresh()</c>，绕消息一圈只是多一层间接），
/// 只有<strong>别人改了、我没法知道</strong>时才发（<c>TrashViewModel</c> 的恢复）。
/// </para>
/// <para>
/// <strong>不带载荷</strong>：接收方的动作是「整表重读」，变了哪一条都一样。
/// 带上 id 反而会让人以为可以只改一行——而计数、空列表提示、溢出提示
/// 全挂在整表重算上，只改一行的话那几处都要各自维护一遍。
/// </para>
/// <para>
/// <strong>发送方在 App 层，不在 Core</strong>：§18.2 那张把消息归给
/// <c>NoteService</c> 的表做不到——<c>NoteService</c> 在 <c>LumiMemo.Core</c>，
/// 而 Core 零第三方依赖（§4.1），它调不到 <c>WeakReferenceMessenger</c>。
/// 能发消息的只有 App 层那几个真正改动便签集合的地方。
/// </para>
/// <para>
/// <strong>刻意不发「正文变了」</strong>：§18.2 表里另有一个 <c>NoteUpdatedMessage</c>，
/// 照做的话用户在便签里每敲一个字都会让管理器把整张列表重排一遍。
/// 管理器的列表只关心<strong>有哪些便签</strong>，不关心某一张的正文。
/// </para>
/// </remarks>
public sealed record NotesChangedMessage;
