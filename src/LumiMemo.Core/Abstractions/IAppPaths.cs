namespace LumiMemo.Core.Abstractions;

/// <summary>
/// 应用用到的全部路径（§8.1）。
/// </summary>
/// <remarks>
/// <para>
/// 路径分两类，存放位置刻意不同：
/// </para>
/// <list type="bullet">
///   <item>
///     <strong>设备状态</strong>（<see cref="SettingsFile"/>、<see cref="LayoutFile"/>、
///     <see cref="LogDirectory"/>）放 <c>%LOCALAPPDATA%\LumiMemo\</c>，<strong>不跟随笔记目录</strong>。
///     否则 OneDrive / Dropbox 会把 A 机器的窗口坐标同步到 B 机器，窗口就跑到了屏幕外。
///   </item>
///   <item>
///     <strong>笔记相关</strong>（<see cref="TrashDirectory"/>、<see cref="AttachmentsDirectory"/>）
///     在笔记目录内。回收站必须与笔记<strong>同卷</strong>，否则 <c>File.Move</c> 会从原子操作
///     退化成「复制 + 删除」。
///   </item>
/// </list>
/// <para>
/// 把路径收敛到这一个抽象后面，是为了将来加便携模式时只需换一个实现，
/// 不改任何调用方（§8.1、§22.5）。
/// </para>
/// </remarks>
public interface IAppPaths
{
    /// <summary><c>%LOCALAPPDATA%\LumiMemo\settings.json</c></summary>
    string SettingsFile { get; }

    /// <summary><c>%LOCALAPPDATA%\LumiMemo\layout.json</c></summary>
    string LayoutFile { get; }

    /// <summary><c>%LOCALAPPDATA%\LumiMemo\logs\</c></summary>
    string LogDirectory { get; }

    /// <summary>
    /// <c>%LOCALAPPDATA%\LumiMemo\recovery\</c>。
    /// 一期不做崩溃恢复，但路径先留在这里（§11.6、§8.1）。
    /// </summary>
    string RecoveryDirectory { get; }

    /// <summary>
    /// 来自 <see cref="Models.AppSettings.NotesFolder"/>。
    /// <see langword="null"/> 表示<strong>用户尚未选定笔记目录</strong>（§8.6 首次运行向导）——
    /// 这是合法的启动状态，不是错误。
    /// </summary>
    /// <remarks>
    /// 刻意用 <see langword="null"/> 而不是空字符串表达「没有」：空串会让下面三个派生路径
    /// 退化成 <c>.lumimemo\trash</c> 这样的<strong>相对路径</strong>，
    /// 谁在向导之前碰一下，就会在<strong>进程当前目录</strong>下静默建出目录树。
    /// 可空类型能让编译器逼着每个消费方处理这种状态。
    /// </remarks>
    string? NotesFolder { get; }

    /// <summary><c>{NotesFolder}/.lumimemo/trash</c></summary>
    /// <exception cref="InvalidOperationException">尚未选定笔记目录。</exception>
    string TrashDirectory { get; }

    /// <summary><c>{NotesFolder}/.lumimemo/trash-index.json</c></summary>
    /// <exception cref="InvalidOperationException">尚未选定笔记目录。</exception>
    string TrashIndexFile { get; }

    /// <summary><c>{NotesFolder}/{AttachmentsFolderName}</c></summary>
    /// <exception cref="InvalidOperationException">尚未选定笔记目录。</exception>
    string AttachmentsDirectory { get; }
}
