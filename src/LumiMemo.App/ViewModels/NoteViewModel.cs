using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using LumiMemo.App.Abstractions;
using LumiMemo.App.Services;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;

namespace LumiMemo.App.ViewModels;

/// <summary>
/// 单张便签的界面状态：内容、标题、颜色、标签、保存状态、折叠/置顶/锁定状态、缩放（§18.1）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>三类状态严格分开</strong>（§18.1）：
/// </para>
/// <list type="bullet">
///   <item><c>Content</c> / <c>Color</c> / <c>Tags</c> 属于 <see cref="Note"/>，落盘到 Markdown。</item>
///   <item><c>IsCollapsed</c> / <c>IsTopMost</c> / <c>IsLocked</c> / <c>ContentScale</c> 属于
///   <see cref="NoteLayout"/>，落盘到 <c>layout.json</c>。</item>
///   <item><see cref="SaveStatus"/> / <see cref="IsComposing"/> / <see cref="IsDirty"/> /
///   <see cref="CaretIndex"/> 只属于本类，<strong>绝不落盘</strong>。</item>
/// </list>
/// <para>
/// <strong>不持有窗口引用</strong>（§18.3）。需要操作窗口时走 <see cref="IWindowManager"/>，
/// 用 <see cref="Id"/> 查找。这样对窗口的依赖是「接口 + 标识符」而不是强引用，
/// 窗口池复用才有可能（§13.9）。
/// </para>
/// <para>
/// <strong>不缓存 <see cref="Note"/> 的派生值</strong>：<see cref="Title"/> 每次都问 <see cref="Note"/>，
/// 因为 <see cref="Note.Title"/> 自己带缓存（§5.4）。
/// </para>
/// </remarks>
public sealed partial class NoteViewModel : ObservableObject, IDisposable
{
    private readonly INoteService _noteService;
    private readonly AutoSaveService _autoSaveService;
    private readonly IDispatcher _dispatcher;
    private readonly IDialogService _dialogService;
    private readonly IWindowManager _windowManager;
    private readonly IMessenger _messenger;

    private bool _isDisposed;

    /// <param name="note">本便签的数据模型。ViewModel 与它共存亡。</param>
    /// <param name="layout">本便签的设备状态。折叠/置顶/缩放直接读写它。</param>
    /// <param name="messenger">
    /// 消息总线。<strong>刻意注入而不是直接用 <c>WeakReferenceMessenger.Default</c></strong>：
    /// 那个静态单例是全进程共享的隐藏依赖，测试并行跑时不同用例会共用同一条总线，
    /// 注册与注销互相干扰，故障还只在特定执行顺序下出现。
    /// 组合根注册的是 <c>WeakReferenceMessenger.Default</c>，行为与直接调用它完全一致。
    /// </param>
    public NoteViewModel(
        Note note,
        NoteLayout layout,
        INoteService noteService,
        AutoSaveService autoSaveService,
        IDispatcher dispatcher,
        IDialogService dialogService,
        IWindowManager windowManager,
        IMessenger messenger)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(layout);

        Note = note;
        Layout = layout;
        _noteService = noteService;
        _autoSaveService = autoSaveService;
        _dispatcher = dispatcher;
        _dialogService = dialogService;
        _windowManager = windowManager;
        _messenger = messenger;

        _content = note.Content;

