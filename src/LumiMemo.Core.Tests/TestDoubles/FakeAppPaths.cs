using LumiMemo.Core.Abstractions;

namespace LumiMemo.Core.Tests.TestDoubles;

/// <summary>
/// 不出内存的 <see cref="IAppPaths"/> 替身。
/// </summary>
/// <remarks>
/// <para>
/// 只有 <see cref="NotesFolder"/> 会被 Core 层的服务读到（<c>TrashService</c> 用它把便签的
/// 绝对路径换算成相对路径），其余路径在本层没有任何消费方，因此这里给的是不会有人碰的占位值。
/// </para>
/// <para>
/// 默认值 <c>D:\notes</c> 是刻意选的：<see cref="FakeNoteRepository"/> 与用例里的
/// <c>NewNote</c> 都用 <c>D:\notes\{id}.md</c> 当 <c>FilePath</c>，
/// 于是 <c>Path.GetRelativePath</c> 算出来正好是 <c>{id}.md</c>，
/// 与真实运行时「便签就在笔记目录根上」的情形一致。
/// </para>
/// </remarks>
public sealed class FakeAppPaths : IAppPaths
{
    /// <summary>用一个固定根目录建替身。</summary>
    /// <param name="notesFolder">
    /// 笔记目录；传 <see langword="null"/> 模拟「用户尚未选定目录」（§8.6 首次运行向导）。
    /// </param>
    public FakeAppPaths(string? notesFolder = @"D:\notes") => NotesFolder = notesFolder;

    /// <inheritdoc />
    public string SettingsFile => @"C:\fake\settings.json";

    /// <inheritdoc />
    public string LayoutFile => @"C:\fake\layout.json";

    /// <inheritdoc />
    public string LogDirectory => @"C:\fake\logs\";

    /// <inheritdoc />
    public string RecoveryDirectory => @"C:\fake\recovery\";

    /// <inheritdoc />
    public string? NotesFolder { get; }

    /// <inheritdoc />
    public string TrashDirectory =>
        Path.Combine(RequireNotesFolder(), ".lumimemo", "trash");

    /// <inheritdoc />
    public string TrashIndexFile =>
        Path.Combine(RequireNotesFolder(), ".lumimemo", "trash-index.json");

    /// <inheritdoc />
    public string AttachmentsDirectory =>
        Path.Combine(RequireNotesFolder(), "attachments");

    private string RequireNotesFolder() =>
        NotesFolder ?? throw new InvalidOperationException("尚未选定笔记目录。");
}
