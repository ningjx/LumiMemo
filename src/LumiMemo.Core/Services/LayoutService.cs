using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Math;
using LumiMemo.Core.Models;

namespace LumiMemo.Core.Services;

/// <summary>
/// 布局的<strong>决策层</strong>：窗口该摆哪，以及什么时候落盘（§8.5、§13.8）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="ILayoutStore"/> 的分工是这条线：<strong>存储层只搬运物理像素，
/// 完全不碰坐标换算，也不做定时</strong>；本类不认识 JSON、不碰文件，
/// 只负责「按 §13.8 算出该摆哪」与「按 §8.5 的一秒去抖决定何时写」。
/// 两边各占一半，谁都不越界。
/// </para>
/// <para>
/// 放置计算本身一行都不在本类里——它全部转交给 <see cref="LayoutMath"/>，
/// 那是坐标规则的唯一实现处。本类只是把「当前有哪些显示器」和「状态条开着没有」
/// 这两个运行时输入喂给它。
/// </para>
/// <para>
/// <strong>本类不记日志</strong>（Core 零第三方依赖）。放置结果里带着
/// <see cref="WindowPlacement.WasAdjusted"/>，该不该记一条由 App 层看那个标志决定。
/// </para>
/// </remarks>
public sealed class LayoutService : IDisposable
{
    private readonly ILayoutStore _store;
    private readonly IDisplayProvider _displays;
    private readonly IUiTimer _flushTimer;

    public LayoutService(ILayoutStore store, IDisplayProvider displays, IUiTimerFactory timers)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentNullException.ThrowIfNull(timers);

        _store = store;
        _displays = displays;
        _flushTimer = timers.Create();
    }

    /// <summary>去抖时长，默认 1 秒（§8.5）。</summary>
    /// <remarks>
    /// 做成可写属性而不是常量：它将来可能跟设置走，而设置是运行期能改的。
    /// </remarks>
    public TimeSpan FlushDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>状态条是否显示（§15.2），决定折叠态的窗口高度是 44 还是 36 DIP。</summary>
    /// <remarks>
    /// 与 <c>JsonLayoutStore.DefaultWidth</c> 同一手法：来自设置、运行期可改，
    /// 而它是每一次放置计算都要用的输入，做成 <see cref="ResolvePlacement"/> 的参数
    /// 等于让每个调用点都去搬同一个值。
    /// </remarks>
    public bool ShowStatusBar { get; set; } = true;

    /// <summary>载入 layout.json（§17.1 第 4 步）。</summary>
    public Task LoadAsync(CancellationToken ct = default) => _store.LoadAsync(ct);

    /// <summary>取布局，不存在则按设置里的默认尺寸新建一条（§3.3 流 3）。</summary>
    /// <remarks>
    /// 新建的条目必须落盘（§8.3 的 <c>isOpen</c> 默认为 true），因此这里顺手安排一次
    /// 去抖落盘：存储层负责把内存标脏，「什么时候真写」由本类持有的定时器决定。
    /// </remarks>
    public NoteLayout GetOrCreate(Guid noteId)
    {
        NoteLayout layout = _store.GetOrCreate(noteId);

        MarkDirtyAndScheduleFlush();

        return layout;
    }

    /// <summary>
    /// 取布局，不存在时返回 <c>null</c>，<strong>不产生任何副作用</strong>。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="GetOrCreate"/> 的区别不只是「少建一条」：它也不安排落盘。
    /// 需要「只是问一下」的调用方（恢复上次打开的窗口、关闭某张便签的窗口）必须走这里，
    /// 否则每问一次就往 layout.json 里多写一个尚不存在的条目。
    /// </remarks>
    public NoteLayout? TryGet(Guid noteId) => _store.TryGet(noteId);

    /// <summary>全部布局。显示器配置变化后的重排与管理器都要遍历它。</summary>
    public IReadOnlyCollection<NoteLayout> All => _store.All;

    /// <summary>
    /// 算出这张便签此刻该摆在哪（§13.8 的完整恢复算法）。
    /// </summary>
    /// <param name="saved">磁盘上的布局。<strong>本方法不修改它。</strong></param>
    /// <param name="cascadeIndex">
    /// 本次会话中这是第几张被层叠重排的便签。只在「原显示器已拔掉」那条路径上用得到，
    /// 正常情况下传 0 即可。
    /// </param>
    /// <remarks>
    /// 刻意<strong>不</strong>写回 <paramref name="saved"/>：原始坐标要留着，
    /// 用户把显示器插回来之后还得靠它把窗口摆回原处（§13.8）。
    /// 窗口真实几何的回写是 <c>WindowManager.CaptureGeometry</c> 的事（§14.4）。
    /// </remarks>
    public WindowPlacement ResolvePlacement(NoteLayout saved, int cascadeIndex) =>
        LayoutMath.Restore(saved, _displays.All, cascadeIndex, ShowStatusBar);

    /// <summary>
    /// 标记布局有改动，并安排一次一秒后的落盘（§8.5 的触发点）。
    /// </summary>
    /// <remarks>
    /// 拖动窗口期间这个方法会被调几十次，而磁盘最多只被碰一次：
    /// 每次调用都是<strong>重新计时</strong>，不是排队（<see cref="IUiTimer"/> 的约定）。
    /// </remarks>
    public void MarkDirtyAndScheduleFlush()
    {
        _store.MarkDirty();
        _flushTimer.Start(FlushDelay, OnFlushDue);
    }

    /// <summary>立即落盘，绕过去抖。退出流程必须调一次（§17.4）。</summary>
    /// <remarks>
    /// 同时取消还在等待的那一轮：已经写过了，再触发一次只是白写一遍。
    /// 本方法不抛异常——布局写失败不打断用户操作（§8.5），失败由存储层自己记日志。
    /// </remarks>
    public async Task FlushNowAsync(CancellationToken ct = default)
    {
        _flushTimer.Stop();

        await _store.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 只释放那个定时器，<strong>不落盘</strong>。
    /// </summary>
    /// <remarks>
    /// 容器释放单例的顺序与 §17.4 要求的退出顺序毫无关系，在这里顺手写一次盘
    /// 看起来是「兜底」，实际是在一个已经乱掉的关停序列里插入一次未知时机的文件写。
    /// 未落盘的改动由退出流程的 <see cref="FlushNowAsync"/> 负责，
    /// 本方法只保证没有定时器在关停之后还惦记着回调。
    /// </remarks>
    public void Dispose() => _flushTimer.Dispose();

    /// <summary>定时器到期：异步落盘，不等待。</summary>
    /// <remarks>
    /// 这里必须是「发出去就不管」：定时器回调的签名容不下 await。
    /// 之所以敢这么写，是因为 <c>ILayoutStore.FlushAsync</c> 的契约就是
    /// <strong>不抛异常</strong>（§8.5 的写入失败处理），否则会变成一个
    /// 谁也没观察到的 <see cref="Task"/>。
    /// </remarks>
    private void OnFlushDue() => _ = FlushNowAsync();
}
