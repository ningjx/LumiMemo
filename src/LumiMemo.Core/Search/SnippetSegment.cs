namespace LumiMemo.Core.Search;

/// <summary>
/// 摘要里的一段连续文本，以及它是不是命中片段（§12.3）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>刻意不是 HTML。</strong>摘要由 View 用一个 <c>TextBlock</c> 加若干个 <c>Run</c> 渲染
/// （<see cref="IsMatch"/> 为真的那些换个前景色），Core 不拼标记字符串——
/// 拼出来的是给 WPF 看的标记，那就把界面技术漏进了没有第三方依赖的 Core（§4.1），
/// 而且用户正文里的尖括号会被当成标记解析。
/// </para>
/// <para>
/// 省略号也是普通的一段（<see cref="IsMatch"/> 为假），这样 View 只需要一个循环，
/// 不必在开头结尾单独判断"这里要不要画省略号"。
/// </para>
/// </remarks>
/// <param name="Text">这一段的内容。</param>
/// <param name="IsMatch">是否是命中的那一段。</param>
public readonly record struct SnippetSegment(string Text, bool IsMatch);
