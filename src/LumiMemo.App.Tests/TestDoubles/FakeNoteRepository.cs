using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;

namespace LumiMemo.App.Tests.TestDoubles;

/// <summary>
/// 不出内存的 <see cref="INoteRepository"/> 替身。
/// </summary>
/// <remarks>
/// <para>
/// 真实的磁盘行为（原子写盘、编码与行尾的原样保留、id 回补）由集成测试覆盖。
/// 本替身只服务 <c>TrashService</c> 的编排：<strong>恢复之后有没有把文件读回内存</strong>。
/// </para>
/// <para>
/// 它<strong>不做任何业务加工</strong>——不补 id、不建目录、不校验内容。
/// 替身一旦开始「帮忙」，被测代码的缺陷就会被它掩盖过去。
/// </para>
/// </remarks>
public sealed class FakeNoteRepository : INoteRepository
{
    /// <summary><see cref="ReloadAsync"/> 认得出来的便签，按 <c>FilePath</c> 索引。</summary>
    public List<Note> NotesToLoad { get; } = [];

    /// <summary>
    /// 逐路径指定 <see cref="ReloadAsync"/> 的结果，<strong>优先于 <see cref="NotesToLoad"/></strong>。
    /// </summary>
    /// <remarks>
    /// 「读到了什么」与「它跟上次同步的字节一不一样」是两件事，监听那套逻辑两件都要，
    /// 而 <see cref="NotesToLoad"/> 只表达得了前一件。没写在这一张里的路径按
    /// <see cref="NotesToLoad"/> 找，找不到就当文件已经不在。
    /// </remarks>
    public Dictionary<string, NoteFileSync> ReloadResults { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><see cref="ReloadAsync"/> 收到过的路径，按发生顺序。用来钉「一个窗口里多次事件只读一次盘」。</summary>
    public List<string> ReloadedPaths { get; } = [];

    /// <summary>按路径断言「这一次读不出来」，值就是抛出的那个异常。</summary>
    /// <remarks>
    /// 单个文件被别的程序独占锁住是日常（§5.10），而「跳过它、其余照常」这条路径
    /// 只有真的抛得出来才走得进去。抛 <see cref="Task.FromException"/> 而不是同步抛，
    /// 与真实实现的异步形状一致。
    /// </remarks>
    public Dictionary<string, Exception> ReloadFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<IReadOnlyList<Note>> LoadAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Note>>([.. NotesToLoad]);

    /// <inheritdoc />
    public Task<NoteFileSync> ReloadAsync(string path, CancellationToken ct = default)
    {
        ReloadedPaths.Add(path);

        if (ReloadFailures.TryGetValue(path, out Exception? failure))
        {
            return Task.FromException<NoteFileSync>(failure);
        }

        if (ReloadResults.TryGetValue(path, out NoteFileSync? scripted))
        {
            return Task.FromResult(scripted);
        }

        Note? found = NotesToLoad.Find(
            note => string.Equals(note.FilePath, path, StringComparison.OrdinalIgnoreCase));

        // 进过 NotesToLoad 的一律当成「变过」：替身没有基线可比，
        // 默认成「没变」会让本该被处理的用例静默什么都不做，比报错难查得多。
        return Task.FromResult(new NoteFileSync(found, DiskChanged: true));
    }

    /// <inheritdoc />
    /// <remarks>本替身服务的用例都在 .md 这条路上，别的路径一律算便签文件。</remarks>
    public bool IsNoteFile(string path) => true;

    /// <inheritdoc />
    /// <remarks>
    /// 不支持：走到这里说明用例真的想要一份冲突副本，而它需要的语义
    /// （副本内容、命名、原文件不动）只能在真实磁盘上验，那属于集成测试。
    /// 悄悄返回一个假路径会让「备份没留成」这类缺陷被掩盖过去。
    /// </remarks>
    public Task<string?> BackupConflictCopyAsync(string path, CancellationToken ct = default) =>
        throw new NotSupportedException("本替身不模拟冲突备份，请用集成测试覆盖。");

    /// <inheritdoc />
    public Task SaveAsync(Note note, CancellationToken ct = default)
    {
        NotesToLoad.RemoveAll(
            existing => string.Equals(existing.FilePath, note.FilePath, StringComparison.OrdinalIgnoreCase));
        NotesToLoad.Add(note);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Note> CreateAsync(
        NoteColor? color = null, string? targetFolder = null, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        var note = new Note
        {
            Id = id,
            FilePath = $@"D:\notes\{id:N}.md",
            Content = string.Empty,
            Color = color ?? NoteColor.Yellow,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

        NotesToLoad.Add(note);

        return Task.FromResult(note);
    }
}
