using LumiMemo.Core.Abstractions;

namespace LumiMemo.Infrastructure.Io;

/// <summary>
/// <see cref="IAppPaths"/> 的默认实现：设备状态放 <c>%LOCALAPPDATA%\LumiMemo\</c>，
/// 笔记相关路径落在笔记目录内（§8.1）。
/// </summary>
/// <remarks>
/// <para>
/// 构造函数接受一个根目录，是为了让集成测试能指向临时目录而不碰真实用户配置。
/// 无参构造函数走真实位置。
/// </para>
/// <para>
/// 笔记目录与附件目录名来自设置，因此在设置载入后才能确定（§8.6 的首次运行向导）。
/// 这两个值通过可写属性注入，但 <see cref="IAppPaths"/> 只暴露读——消费方无法在中途改掉路径。
/// </para>
/// </remarks>
public sealed class AppPaths : IAppPaths
{
    /// <summary>笔记目录下存放程序元数据的隐藏目录名（§5.1）。</summary>
    public const string MetadataDirectoryName = ".lumimemo";

    private readonly string _localAppDataRoot;

    private string? _notesFolder;
    private string _attachmentsFolderName = "attachments";

    /// <summary>使用真实的 <c>%LOCALAPPDATA%\LumiMemo</c>。</summary>
    public AppPaths()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LumiMemo"))
    {
    }

    /// <param name="localAppDataRoot">设备状态的根目录。测试传入临时目录。</param>
    public AppPaths(string localAppDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataRoot);

        _localAppDataRoot = localAppDataRoot;
    }

    /// <inheritdoc />
    public string SettingsFile => Path.Combine(_localAppDataRoot, "settings.json");

    /// <inheritdoc />
    public string LayoutFile => Path.Combine(_localAppDataRoot, "layout.json");

    /// <inheritdoc />
    public string LogDirectory => Path.Combine(_localAppDataRoot, "logs");

    /// <inheritdoc />
    public string RecoveryDirectory => Path.Combine(_localAppDataRoot, "recovery");

    /// <inheritdoc />
    public string? NotesFolder => _notesFolder;

    /// <inheritdoc />
    public string TrashDirectory => Path.Combine(RequireNotesFolder(), MetadataDirectoryName, "trash");

    /// <inheritdoc />
    public string TrashIndexFile =>
        Path.Combine(RequireNotesFolder(), MetadataDirectoryName, "trash-index.json");

    /// <inheritdoc />
    public string AttachmentsDirectory => Path.Combine(RequireNotesFolder(), _attachmentsFolderName);

    /// <summary>设备状态的根目录（<c>%LOCALAPPDATA%\LumiMemo</c>）。</summary>
    public string LocalAppDataRoot => _localAppDataRoot;

    /// <summary>
    /// 设置笔记目录。由设置载入流程与「切换笔记目录」流程调用（§8.6、§17.1）。
    /// </summary>
    /// <remarks>
    /// 要求绝对路径。笔记目录来自 <c>settings.json</c>——那是个用户能手改、
    /// 还可能被网盘同步过来的文件，属于系统边界，值得在这里挡一道：
    /// 相对路径一旦进来，上面三个派生路径就全都指向「进程当前目录」，
    /// 而不同启动方式下当前目录是不同的，用户笔记会散落在莫名其妙的地方。
    /// </remarks>
    public void SetNotesFolder(string notesFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notesFolder);

        if (!Path.IsPathFullyQualified(notesFolder))
        {
            throw new ArgumentException(
                $"笔记目录必须是绝对路径，实际收到「{notesFolder}」。",
                nameof(notesFolder));
        }

        _notesFolder = notesFolder;
    }

    /// <summary>设置附件目录名（相对名，不含路径分隔符）。由设置载入流程调用（§6.1）。</summary>
    public void SetAttachmentsFolderName(string folderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);

        _attachmentsFolderName = folderName;
    }

    /// <summary>
    /// 建立设备状态目录（<c>logs\</c>、<c>recovery\</c>）。启动序列的第一步（§17.1）。
    /// </summary>
    /// <remarks>
    /// 只建 <c>%LOCALAPPDATA%</c> 下的目录。笔记目录下的 <c>.lumimemo\trash\</c>
    /// 由回收站服务在需要时创建——笔记目录此刻可能还没被用户选定。
    /// </remarks>
    public void EnsureLocalAppDataDirectories()
    {
        Directory.CreateDirectory(_localAppDataRoot);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(RecoveryDirectory);
    }

    /// <summary>
    /// 取笔记目录，未选定则抛异常。
    /// </summary>
    /// <remarks>
    /// 这里<strong>故意抛异常而不是退回空串</strong>：调用方在还没选目录时来问
    /// 「回收站在哪」，说明它的执行顺序错了。让它在当下就炸掉、带上清晰的说明，
    /// 远好过让它在错误的目录下静默建出一棵树。
    /// </remarks>
    private string RequireNotesFolder() =>
        _notesFolder ?? throw new InvalidOperationException(
            "尚未选定笔记目录。依赖笔记目录的路径必须先经过 AppPaths.SetNotesFolder"
            + "（启动序列见 §17.1，首次运行向导见 §8.6）。");
}
