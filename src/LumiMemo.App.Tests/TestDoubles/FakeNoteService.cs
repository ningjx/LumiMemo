using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;

namespace LumiMemo.App.Tests.TestDoubles;

/// <summary>
/// <see cref="INoteService"/> 的记录型替身（§21.1：App.Tests 要测 ViewModel 与 AutoSaveService 的调度，
/// 就必须能注入一个假的 <c>INoteService</c>）。
/// </summary>
/// <remarks>
/// 它是「哑」的：不做任何业务规则，只记录被调用了什么。
/// 真正的业务规则由 Core.Tests 与 Integration.Tests 覆盖，这里只关心
/// <strong>ViewModel 有没有在对的时候发出对的调用</strong>。
/// </remarks>
public sealed class FakeNoteService : INoteService
{
    /// <summary>所有 <see cref="ApplyLocalEdit"/> 调用，按发生顺序。</summary>
    public List<(Guid NoteId, string Content)> LocalEdits { get; } = [];

    /// <summary>所有 <see cref="SaveNoteAsync"/> 的便签 id，按发生顺序。</summary>
    public List<Guid> SavedNoteIds { get; } = [];

    /// <summary>所有的 <see cref="MarkNoteClosed"/> 调用。</summary>
    public List<Guid> MarkedClosed { get; } = [];

    /// <summary>所有的 <see cref="DeleteNoteAsync"/> 调用。</summary>
    public List<Guid> DeletedNoteIds { get; } = [];

    /// <summary>
    /// 每次 <see cref="DeleteNoteAsync"/> 被调用时，这张便签<strong>此前是否已经落过盘</strong>。
    /// </summary>
    /// <remarks>
    /// 存在的理由只有一条不变式：删一张<strong>开着</strong>的便签时，内容必须在文件被搬进
    /// 回收站之前写进磁盘。便签窗口关闭时自己会存一次，但那一次排在消息队列里，
    /// 等它跑起来便签可能已经不在 <c>NoteStore</c> 里而静默返回——
    /// 失效的后果是用户丢掉最后半秒敲的字，而界面上看不出任何异常。
    /// </remarks>
    public List<bool> SavedBeforeDelete { get; } = [];

    /// <summary>
    /// <see cref="DeleteNoteAsync"/> 之外还要做的事。
    /// </summary>
    /// <remarks>
    /// 真实现里「把便签从 <c>NoteStore</c> 与 <c>SearchIndex</c> 里摘掉」发生在
    /// <c>TrashService</c>，而本替身是哑的、不碰任何 Store。
    /// 可是「删完之后列表里少一条」这条断言需要那个副作用真的发生，
    /// 所以由装配处（<c>ManagerHarness</c>）把它接上，而不是让替身去认识 Store。
    /// </remarks>
    public Action<Guid>? DeleteEffect { get; set; }

    /// <summary><see cref="OpenNote"/> 的返回值来源。缺省时返回 <c>null</c>（便签不存在）。</summary>
    public Func<Guid, NoteOpenRequest?>? OpenNoteHandler { get; set; }

    /// <summary><see cref="OpenAll"/> 的返回值。</summary>
    public List<NoteOpenRequest> OpenAllResult { get; } = [];

    /// <summary><see cref="LoadAllAsync"/> 是否被调用过，以及调用次数。</summary>
    public int LoadAllCallCount { get; private set; }

