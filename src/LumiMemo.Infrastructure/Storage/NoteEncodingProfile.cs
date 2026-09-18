using System.Text;

namespace LumiMemo.Infrastructure.Storage;

/// <summary>
/// 一个笔记文件在磁盘上的<strong>编码形态</strong>：用哪种编码、要不要写 BOM（§5.9）。
/// </summary>
/// <remarks>
/// <para>
/// §5.9 只把「有没有 BOM」交给 <c>Note.HadBom</c>，但真实世界里还有第二个维度：
/// <strong>文件根本不是 UTF-8</strong>。老版本的记事本、某些中文编辑器会写成 GBK，
/// 「另存为 Unicode」会写成 UTF-16。这些文件被读进来之后，如果按 UTF-8 写回去，
/// 整个文件会被改名换姓——用户看到的是自己整篇笔记变成乱码。
/// </para>
/// <para>
/// 所以「读的时候发现了什么编码」必须一直带到「写回去」那一步。它<strong>不能</strong>放在
/// <c>Note</c> 上：那是 Core 层的模型，不该认识 <see cref="Encoding"/> 这种 BCL 类型，
/// 而且它跟着文件路径走、不跟着便签身份走。因此这份形态由仓储按路径缓存。
/// </para>
/// </remarks>
public sealed class NoteEncodingProfile
{
    private NoteEncodingProfile(Encoding encoding, byte[] preamble)
    {
        Encoding = encoding;
        Preamble = preamble;
    }

    /// <summary>用于编码正文的编码器。构造时均<strong>不带</strong> BOM，BOM 由 <see cref="Preamble"/> 显式给出。</summary>
    public Encoding Encoding { get; }

    /// <summary>BOM 字节。无 BOM 时为空数组。</summary>
    public byte[] Preamble { get; }

    /// <summary>该形态是否写 BOM。</summary>
    public bool HasBom => Preamble.Length > 0;

    /// <summary>UTF-8，无 BOM。新建文件的初始形态（§5.9）。</summary>
    public static NoteEncodingProfile Utf8 { get; } = new(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), []);

    /// <summary>UTF-8，带 BOM（记事本写出来的那种）。</summary>
    public static NoteEncodingProfile Utf8WithBom { get; } = new(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), [0xEF, 0xBB, 0xBF]);

    /// <summary>UTF-16 小端，带 BOM。记事本「Unicode」就是它。</summary>
    public static NoteEncodingProfile Utf16Le { get; } = new(new UnicodeEncoding(bigEndian: false, byteOrderMark: false), [0xFF, 0xFE]);

    /// <summary>UTF-16 大端，带 BOM。</summary>
    public static NoteEncodingProfile Utf16Be { get; } = new(new UnicodeEncoding(bigEndian: true, byteOrderMark: false), [0xFE, 0xFF]);

    /// <summary>系统 ANSI 代码页，无 BOM。§5.9 的非法 UTF-8 回退目标。</summary>
    public static NoteEncodingProfile SystemAnsi { get; } = new(CreateSystemAnsiEncoding(), []);

    /// <summary>指定代码页的 ANSI 形态，无 BOM。</summary>
    /// <remarks>
    /// 生产代码一律用 <see cref="SystemAnsi"/>，这个方法只服务于一个目的：
    /// 让「非法 UTF-8 回退到 ANSI」这条规则能在<strong>任意</strong>机器上被确定性地验证。
    /// 英文 Windows 上 <c>GetEncoding(0)</c> 是 1252，中文 Windows 上是 936，
    /// 测试不该因为跑在哪台机器上而变红或变绿。
    /// </remarks>
    public static NoteEncodingProfile AnsiWith(Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);

        return new NoteEncodingProfile(encoding, []);
    }

    /// <summary>
    /// 取系统 ANSI 代码页。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这里必须先注册 <see cref="CodePagesEncodingProvider"/>，否则
    /// <see cref="Encoding.GetEncoding(int)"/> 会<strong>静默返回 UTF-8</strong>——
    /// 正好就是 §5.9 明令禁止的「静默按 UTF-8 硬读」：不报错、不抛异常，
    /// 只是把 GBK 文件读成一片乱码，然后用户一动键盘就把乱码写回磁盘。
    /// </para>
    /// <para>
    /// 该 provider 随 .NET 运行时提供（Windows 上），<strong>不需要额外的 NuGet 包</strong>。
    /// </para>
    /// <para>
    /// 用 <c>GetEncoding(0)</c>（系统 ANSI 代码页）而不是当前区域性的代码页：
    /// 前者是这个文件当初最可能是被哪个编辑器写出来的答案，与线程的
    /// <c>CurrentCulture</c> 无关。
    /// </para>
    /// </remarks>
    private static Encoding CreateSystemAnsiEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(0);
    }
}
