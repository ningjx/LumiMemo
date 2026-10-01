using LumiMemo.Core.Models;

namespace LumiMemo.WinUI.Services;

/// <summary>便签窗口要用、但自己不拥有的几个管理动作（新建便笺、删除本笺）。</summary>
/// <remarks>
/// 便签窗口由 <c>NoteWindowFactory</c> 手工构造、不在容器里，拿不到
/// <see cref="NoteWindowManager"/>；用这个窄接口把它需要的两个动作传进去，
/// 避免窗口反向依赖整个管理器。
/// </remarks>
public interface INoteWindowActions
{
    /// <summary>新建一张便签并打开它的窗口。</summary>
    Task CreateNoteAsync();

    /// <summary>删除一张便签（入回收站）；窗口内容保存失败时返回 false 且不删除。</summary>
    Task<bool> DeleteNoteAsync(Note note);
}