    /// <inheritdoc />
    public Task LoadAllAsync(CancellationToken ct = default)
    {
        LoadAllCallCount++;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void ApplyExternalChange(string path, NoteReadResult readResult) =>
        LocalEdits.Add((Guid.Empty, $"外部变更:{path}"));

    /// <inheritdoc />
    public void ApplyLocalEdit(Note note, string content)
    {
        ArgumentNullException.ThrowIfNull(note);

        // 复刻 NoteService.ApplyLocalEdit 里与本替身相关的那一步：写回正文。
        // 标题缓存的失效是 Note.Content setter 自己的事，这里不需要（也不该）插手。
        note.Content = content;

        LocalEdits.Add((note.Id, content));
    }

    /// <summary>所有 <see cref="ApplyColorEdit"/> 调用，按发生顺序。</summary>
    public List<(Guid NoteId, NoteColor Color)> ColorEdits { get; } = [];

    /// <summary>
    /// 所有 <see cref="ApplyTagsEdit"/> 调用，按发生顺序。
    /// </summary>
    /// <remarks>
    /// 记的是<strong>原样传进来的那一份</strong>，不做规范化——规范化是业务规则，
    /// 由真实现（<c>NoteService</c>）负责，验它的是 Integration.Tests。
    /// 这里要验的是「ViewModel 有没有把该传的传对」，替身自己再实现一遍规则
    /// 只会让两边一起错还彼此印证。
    /// </remarks>
    public List<(Guid NoteId, IReadOnlyList<string> Tags)> TagsEdits { get; } = [];

    /// <inheritdoc />
    public void ApplyColorEdit(Note note, NoteColor color)
    {
        ArgumentNullException.ThrowIfNull(note);

        // 同 ApplyLocalEdit：复刻真实现里与本替身相关的那一步。不写回的话，
        // 「改完之后管理器那行的颜色点变了吗」这条断言就无从谈起。
        note.Color = color;

        ColorEdits.Add((note.Id, color));
    }

    /// <inheritdoc />
    public void ApplyTagsEdit(Note note, IReadOnlyList<string> tags)
    {
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(tags);

        note.Tags.Clear();
        note.Tags.AddRange(tags);

        TagsEdits.Add((note.Id, tags));
    }

    /// <summary>设成非 null 后，<see cref="SaveNoteAsync"/> 会抛出它。用于验证失败路径。</summary>
    public Exception? SaveException { get; set; }

    /// <inheritdoc />
    public Task SaveNoteAsync(Guid noteId)
    {
        SavedNoteIds.Add(noteId);

        if (SaveException is not null)
        {
            throw SaveException;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SaveAllAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>所有 <see cref="CreateNoteAsync"/> 收到的参数，按发生顺序。</summary>
    public List<(NoteColor? Color, string? TargetFolder)> CreatedNotes { get; } = [];

    /// <summary><see cref="CreateNoteAsync"/> 的返回值来源。缺省时现造一张。</summary>
    /// <remarks>
    /// 缺省给一张真便签而不是抛异常：新建便签是托盘菜单与管理器的常规动作，
    /// 大多数用例关心的只是「建完之后窗口有没有按预期开出来」，
    /// 让它们每个都先配一次 <c>CreateNoteHandler</c> 纯属噪音。
    /// </remarks>
    public Func<Note>? CreateNoteHandler { get; set; }

    /// <summary>便签建出来之后要发生的事，由装配处补上。</summary>
    /// <remarks>
    /// 真实现里「进 <c>NoteStore</c>、通知索引」是 <c>NoteService.CreateNoteAsync</c>
    /// 自己做的两步。替身不碰那个 Store——它由装配处持有，与 <see cref="DeleteEffect"/>
    /// 同一个理由，也让这里成为补上那两步的唯一地方。
    /// </remarks>
    public Action<Note>? CreateEffect { get; set; }

    /// <inheritdoc />
    public Task<Note> CreateNoteAsync(NoteColor? color = null, string? targetFolder = null)
    {
        CreatedNotes.Add((color, targetFolder));

        Note note = CreateNoteHandler is { } handler ? handler() : NewNote();

        CreateEffect?.Invoke(note);

        return Task.FromResult(note);
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

    /// <inheritdoc />
    public Task MoveNoteAsync(Guid noteId, string targetFolder) => Task.CompletedTask;

    /// <inheritdoc />
    public Task DeleteNoteAsync(Guid noteId)
    {
        SavedBeforeDelete.Add(SavedNoteIds.Contains(noteId));
        DeletedNoteIds.Add(noteId);

        DeleteEffect?.Invoke(noteId);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RestoreFromTrashAsync(Guid noteId, string? targetPath) => Task.CompletedTask;

    /// <inheritdoc />
    public NoteOpenRequest? OpenNote(Guid noteId) => OpenNoteHandler?.Invoke(noteId);

    /// <inheritdoc />
    public IReadOnlyList<NoteOpenRequest> OpenAll() => OpenAllResult;

    /// <inheritdoc />
    public void MarkNoteClosed(Guid noteId) => MarkedClosed.Add(noteId);
}
