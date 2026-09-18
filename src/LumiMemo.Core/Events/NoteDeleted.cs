namespace LumiMemo.Core.Events;

/// <summary>
/// 一张便签消失——被移入回收站，或文件在外部被删除。
/// </summary>
/// <remarks>
/// 这里只带 <see cref="NoteId"/> 与 <see cref="FilePath"/>，不带 <c>Note</c> 对象：
/// 收到本事件时便签已从 <c>NoteStore</c> 移除，再把那个实例传出去会诱使订阅方
/// 把它当成「仍然存在的便签」继续使用。
/// </remarks>
/// <param name="NoteId">被删除便签的 id。</param>
/// <param name="FilePath">删除前所在的位置。回收站恢复需要它。</param>
public sealed record NoteDeleted(Guid NoteId, string FilePath);
