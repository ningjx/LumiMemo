namespace LumiMemo.Core.Models;

/// <summary>
/// 正文中对附件的引用，扫描孤儿附件时用（§6.4、§9.2）。
/// </summary>
/// <param name="RelativePath">
/// 正文里写的相对路径原文，例如 <c>../attachments/20260919-101712-7e1d90ab.jpg</c>。
/// </param>
/// <param name="FileName">路径末段的文件名，用于与 <c>attachments/</c> 目录的内容比对。</param>
public sealed record AttachmentRef(string RelativePath, string FileName);