        // 初始状态直接来自模型，不走属性 setter——否则构造期间就会标记脏、触发一次保存。
        _color = note.Color;
        _isCollapsed = layout.IsCollapsed;
        _isTopMost = layout.IsTopMost;
        _isLocked = layout.IsLocked;
        _contentScale = layout.ContentScale;
    }

    /// <summary>本便签的数据模型。</summary>
    public Note Note { get; }

    /// <summary>本便签的设备状态。</summary>
    public NoteLayout Layout { get; }

    /// <summary>便签 id。所有窗口操作、保存、删除都用它作标识。</summary>
    public Guid Id => Note.Id;

    /// <summary>
    /// 显示标题。<strong>完全派生，从不存储</strong>（§5.4）。
    /// </summary>
    /// <remarks>
    /// 加 <c>[NotifyPropertyChangedFor]</c> 挂在 <see cref="Content"/> 上，
    /// 而不是在这里自己缓存一份——缓存两份迟早会不一致。
    /// </remarks>
    public string Title => Note.Title;

    /// <summary>正文长度，状态条上那个「N 字」。</summary>
    /// <remarks>
    /// 派生值、不存储。挂在 <see cref="Content"/> 的 <c>[NotifyPropertyChangedFor]</c> 上，
    /// 于是它不需要自己的通知逻辑，也不会出现「字数与内容对不上」的状态。
    /// </remarks>
    public int CharacterCount => Content.Length;

    /// <summary>保存状态的中文说明，状态条显示用。</summary>
    /// <remarks>
    /// 文案暂时写死在这里而不是资源文件：状态条这一处的文案在 §24.2 的清单里，
    /// 等资源机制（<c>Strings</c> 包装类 + resx）接上时一并搬过去。
    /// 现在就建资源文件的话，<c>Strings.resx</c> 里会只有四个键、却要拖进一整套
    /// 生成与查找机制，反而看不清哪些文案是真的要本地化的。
    /// </remarks>
    public string StatusText => SaveStatus switch
    {
        SaveStatus.Saved => "已保存",
        SaveStatus.Pending => "未保存",
        SaveStatus.Saving => "保存中…",
        SaveStatus.Failed => "保存失败",
        _ => string.Empty,
    };

    // ------------------------------------------------------------------
    // 第一类：属于 Note，落盘到 Markdown
    // ------------------------------------------------------------------

    /// <summary>Markdown 正文。绑定到 <c>TextBox</c>，<c>UpdateSourceTrigger=PropertyChanged</c>（流 1）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(CharacterCount))]
    private string _content;

    /// <summary>便签颜色。写入 Front Matter 的 <c>color</c>（§5.3）。</summary>
    [ObservableProperty]
    private NoteColor _color;

    // ------------------------------------------------------------------
    // 第二类：属于 NoteLayout，落盘到 layout.json
    // ------------------------------------------------------------------

    /// <summary>是否折叠成标题条。折叠时窗口高度变成标题条高度，展开时恢复 <c>ExpandedHeight</c>（§15.2）。</summary>
    [ObservableProperty]
    private bool _isCollapsed;

    /// <summary>是否置顶。</summary>
    [ObservableProperty]
    private bool _isTopMost;

    /// <summary>是否锁定（锁定后不可编辑、不可拖动）。</summary>
    [ObservableProperty]
    private bool _isLocked;

    /// <summary>内容缩放比例，0.5 ~ 2.0（§15.5）。</summary>
    [ObservableProperty]
    private double _contentScale;

    // ------------------------------------------------------------------
    // 第三类：纯 UI 临时状态，绝不落盘（§18.1）
    // ------------------------------------------------------------------

    /// <summary>保存状态，用于界面上的「已保存 / 保存中 / 失败」提示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private SaveStatus _saveStatus = SaveStatus.Saved;

    /// <summary>是否正在输入法组字中。组字期间绝不去抖保存，否则会存下半截拼音（§15.6）。</summary>
    [ObservableProperty]
    private bool _isComposing;

    /// <summary>是否有改动尚未落盘。</summary>
    [ObservableProperty]
    private bool _isDirty;

    /// <summary>
    /// 光标位置。纯粹为了让重开窗口时不蹦到开头，<strong>不持久化</strong>（§18.1）。
    /// </summary>
    [ObservableProperty]
    private int _caretIndex;

    /// <summary>
    /// 内容变化时把新值推给业务层，并排一次去抖保存（流 1）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两步是分开的：<c>ApplyLocalEdit</c> 只改内存，回到它之后内容就已经是新的了；
    /// 真正的落盘由 <see cref="AutoSaveService"/> 在 <c>autoSaveDelayMs</c> 之后发起。
    /// 顺序不能反——先排保存再改内存的话，那次保存写出去的会是旧内容。
    /// </para>
    /// <para>
    /// 500ms 去抖、合并连续输入、组字期间跳过一次，全是 <see cref="AutoSaveService"/> 的职责（§11.3）。
    /// </para>
    /// </remarks>
    partial void OnContentChanged(string value)
    {
        _noteService.ApplyLocalEdit(Note, value);
        _autoSaveService.ScheduleSave(Note.Id);
    }

    /// <summary>实现 <see cref="IDisposable"/>，且必须幂等（§18.3）。</summary>
    /// <remarks>
    /// <para>
    /// 由 <c>NoteWindow.OnClosed</c> 调用。窗口在异常路径下可能走到两次 <c>OnClosed</c>，
    /// 因此 <c>_isDisposed</c> 标志不能省。
    /// </para>
    /// <para>
    /// 必须清理的三样东西（§18.3）：Messenger 注册、自动保存计时器、对 <see cref="Note"/> 的订阅。
    /// 漏掉计时器的话，窗口关了还会触发一次保存，而且会碰到已释放的对象。
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _messenger.UnregisterAll(this);

        // 取消等待中的那一轮保存：窗口关了之后定时器还会到期，而那时本对象已经释放。
        // 注意这里**不**调 SaveNowAsync——关窗口不该顺带写盘（§17.3 的关闭语义是
        // 「窗口没了，便签还在」），内容早已通过去抖落过盘，最近一次改动由
        // 调用方在 Closing 时显式保存。
        _autoSaveService.CancelScheduledSave(Id);

        _isDisposed = true;
    }
}
