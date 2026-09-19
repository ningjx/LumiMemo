using System.IO;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LumiMemo.App.Abstractions;
using LumiMemo.App.Services;
using LumiMemo.App.Views;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Services;

namespace LumiMemo.App.ViewModels;

/// <summary>
/// 托盘图标与它的右键菜单（§15.9）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>它没有对应的窗口</strong>，所以没有 XAML 也没有绑定：菜单项在
/// <c>TrayService</c> 里建出来，<c>Command</c> 直接指向这里的属性。
/// 这样拆的理由是 <c>TaskbarIcon</c> 需要真实的消息循环，测试进程里建不起来；
/// 而菜单里真正有内容的（单选行为的分派、回收站计数、重扫）都留在这里，
/// 于是它们可测——这正是 §18.1 把 <c>TrayViewModel</c> 单列一行的意思。
/// </para>
/// <para>
/// <strong>它不自己实现便签动作</strong>：「新建」「显示全部」「收起全部」「重新加载」
/// 一律转发给 <see cref="ManagerViewModel"/>。这些动作管理器窗口上也各有一个入口，
/// 而 §17.6 要求同一件事只有一条路径——各写一份的话，
/// 「用户关掉某张便签后它还会不会回来」这种判断迟早会在某一条路上走岔。
/// </para>
/// <para>
/// <strong>它一个窗口类型都不碰</strong>：「便签列表...」与「设置...」分别经
/// <see cref="IManagerWindowPresenter"/> 和 <c>SettingsWindowLauncher</c> 走。
/// 直接持有 <c>ManagerWindow</c> 也能跑，但那样本类的每一个用例都得上 STA 线程
/// 并造一个真窗口——而这里真正有内容的（单选行为的分派、回收站计数、重扫）
/// 一条都用不着窗口。
/// </para>
/// </remarks>
public sealed partial class TrayViewModel : ObservableObject
{
    /// <summary>双击托盘图标固定走「显示全部便签」（§15.9），不随设置变。</summary>
    public const string ShowAllNotesAction = "showAllNotes";

    private readonly ManagerViewModel _manager;
    private readonly IManagerWindowPresenter _managerWindow;
    private readonly SettingsWindowLauncher _settingsLauncher;
    private readonly IShellLauncher _shellLauncher;
    private readonly IAppPaths _paths;
    private readonly IDialogService _dialogs;
    private readonly TrashService _trash;
    private readonly IApplicationLifetime _lifetime;

    public TrayViewModel(
        ManagerViewModel manager,
        IManagerWindowPresenter managerWindow,
        SettingsWindowLauncher settingsLauncher,
        IShellLauncher shellLauncher,
        IAppPaths paths,
        IDialogService dialogs,
        TrashService trash,
        IApplicationLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(managerWindow);
        ArgumentNullException.ThrowIfNull(settingsLauncher);
        ArgumentNullException.ThrowIfNull(shellLauncher);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(trash);
        ArgumentNullException.ThrowIfNull(lifetime);

        _manager = manager;
        _managerWindow = managerWindow;
        _settingsLauncher = settingsLauncher;
        _shellLauncher = shellLauncher;
        _paths = paths;
        _dialogs = dialogs;
        _trash = trash;
        _lifetime = lifetime;
    }

    /// <summary>鼠标悬停在托盘图标上时的文字。</summary>
    public string ToolTipText => "LumiMemo";

    /// <summary>回收站里的条目数，显示在菜单项的文字里。</summary>
    /// <remarks>
    /// 做成属性而不是每次去查磁盘：「回收站里有没有东西」要在菜单弹出来的那一瞬间就是对的，
    /// 而 <c>ListAsync</c> 要读索引文件。刷新时机见 <see cref="RefreshTrashCountAsync"/>。
    /// </remarks>
    [ObservableProperty]
    private int _trashCount;

    /// <summary>「回收站（N）...」。空的时候不写那个括号（§15.9）。</summary>
    public string TrashMenuHeader => TrashCount > 0 ? $"回收站（{TrashCount}）..." : "回收站...";

    /// <summary>计数一变，菜单项的文字也要跟着变——它们是同一个数的两种写法。</summary>
    partial void OnTrashCountChanged(int value) => OnPropertyChanged(nameof(TrashMenuHeader));

    /// <summary>
    /// 单击托盘图标的行为：<c>toggleManager</c> / <c>newNote</c> / <c>showAllNotes</c>（§8.2）。
    /// </summary>
    /// <remarks>
    /// 可写属性而不是构造参数，与 <c>AutoSaveService.DelayMilliseconds</c> 同一手法：
    /// 设置是启动时才知道的，不该成为构造函数的一部分。取值不认识时按
    /// <c>toggleManager</c> 处理——配置文件允许被手改（§9.3）。
    /// </remarks>
    public string SingleClickAction { get; set; } = "toggleManager";

