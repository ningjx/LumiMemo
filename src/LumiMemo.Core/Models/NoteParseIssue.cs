namespace LumiMemo.Core.Models;

/// <summary>
/// 一条解析异常记录（§9.2）。用于在管理器 UI 上给便签打警告标记、并写日志。
/// </summary>
/// <param name="Kind">异常种类，决定 UI 上显示哪种标记。</param>
/// <param name="Detail">补充说明。只进日志，不在 UI 上直接展示。</param>
public sealed record NoteParseIssue(NoteParseIssueKind Kind, string? Detail);
