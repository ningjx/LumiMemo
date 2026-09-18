namespace LumiMemo.Core.Events;

/// <summary>
/// 文件监听事件的种类（§10.1）。
/// </summary>
/// <remarks>
/// <see cref="Renamed"/> 必须与「删除 + 新建」区分开，否则外部重命名一张便签会
/// 变成「便签消失又出现两张」——§21.3 的 watcher 测试专门覆盖这一条。
/// </remarks>
public enum FileWatchChangeKind
{
    Created,

    Changed,

    Deleted,

    /// <summary>重命名或移动。此时事件里同时带新旧路径。</summary>
    Renamed,

    /// <summary>
    /// 监听出错，通常是缓冲区溢出（§10.4）。
    /// </summary>
    /// <remarks>
    /// <strong>必须处理</strong>：溢出后 <c>FileSystemWatcher</c> 不会自动恢复，
    /// 后续所有变更都会静默丢失。对「Markdown 是唯一数据源」的产品来说，
    /// 这是系统性数据不一致的来源，收到本事件必须触发一次全量重扫描。
    /// </remarks>
    Error,
}
