namespace LumiMemo.Core.Models;

/// <summary>
/// 结束分隔符 <c>---</c> 之后、正文之前的<strong>结构形态</strong>（§5.2、§5.9）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要它：§5.2 要求 Front Matter 与正文之间留<strong>一个空行</strong>，
/// 而 §5.9 要求写回时「末尾换行」原样保留。这两条一起看，分隔符之后的字节有且只有
/// 三种合法形态，而它们用「正文内容」是表达不出来的——三种形态都可能对应同一个空正文。
/// </para>
/// <para>
/// 把这三种形态显式记下来，才能同时做到两件本会互相冲突的事：
/// 正文里<strong>不含</strong>那个约定空行（便签窗口打开时不会凭空多一个空的首行），
/// 而写回时又<strong>逐字节</strong>还原原文件。
/// </para>
/// <para>
/// 新建便签的默认值是 <see cref="LineBreakAndBlankLine"/>，即 §5.2 定义的标准形态。
/// </para>
/// </remarks>
public enum FrontMatterTail
{
    /// <summary>
    /// 结束分隔符就是文件末尾，后面没有任何字符。
    /// </summary>
    /// <remarks>此形态下正文必然为空串——分隔符后面没有东西可以当正文。</remarks>
    None,

    /// <summary>
    /// 结束分隔符后面只有一个行尾换行，没有空行。
    /// </summary>
    /// <remarks>多见于「文件原本没有 Front Matter，程序补写上去」的情形（§5.10 的追加策略）。</remarks>
    LineBreakOnly,

    /// <summary>
    /// 结束分隔符后面是一个行尾换行，再一个空行（§5.2 的标准形态）。
    /// </summary>
    LineBreakAndBlankLine,
}
