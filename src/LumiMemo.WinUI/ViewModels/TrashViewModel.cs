using CommunityToolkit.Mvvm.ComponentModel;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.WinUI.Services;
using Microsoft.Extensions.Logging;

namespace LumiMemo.WinUI.ViewModels;

/// <summary>回收站窗口的状态：列表、恢复、彻底删除与清空。</summary>
/// <remarks>
/// 恢复的编排放在这里而不在窗口：移文件（<see cref="ITrashStore"/>）→
/// 读回便笺（<see cref="INoteStorage"/>）→ 登记进便笺列表（<see cref="NoteWindowManager"/>），
/// 三步都是服务层的事；窗口只管显示与确认。失败向调用方抛出，由窗口决定怎么告诉用户。
/// </remarks>
public sealed class TrashViewModel(
    ITrashStore trash,
    INoteStorage storage,
    NoteWindowManager windows,
    ILogger<TrashViewModel> logger)
    : ObservableObject
{
    private IReadOnlyList<TrashListItem> _items = [];

    /// <summary>回收站的当前内容，按删除时间倒序（由存储层保证）。</summary>
    public IReadOnlyList<TrashListItem> Items
    {
        get => _items;
        private set => SetProperty(ref _items, value);
    }

    /// <summary>刷新列表（打开窗口与每次操作之后调用）。</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        IReadOnlyList<TrashEntry> entries = await trash.ListAsync(ct);
        Items = [.. entries.Select(static entry => new TrashListItem(entry))];
    }

    /// <summary>恢复一条，并让便签重新出现在便笺列表里。</summary>
    public async Task RestoreAsync(TrashListItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        string path = await trash.RestoreAsync(item.Entry, ct);
        Note? note = await storage.TryLoadAsync(path, ct);

        if (note is null)
        {
            // 文件恢复出来了但读不回来（内容坏了）：保留文件、如实告知——
            // 比悄悄丢掉更能让用户自己抢救。
            logger.LogWarning("恢复的便笺无法解析：{Path}。", path);
            throw new InvalidOperationException("便笺文件已恢复，但内容无法解析，请检查笔记目录。");
        }

        windows.RegisterRestoredNote(note);
        await RefreshAsync(ct);
    }

    /// <summary>彻底删除一条（不可恢复）。</summary>
    public async Task PurgeAsync(TrashListItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        await trash.PurgeAsync(item.Entry, ct);
        await RefreshAsync(ct);
    }

    /// <summary>清空回收站。</summary>
    public async Task PurgeAllAsync(CancellationToken ct = default)
    {
        // 先取快照：PurgeAsync 之後 Items 会重建，边遍历边刷新会变得不确定。
        foreach (TrashListItem item in Items.ToArray())
        {
            await trash.PurgeAsync(item.Entry, ct);
        }

        await RefreshAsync(ct);
    }
}
