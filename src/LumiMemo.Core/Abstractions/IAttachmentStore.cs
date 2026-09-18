namespace LumiMemo.Core.Abstractions;

/// <summary>
/// 附件的磁盘操作（§6）。实现在 Infrastructure 层，操作笔记目录下的附件目录。
/// </summary>
/// <remarks>
/// <para>
/// 文件名规则是 <c>{yyyyMMdd}-{HHmmss}-{8位随机十六进制}.{ext}</c>（§6.1）。
/// <strong>随机后缀是必需的</strong>：用递增序号会在「同一秒粘贴两次」或「从别处拷入同名文件」时
/// 覆盖已有附件，导致便签里的图片被静默替换成另一张——这是静默的数据损坏。
/// 因此实现必须在写入前检查目标是否存在，存在则重新生成随机后缀。
/// </para>
/// <para>
/// 本接口只负责文件的存放与枚举。正文里的相对路径怎么算（§6.2）、
/// 移动便签时怎么重写链接（§6.3）、哪些附件成了孤儿（§6.4），由各自的服务负责。
/// </para>
/// </remarks>
public interface IAttachmentStore
{
    /// <summary>把一段内容存为附件。</summary>
    /// <param name="content">附件字节流。</param>
    /// <param name="extension">扩展名，含点号，例如 <c>.png</c>。</param>
    /// <returns>生成的文件名（不含目录）。</returns>
    Task<string> SaveAsync(Stream content, string extension, CancellationToken ct = default);

    /// <summary>枚举附件目录下的全部文件名，用于孤儿扫描（§6.4）。</summary>
    IReadOnlyList<string> ListFileNames();

    /// <summary>删除一个附件。孤儿清理时使用（§6.4）。</summary>
    Task DeleteAsync(string fileName, CancellationToken ct = default);
}
