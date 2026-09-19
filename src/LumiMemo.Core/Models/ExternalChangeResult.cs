namespace LumiMemo.Core.Models;

/// <summary>一次外部文件变化落到内存之后的结果（§10、§11.4）。</summary>
public enum ExternalChangeKind
{
    /// <summary>什么都不用做：磁盘其实没变（自写事件、或重复通知）。</summary>
    None,

    /// <summary>内存里那张便签被磁盘版替换了（静默重载，§11.4 第 1 行）。</summary>
    Reloaded,

    /// <summary>磁盘上多了一张内存里没有的便签。</summary>
    Created,

    /// <summary>磁盘上那张便签没有了，内存里已摘除。</summary>
    Deleted,

    /// <summary>两边都改了、而且改得不一样，需要用户裁决（§11.4 的真冲突）。</summary>
    Conflict,
}

/// <summary>
/// 把一次外部变化应用到内存之后的结论，交给 App 层决定开窗、关窗还是弹冲突对话框。
/// </summary>
/// <param name="Kind">这次变化的种类。</param>
/// <param name="LocalNote">
/// 内存里那一张，路径上原本没有便签时为 <see langword="null"/>——
/// <strong>唯一例外是 <see cref="ExternalChangeKind.Created"/> 里「老便签被顶掉」那一种</strong>，
/// 那时它是被摘除的旧便签（见 <paramref name="Replaced"/>）。
/// 它也是 <see cref="ExternalChangeKind.Conflict"/> 里「本地那一版」。
/// </param>
/// <param name="Disk">
/// 这次读盘的结果与差异。冲突时它带着<strong>磁盘那一版</strong>，供「重新加载」采用；
/// 覆盖外部版本时，它又是要备份下来的那一版。
/// </param>
/// <param name="Replaced">
/// 被这次变化顶掉的那一张便签，没有时为 <see langword="null"/>。
/// </param>
/// <remarks>
/// <para>
/// <strong>Core 只回答「内存变成了什么样」，不决定界面怎么办</strong>：开窗、关窗、弹对话框
/// 都需要窗口层与对话框，而 Core 两样都不认识（§3.1）。返回一个结论而不是 <c>void</c>，
/// 就是这条分界线的落点。
/// </para>
/// <para>
/// <strong><paramref name="Replaced"/> 为什么不并进 <paramref name="LocalNote"/></strong>：
/// 那会让「内存里现在是哪一张」这个问题在同一个字段上有两种答案
/// （<see cref="ExternalChangeKind.Created"/> 时是这个路径上原来的旧便签、
/// 其余种类时是合并之后留下来的那一张），调用方要先比一次引用才敢用。
/// 拆开之后两个字段各自只有一个含义：现在这张、以及被换掉的那张。
/// 需要关掉旧窗口的只有后者——它的 id 与 <see cref="Disk"/> 里那张<strong>不同</strong>，
/// 不显式关掉的话，那扇窗口会一直挂着一张内存里已经不存在的便签。
/// </para>
/// <para>
/// 出现「换身份」是因为外部编辑器改写了 Front Matter 里的 <c>id</c>，
/// 或者整个文件被另一个版本替换了（比如 git 切了一次分支）。
/// </para>
/// </remarks>
public sealed record ExternalChangeResult(
    ExternalChangeKind Kind,
    Note? LocalNote,
    NoteFileSync Disk,
    Note? Replaced = null);
