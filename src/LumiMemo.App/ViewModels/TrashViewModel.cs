using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LumiMemo.App.Abstractions;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Services;

namespace LumiMemo.App.ViewModels;

/// <summary>
/// 回收站页签的界面状态（§7.4）。它是 <c>SettingsWindow</c> 的一页，<strong>没有独立窗口</strong>。
/// </summary>
/// <remarks>
/// <para>
/// <strong>它只碰 <see cref="TrashService"/></strong>：不认 <c>ITrashStore</c>、不拼路径、
/// 不判定一致性。存储层该修的索引错误已经修完了，这里拿到的是一份可以直接显示的清单。
/// </para>
/// <para>
/// <strong>所有会改磁盘的操作都要先问过用户</strong>，而且问的话术来自 <c>Strings</c>。
/// 清空回收站是全程序唯一不可逆的破坏性操作，它必须走两次确认（§7.4）。
/// </para>
/// <para>
/// <strong>不持有窗口引用</strong>（§18.3）：确认框走 <see cref="IDialogService"/>，
/// 打开目录走 <see cref="IShellLauncher"/>。这样整个类可以在无界面进程里跑完。
/// </para>
/// </remarks>
public sealed partial class TrashViewModel : ObservableObject
{
    private readonly TrashService _trash;
    private readonly IAppPaths _paths;
    private readonly IDialogService _dialogs;
    private readonly IShellLauncher _shell;
    private readonly IDispatcher _dispatcher;

    public TrashViewModel(
        TrashService trash,
        IAppPaths paths,
        IDialogService dialogs,
        IShellLauncher shell,
        IDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(trash);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _trash = trash;
        _paths = paths;
        _dialogs = dialogs;
        _shell = shell;
        _dispatcher = dispatcher;
    }

    /// <summary>回收站里的条目，按删除时刻倒序（存储层已经排好）。</summary>
    public ObservableCollection<TrashItem> Items { get; } = [];

    /// <summary>当前选中的那一行。恢复命令作用在它上面。</summary>
    [ObservableProperty]
    private TrashItem? _selectedItem;

    /// <summary>列表为空时的提示。空列表与「正在加载」在界面上长得一样，得靠文字区分。</summary>
    public string EmptyHint => "回收站是空的。删掉的便签会先放在这里。";

    /// <summary>状态行上的计数，形如「回收站里有 3 个项目（共 2.1 MB）」。</summary>
    /// <remarks>
    /// 大小也在这一行里报出来：用户点「清空」之前该知道自己在永久删掉多少东西。
    /// 这也是 §7.4 第一次确认框里那句话的取数处。
    /// </remarks>
    public string CountText => Items.Count == 0
        ? "回收站是空的"
        : $"回收站里有 {Items.Count} 个项目（共 {TrashItem.FormatSize(TotalBytes)}）";

    /// <summary>列表里所有条目的字节数之和。</summary>
    public long TotalBytes => Items.Sum(item => item.Entry.Size);

    /// <summary>
    /// 重新读一遍回收站。
    /// </summary>
    /// <remarks>
    /// <see cref="IDispatcher.VerifyAccess"/> 不是装饰：<see cref="ObservableCollection{T}"/>
    /// 被界面绑定时跨线程改会抛异常（§3.4 规则 T5）。存储层的 IO 在
    /// <c>await</c> 之后回来，而本方法每个 <c>await</c> 都<strong>刻意不加</strong>
    /// <c>ConfigureAwait(false)</c>，让续体回到 UI 线程。
    /// </remarks>
    [RelayCommand]
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        _dispatcher.VerifyAccess();

        IReadOnlyList<TrashEntry> entries = await _trash.ListAsync(ct);

        Items.Clear();

