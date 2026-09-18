using CommunityToolkit.Mvvm.Messaging;
using LumiMemo.App.Abstractions;
using LumiMemo.App.ViewModels;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;

namespace LumiMemo.App.Services;

/// <summary>
/// <see cref="NoteViewModel"/> 的<strong>唯一构造点</strong>（§14.1）。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由是把「八个构造参数」这件事收在一处。没有它的话，启动序列恢复上次打开、
/// 管理器双击开一张、将来的「新建便签」三处各写一遍 <c>new NoteViewModel(...)</c>，
/// 而每加一个依赖就要同步改三个地方——漏掉一处是编译错误，但改错顺序是运行时的事故。
/// </para>
/// <para>
/// <strong>注册进容器的理由</strong>：它是无状态的，且依赖的六个服务全是单例。
/// 相比让调用方自己 <c>new</c> 一个（那样调用方得先把这六个依赖全拿到手），
/// 注册进容器能让 <c>StartupSequence</c> 与 <c>ManagerViewModel</c> 都只是注入一个工厂。
/// 这不改变「ViewModel 不交给容器解析」这条规则——容器里没有 <c>NoteViewModel</c> 的注册，
/// 只有造它的这个工厂。
/// </para>
/// </remarks>
public sealed class NoteViewModelFactory
{
    private readonly INoteService _noteService;
    private readonly AutoSaveService _autoSaveService;
    private readonly IDispatcher _dispatcher;
    private readonly IDialogService _dialogService;
    private readonly IWindowManager _windowManager;
    private readonly IMessenger _messenger;

    public NoteViewModelFactory(
        INoteService noteService,
        AutoSaveService autoSaveService,
        IDispatcher dispatcher,
        IDialogService dialogService,
        IWindowManager windowManager,
        IMessenger messenger)
    {
        ArgumentNullException.ThrowIfNull(noteService);
        ArgumentNullException.ThrowIfNull(autoSaveService);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(windowManager);
        ArgumentNullException.ThrowIfNull(messenger);

        _noteService = noteService;
        _autoSaveService = autoSaveService;
        _dispatcher = dispatcher;
        _dialogService = dialogService;
        _windowManager = windowManager;
        _messenger = messenger;
    }

    /// <param name="note">便签模型。ViewModel 与它共存亡。</param>
    /// <param name="layout">该便签的布局。折叠、置顶、缩放直接读写它。</param>
    public NoteViewModel Create(Note note, NoteLayout layout)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(layout);

        return new NoteViewModel(
            note,
            layout,
            _noteService,
            _autoSaveService,
            _dispatcher,
            _dialogService,
            _windowManager,
            _messenger);
    }
}
