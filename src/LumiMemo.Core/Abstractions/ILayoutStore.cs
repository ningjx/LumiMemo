using LumiMemo.Core.Models;

namespace LumiMemo.Core.Abstractions;

/// <summary>
/// <c>layout.json</c> 的读写（§8.3、§8.5）。实现在 Infrastructure 层。
/// </summary>
/// <remarks>
/// <para>
/// 采用<strong>整体保存 + 触发点 + 节流</strong>（§8.5）：<c>layout.json</c> 是一个整文件，
/// 按单便签保存意味着每次窗口移动都要做一次全文件重写并走原子替换。因此改动只调
/// <see cref="MarkDirty"/>，由节流后的 <see cref="FlushAsync"/> 统一落盘。
/// </para>
/// <para>
/// 保存的坐标与尺寸全部是<strong>物理像素</strong>，且必须与 <see cref="NoteLayout.Dpi"/>
/// 一起写入——只存像素不存 DPI，跨显示器恢复时算不出正确的视觉大小（§8.3）。
/// </para>
/// </remarks>
public interface ILayoutStore
{
    Task LoadAsync(CancellationToken ct = default);

    /// <summary>取布局，不存在则按设置里的默认尺寸新建一条（§3.3 流 3）。</summary>
    NoteLayout GetOrCreate(Guid noteId);

    /// <summary>取布局，不存在时返回 <c>null</c>（不产生副作用）。</summary>
    NoteLayout? TryGet(Guid noteId);

    /// <summary>全部布局。管理器与显示器变化后的重排需要遍历它。</summary>
    IReadOnlyCollection<NoteLayout> All { get; }

    /// <summary>标记有改动，等待节流后的落盘。不要在这个方法里写文件。</summary>
    void MarkDirty();

    /// <summary>把脏数据落盘。退出流程中必须调用一次（§17.4）。</summary>
    Task FlushAsync(CancellationToken ct = default);
}
