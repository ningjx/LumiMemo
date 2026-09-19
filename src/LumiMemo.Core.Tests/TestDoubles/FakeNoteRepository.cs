using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;

namespace LumiMemo.Core.Tests.TestDoubles;

/// <summary>
/// 不出内存的 <see cref="INoteRepository"/> 替身（§21.5 的替身表）。
/// </summary>
/// <remarks>
/// <para>
/// 真实仓储的磁盘行为（原子写盘、编码与行尾的原样保留、id 回补）已经在
/// <c>LumiMemo.Integration.Tests</c> 里用真实文件系统测过，那份不必在这里重来。
/// 本替身只服务 <c>NoteService</c> 的编排逻辑：<strong>它有没有把对的东西在对的时候交出去</strong>。
/// </para>
/// <para>
/// 刻意<strong>不</strong>做任何业务加工——不补 id、不改目录、不校验内容。
/// 一旦替身开始「帮忙」，被测代码的缺陷就会被替身悄悄掩盖过去。
/// </para>
/// </remarks>
public sealed class FakeNoteRepository : INoteRepository
{
    /// <summary><see cref="LoadAllAsync"/> 要返回的便签，由用例预先放好。</summary>
    public List<Note> NotesToLoad { get; } = [];

    /// <summary>所有交给 <see cref="SaveAsync"/> 的便签，按发生顺序。</summary>
    public List<Note> Saved { get; } = [];

    /// <summary><see cref="LoadAllAsync"/> 被调了几次。</summary>
    public int LoadCallCount { get; private set; }

    /// <summary>所有交给 <see cref="CreateAsync"/> 的参数，按发生顺序。</summary>
    public List<(NoteColor? Color, string? TargetFolder)> CreateRequests { get; } = [];

    /// <summary><see cref="CreateAsync"/> 要交出去的便签；不设时自己造一张。</summary>
    public Func<Note>? CreateHandler { get; set; }

    /// <summary><see cref="CreateAsync"/> 要抛的异常；不设时正常返回。</summary>
    public Exception? CreateException { get; set; }

    /// <summary>
    /// 逐路径指定 <see cref="ReloadAsync"/> 的结果，<strong>优先于 <see cref="NotesToLoad"/></strong>。
    /// </summary>
    /// <remarks>
    /// 外部变更那套逻辑要的是一对值：「这个路径读到了什么」与「它跟上次同步的字节一不一样」。
    /// <see cref="NotesToLoad"/> 只表达得了前一半，而「一样」恰恰是自写抑制的判据，
    /// 也是最需要被钉住的一条（漏了它，每次保存都会把自己的写入当成外部改动）。
    /// </remarks>
    public Dictionary<string, NoteFileSync> ReloadResults { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><see cref="ReloadAsync"/> 收到过的路径，按发生顺序。</summary>
    public List<string> ReloadedPaths { get; } = [];

    /// <summary>所有交给 <see cref="BackupConflictCopyAsync"/> 的路径，按发生顺序。</summary>
    public List<string> BackedUpPaths { get; } = [];

    /// <summary><see cref="BackupConflictCopyAsync"/> 要抛的异常；不设时正常返回一个副本路径。</summary>
    /// <remarks>
    /// 用它模拟「副本没留成」：那时覆盖必须就此停下，否则磁盘上那一版就真没了。
    /// </remarks>
    public Exception? BackupException { get; set; }

    /// <inheritdoc />
    public Task<IReadOnlyList<Note>> LoadAllAsync(CancellationToken ct = default)
    {
        LoadCallCount++;

        return Task.FromResult<IReadOnlyList<Note>>(NotesToLoad);
    }

    /// <inheritdoc />
    public Task<NoteFileSync> ReloadAsync(string path, CancellationToken ct = default)
    {
        ReloadedPaths.Add(path);

        if (ReloadResults.TryGetValue(path, out NoteFileSync? scripted))
        {
            return Task.FromResult(scripted);
        }

        Note? found = NotesToLoad.Find(
            note => string.Equals(note.FilePath, path, StringComparison.OrdinalIgnoreCase));

        // 进过 NotesToLoad 的一律当成「变过」：替身没有基线可比，
        // 而默认成「没变」会让本该被处理的用例静默什么都不做——比报错难查得多。
        return Task.FromResult(new NoteFileSync(found, DiskChanged: true));
    }

    /// <inheritdoc />
    /// <remarks>本替身服务的用例只关心 .md，别的路径一律不算便签。</remarks>
    public bool IsNoteFile(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<string?> BackupConflictCopyAsync(string path, CancellationToken ct = default)
    {
        BackedUpPaths.Add(path);

        if (BackupException is { } exception)
        {
            return Task.FromException<string?>(exception);
        }

        // 真的仓储会把副本写进同一个目录，名字与扩展名都有讲究（§11.4）。
        // 替身只要一个「确实做了备份」的证据，名字是什么由集成测试去钉。
        return Task.FromResult<string?>($"{path}.conflict-backup");
    }

    /// <inheritdoc />
    public Task SaveAsync(Note note, CancellationToken ct = default)
    {
        Saved.Add(note);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Note> CreateAsync(
        NoteColor? color = null, string? targetFolder = null, CancellationToken ct = default)
    {
        CreateRequests.Add((color, targetFolder));

        if (CreateException is { } exception)
        {
            return Task.FromException<Note>(exception);
        }

        return Task.FromResult(CreateHandler is { } handler ? handler() : NewNote());
    }

    private static Note NewNote()
    {
        var id = Guid.NewGuid();

        return new Note
        {
            Id = id,
            FilePath = $@"D:\notes\{id:N}.md",
            Content = string.Empty,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };
    }
}
