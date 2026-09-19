namespace LumiMemo.App.Messages;

/// <summary>
/// 「这张便签的置顶被别处改了」——挂着它的窗口该把自己的那一份跟着改过来。
/// </summary>
/// <param name="NoteId">被改的那张便签。</param>
/// <param name="IsTopMost">改成了什么。带上值而不是让接收方去读，是因为接收方此刻读到的
/// 可能还是旧值——发这条消息的场合，布局层的写入与消息的送达之间没有别的同步点。</param>
/// <remarks>
/// <para>
/// <strong>为什么需要它</strong>：置顶的真实状态在 <c>NoteLayout.IsTopMost</c> 上，
/// 而开着的便签窗口另存了一份镜像（<c>NoteViewModel.IsTopMost</c>，标题条那个拨动按钮绑的就是它）。
/// 镜像这一侧的改动会顺着 <c>WindowManager</c> 的 <c>OnViewModelPropertyChanged</c> 往下走
/// （真的把 HWND 设成 topmost、把布局标记为脏），但反过来——<strong>从布局层改</strong>——
/// 没有任何东西会通知窗口。于是管理器里点「置顶」，窗口既不置顶、按钮也还显示着未置顶，
/// 用户再点一下那个按钮，反而把它取消了。
/// </para>
/// <para>
/// <strong>只在这个方向发</strong>：便签窗口自己切换置顶时，管理器那边没有需要立刻改的东西
/// ——列表顺序按修改时间排，与置顶无关（置顶只在搜索结果里加 50 分，那是下一次搜索的事）。
/// 反方向也发一条的话，就得先有一个「谁先动的手」的判据，才能避免两边互相触发的循环。
/// </para>
/// <para>
/// <strong>与 <see cref="NotesChangedMessage"/> 的区别</strong>：那条是「有哪些便签变了」，
/// 接收方整表重读；这条是「某一张的某个字段变了」，接收方只改自己那一份。
/// 不要因为名字像就合并——合并之后管理器每切一次置顶都要整表重扫，而重扫会把用户
/// 正在看的那一行从选中状态里抖出去。
/// </para>
/// <para>
/// 载荷带 id 而不是直接带 <c>NoteLayout</c> 引用：接收方拿不到布局对象也不会出问题
/// （窗口没开着时压根没有接收方），而带引用会让「这条消息能不能跨线程用」变成一个额外要判的事。
/// </para>
/// </remarks>
public sealed record NoteTopMostChangedMessage(Guid NoteId, bool IsTopMost);
