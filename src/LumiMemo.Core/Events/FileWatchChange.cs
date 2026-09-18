namespace LumiMemo.Core.Events;

/// <summary>
/// 一次文件系统变化（§10.1）。
/// </summary>
/// <remarks>
/// 事件在<strong>线程池线程</strong>上到达，不是 UI 线程。消费方必须先封送回 UI 线程
/// 才能触碰 <c>NoteStore</c>、ViewModel 或 <c>ObservableCollection</c>（§3.4 规则 T3）。
/// </remarks>
/// <param name="Kind">变化种类。</param>
/// <param name="Path">受影响的文件完整路径。<see cref="FileWatchChangeKind.Renamed"/> 时是新路径。</param>
/// <param name="OldPath"><see cref="FileWatchChangeKind.Renamed"/> 时的旧路径，其余情况为 <c>null</c>。</param>
public sealed record FileWatchChange(FileWatchChangeKind Kind, string Path, string? OldPath = null);