        foreach (TrashEntry entry in entries)
        {
            Items.Add(new TrashItem(entry));
        }

        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(EmptyHint));
    }

    /// <summary>
    /// 把一条恢复到笔记目录（§7.3）。原位置被占用时先弹三选一。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 三档的落点是：<strong>重命名</strong>传 <see langword="null"/> —— 存储层会拿
    /// <c>OriginalRelativePath</c> 算出原位并自动让开；<strong>恢复到根</strong>传
    /// <see cref="TrashService.RootTargetFor"/> 的结果；<strong>取消</strong>直接返回。
    /// </para>
    /// <para>
    /// 对话框里<strong>不预告具体的序号</strong>（「周报 (1).md」）。那个序号由存储层的
    /// <c>AllocateFreePath</c> 算，取决于撞了几次；在这里重算一遍就是在实现第二份分配逻辑，
    /// 一旦两边不一致，用户看到的名字与实际落下来的名字就不同了。
    /// </para>
    /// </remarks>
    [RelayCommand]
    public async Task RestoreAsync(TrashItem? item, CancellationToken ct = default)
    {
        if (item is null)
        {
            return;
        }

        string? target = null;

        if (_trash.NeedsConflictResolution(item.Entry))
        {
            int choice = await _dialogs.ChooseAsync(
                "恢复便签",
                $"原位置已有同名文件：\n{item.LocationText}\n\n"
                    + "选「重命名」会给恢复回来的文件加一个序号，现有的那个文件不受影响。",
                ["恢复到原位置并重命名", "恢复到笔记目录根", "取消"],
                defaultIndex: 0);

            if (choice == 1)
            {
                target = TrashService.RootTargetFor(item.Entry);
            }
            else if (choice != 0)
            {
                return;
            }
        }

        await _trash.RestoreAsync(item.Entry, target, ct);
        await RefreshAsync(ct);
    }

    /// <summary>
    /// 清空回收站（§7.4）。<strong>两次确认</strong>，第二次的按钮写着「永久删除」。
    /// </summary>
    /// <remarks>
    /// 两次是刻意的：这是全程序唯一不可逆的破坏性操作。第一次让用户看清<strong>数量与体积</strong>，
    /// 第二次把「不可撤销」摆在眼前，且按钮文字是「永久删除」而不是「确定」——
    /// 用户在第二次点错时不该有看错的余地。
    /// </remarks>
    [RelayCommand]
    public async Task EmptyAsync(CancellationToken ct = default)
    {
        if (Items.Count == 0)
        {
            return;
        }

        int files = Items.Count;
        string size = TrashItem.FormatSize(TotalBytes);

        bool confirmed = await _dialogs.ConfirmAsync(
            "清空回收站",
            $"确认清空 {files} 个项目（共 {size}）？",
            "清空",
            "取消");

        if (!confirmed)
        {
            return;
        }

        bool permanent = await _dialogs.ConfirmAsync(
            "清空回收站",
            "此操作不可撤销，确定要永久删除吗？",
            "永久删除",
            "取消");

        if (!permanent)
        {
            return;
        }

        await _trash.EmptyAsync(ct);
        await RefreshAsync(ct);
    }

    /// <summary>
    /// 用资源管理器打开 <c>.lumimemo/trash/</c>（§7.4）。
    /// </summary>
    /// <remarks>
    /// 目录不存在时先建出来：一个空的回收站目录是无害的，而「点了一下什么都没发生」
    /// 会让用户以为按钮坏了。存储层在无条目时不建这个目录（免得往用户的笔记目录里
    /// 塞空文件夹），但用户明确点了「打开回收站目录」就不再是那个场景了。
    /// </remarks>
    [RelayCommand]
    public async Task OpenTrashFolderAsync(CancellationToken ct = default)
    {
        if (_paths.NotesFolder is null)
        {
            await _dialogs.ShowInfoAsync("回收站", "还没有选定笔记文件夹，回收站暂时不可用。");

            return;
        }

        string directory = _paths.TrashDirectory;

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _dialogs.ShowErrorAsync("回收站", $"打不开回收站目录：\n{ex.Message}");

            return;
        }

        if (!_shell.OpenFolder(directory))
        {
            await _dialogs.ShowErrorAsync("回收站", "资源管理器没能打开这个文件夹。");
        }
    }
}
