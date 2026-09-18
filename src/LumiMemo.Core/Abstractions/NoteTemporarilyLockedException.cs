namespace LumiMemo.Core.Abstractions;

/// <summary>
/// 笔记文件被其他进程独占锁定，重试若干次后仍然读不出来（§5.10）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要一个专门的异常类型，而不是让 <see cref="INoteRepository.ReloadAsync"/> 返回
/// <see langword="null"/>：那个接口约定 <c>null</c> 表示<strong>文件已被删除</strong>。
/// 若把「暂时读不出来」也表达成 <c>null</c>，上层的 <c>NoteService</c> 会认为便签没了、
/// 把它从 <c>NoteStore</c> 里摘掉——一次杀软扫描就能让用户的便签从桌面上消失，
/// 而文件其实完好无损地躺在磁盘上。
/// </para>
/// <para>
/// §5.10 要求此时「保留 NoteStore 中的上次内容」。抛出本异常正是让上层有机会
/// 什么都不做——不做任何事，就自动保住了上次的内容。
/// </para>
/// </remarks>
public sealed class NoteTemporarilyLockedException : Exception
{
    public NoteTemporarilyLockedException()
    {
    }

    public NoteTemporarilyLockedException(string message)
        : base(message)
    {
    }

    public NoteTemporarilyLockedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>被锁定的文件路径。</summary>
    public string? FilePath { get; init; }

    /// <summary>构造带路径说明的实例。</summary>
    public static NoteTemporarilyLockedException ForPath(string path, Exception? innerException = null) =>
        new($"笔记文件被其他进程占用，重试后仍无法读取：「{path}」（§5.10）。", innerException)
        {
            FilePath = path,
        };
}
