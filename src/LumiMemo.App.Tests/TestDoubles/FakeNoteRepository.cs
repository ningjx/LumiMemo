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

    /// <inheritdoc />
    public Task<IReadOnlyList<Note>> LoadAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Note>>([.. NotesToLoad]);

    /// <inheritdoc />
    public Task<Note?> ReloadAsync(string path, CancellationToken ct = default)
    {
        Note? found = NotesToLoad.Find(
            note => string.Equals(note.FilePath, path, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(found);
    }

    /// <inheritdoc />
    public Task SaveAsync(Note note, CancellationToken ct = default)
    {
        NotesToLoad.RemoveAll(
            existing => string.Equals(existing.FilePath, note.FilePath, StringComparison.OrdinalIgnoreCase));
        NotesToLoad.Add(note);

        return Task.CompletedTask;
    }
}