    /// <summary>单击托盘图标。</summary>
    /// <remarks>
    /// 双击的判定在 <c>TaskbarIcon</c> 里（它会按住单击一小会儿看有没有第二次）。
    /// 这里只管单击该做什么，不参与那个判定。
    /// </remarks>
    [RelayCommand]
    public void OnSingleClick()
    {
        switch (SingleClickAction)
        {
            case "newNote":
                NewNote();
                break;
            case ShowAllNotesAction:
                ShowAllNotes();
                break;
            default:
                OpenManager();
                break;
        }
    }

    /// <summary>双击托盘图标：显示全部便签（§15.9）。</summary>
    [RelayCommand]
    public void OnDoubleClick() => ShowAllNotes();

    /// <summary>新建一张便签并打开它。</summary>
    [RelayCommand]
    public void NewNote() => _manager.NewNoteCommand.Execute(null);

    /// <summary>显示全部便签（§17.6 的四个入口之一）。</summary>
    [RelayCommand]
    public void ShowAllNotes() => _manager.ShowAllCommand.Execute(null);

    /// <summary>收起全部便签。</summary>
    [RelayCommand]
    public void HideAllNotes() => _manager.HideAllCommand.Execute(null);

    /// <summary>打开管理器窗口（「便签列表...」）。</summary>
    [RelayCommand]
    public void OpenManager() => _managerWindow.BringToFront();

    /// <summary>打开设置窗口的回收站页。</summary>
    [RelayCommand]
    public void OpenTrash() => _settingsLauncher.Show(SettingsTab.Trash);

    /// <summary>打开设置窗口。</summary>
    [RelayCommand]
    public void OpenSettings() => _settingsLauncher.Show();

    /// <summary>重新加载全部便签（§10.5 的兜底）。</summary>
    [RelayCommand]
    public void ReloadAll() => _manager.ReloadAllCommand.Execute(null);

    /// <summary>用资源管理器打开笔记文件夹。</summary>
    /// <remarks>
    /// 打不开时给一句提示。用户点了没反应与「告诉他这个目录不在了」是两回事，
    /// 而网络盘掉线正是这里会发生的常见状况。
    /// </remarks>
    [RelayCommand]
    public async Task OpenNotesFolderAsync()
    {
        string folder = _paths.NotesFolder ?? string.Empty;

        if (!_shellLauncher.OpenFolder(folder))
        {
            await _dialogs.ShowErrorAsync("打开笔记文件夹", $"打不开这个文件夹：\n{folder}");
        }
    }

    /// <summary>关于。</summary>
    [RelayCommand]
    public Task AboutAsync() => _dialogs.ShowInfoAsync(
        "关于 LumiMemo",
        $"LumiMemo {Version()}\n\n"
        + "便签以 Markdown 文件存放在你的笔记文件夹里，"
        + "任何编辑器都能打开它们。");

    /// <summary>退出程序。</summary>
    /// <remarks>
    /// 这只发起退出。§17.4 那一整套收尾（停自动保存 → flush → 写 layout.json）
    /// 挂在 <c>App.OnExit</c> 上，由 <c>Shutdown</c> 触发出来（见 <see cref="IApplicationLifetime"/>）。
    /// </remarks>
    [RelayCommand]
    public void Exit() => _lifetime.RequestShutdown();

    /// <summary>
    /// 重新数一遍回收站里的条目。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 调用的时机只有两个：启动跑完之后、以及右键菜单弹出来的时候
    /// （<c>TrayService</c> 挂在 <c>TaskbarIcon.TrayContextMenuOpen</c> 上）。
    /// 不在每次删除/恢复之后逐个通知——那需要回收站那边反向依赖本类，
    /// 而菜单弹出时刷一次已经足够新：用户看不到菜单的时候那个数字没人看。
    /// </para>
    /// <para>
    /// <strong>它自己吞掉异常</strong>：数不出来（回收站目录被拔了、索引损坏）
    /// 不该让托盘菜单弹不出来。数字归零，用户看到的就是「回收站...」。
    /// </para>
    /// </remarks>
    public async Task RefreshTrashCountAsync()
    {
        try
        {
            TrashCount = (await _trash.ListAsync()).Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            TrashCount = 0;
        }
    }

    /// <summary>
    /// 程序版本号，形如 <c>0.1.0</c>。
    /// </summary>
    /// <remarks>
    /// 取三段而不是四段（<c>ToString(3)</c>）：程序集版本里那个恒为零的第四段
    /// （<c>0.1.0.0</c>）在「关于」里只会让人去数位数，它没有任何含义。
    /// 来源是 <c>Directory.Build.props</c> 的 <c>VersionPrefix</c>。
    /// </remarks>
    private static string Version()
    {
        Assembly assembly = typeof(TrayViewModel).Assembly;

        return assembly.GetName().Version?.ToString(3) ?? "未知";
    }
}
