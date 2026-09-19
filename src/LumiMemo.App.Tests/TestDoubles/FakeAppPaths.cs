using System.IO;
using LumiMemo.Core.Abstractions;

namespace LumiMemo.App.Tests.TestDoubles;

/// <summary>
/// 不出内存的 <see cref="IAppPaths"/> 替身。
/// </summary>
/// <remarks>
/// <para>
/// <c>LumiMemo.Core.Tests</c> 里有一个同名件，<strong>刻意不复用</strong>：
/// 两个测试工程之间没有引用关系，这是仓库既有的约定——
/// 复用一个替身要么建共享工程，要么让一个测试工程去引用另一个的产物，
/// 两条路都比再写这三十行贵。
/// </para>
/// <para>
/// 只有 <see cref="NotesFolder"/> 与由它派生的三个路径会被读到。默认值
/// <c>D:\notes</c> 与用例里便签的 <c>FilePath</c> 同根，于是
/// <c>Path.GetRelativePath</c> 算出来正好是文件名本身。
/// </para>
/// </remarks>
public sealed class FakeAppPaths : IAppPaths
{
    /// <param name="notesFolder">
    /// 笔记目录；传 <see langword="null"/> 模拟「用户尚未选定目录」。
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
    public string TrashDirectory => Path.Combine(RequireNotesFolder(), ".lumimemo", "trash");

    /// <inheritdoc />
    public string TrashIndexFile =>
        Path.Combine(RequireNotesFolder(), ".lumimemo", "trash-index.json");

    /// <inheritdoc />
    public string AttachmentsDirectory => Path.Combine(RequireNotesFolder(), "attachments");

    private string RequireNotesFolder() =>
        NotesFolder ?? throw new InvalidOperationException("尚未选定笔记目录。");
}
