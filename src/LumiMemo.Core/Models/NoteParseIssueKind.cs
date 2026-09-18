namespace LumiMemo.Core.Models;

/// <summary>
/// 解析便签文件时发现的异常种类（§9.2、§5.10 的降级矩阵）。
/// </summary>
/// <remarks>
/// 每一种都对应 §5.10 表格中的一行。核心原则是**任何情况下都不能让用户的便签
/// 从管理器和桌面上消失**，所以这些异常都是"降级并标记"，而不是"失败并跳过"。
/// </remarks>
public enum NoteParseIssueKind
{
    /// <summary>Front Matter 里没有 <c>id</c>，启动流程会补一个并回写（§5.5）。</summary>
    MissingId,

    /// <summary>多个文件声明了同一个 <c>id</c>，按 §5.5 的规则重新分配。</summary>
    DuplicateId,

    /// <summary>YAML 语法错误。能解析出的字段照用，全部失败则用默认值。</summary>
    InvalidYaml,

    /// <summary>文件不是合法 UTF-8，按系统 ANSI 回退读取（§5.9）。</summary>
    InvalidEncoding,

    /// <summary>文件超过 1MB：正常加载，但不参与全文搜索（§5.10）。</summary>
    FileTooLarge,

    /// <summary>路径包含非法字符，无法加载。</summary>
    UnreadablePath,

    /// <summary>文件被其他进程独占锁定，重试后仍失败，保留上次内容并标记（§5.10）。</summary>
    TemporarilyLocked,
}
