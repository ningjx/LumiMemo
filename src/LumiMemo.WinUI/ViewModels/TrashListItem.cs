using LumiMemo.Core.Models;

namespace LumiMemo.WinUI.ViewModels;

/// <summary>回收站列表的一行展示数据。</summary>
public sealed class TrashListItem(TrashEntry entry)
{
    public TrashEntry Entry { get; } = entry;

    public string Title => Entry.Title;

    public string DeletedAtText => $"删除于 {Entry.DeletedAt.LocalDateTime:yyyy-MM-dd HH:mm}";
}
