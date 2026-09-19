using LumiMemo.Core.Models;

namespace LumiMemo.App.Abstractions;

/// <summary>
/// 外部文件变化落到内存与界面上的入口（§10.2、§11.4）。
/// </summary>
/// <remarks>
/// <para>
/// 文件监听（<c>FileWatchService</c>）只负责「谁变了、变了之后是什么内容」，结论交到这里；
/// 开窗、关窗、弹冲突对话框都是这里的事。两者之间隔一个接口而不是直接注入
/// <c>ManagerViewModel</c>，理由与 <see cref="IDispatcher"/> 同源：监听那边的去抖、
/// 去重、自写抑制、溢出恢复都要能在无 WPF 的测试进程里单独验，
/// 不该为了测它们把整个管理器连同窗口层一起装起来。
/// </para>
/// <para>
/// <strong>两个方法都只允许在 UI 线程上调用</strong>（§3.4 规则 T5）：实现要写
/// <c>NoteStore</c>、动 <c>ObservableCollection</c>、开窗，三样都绑死在 UI 线程上。
/// 因此监听器必须在调用之前封送，不能图省事在线程池线程上直接调。
/// </para>
/// </remarks>
public interface IExternalChangeSink
{
    /// <summary>
    /// 应用一批外部变化：每一项是「一个路径」加上「刚读到的磁盘内容」。
    /// </summary>
    /// <param name="batch">
    /// 一个去抖窗口内攒下来的一批，按事件到达顺序。
    /// <c>Sync.Note</c> 为 <see langword="null"/> 表示那个路径上的文件已经没有了。
    /// </param>
    /// <remarks>
    /// 已经是同一份内容的项不该出现在这里——自写抑制在读盘那一步就做掉了
    /// （<c>NoteFileSync.DiskChanged</c> 为假的项不会进这个参数）。
    /// </remarks>
    Task ApplyExternalChangesAsync(IReadOnlyList<(string Path, NoteFileSync Sync)> batch);

    /// <summary>
    /// 重新扫描整个笔记目录，把内存里的一切与磁盘对齐（§10.4、§10.5）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 <see cref="ApplyExternalChangesAsync"/> 的区别在于<strong>它不需要知道是哪些路径变过</strong>。
    /// 监听缓冲区溢出之后，丢掉的恰恰就是「哪些文件动过」这条信息，只能整目录重来一遍。
    /// </para>
    /// <para>
    /// 托盘的「重新加载全部便签」走的也是这一个：那条路是网络盘用户的兜底
    /// （§10.5 的轮询与提示本轮没做），与溢出恢复要做的事本来就是同一件。
    /// </para>
    /// </remarks>
    Task ApplyFullRescanAsync();
}
