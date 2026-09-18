using System.Globalization;
using System.Security.Cryptography;
using LumiMemo.Core.Abstractions;
using LumiMemo.Infrastructure.Io;

namespace LumiMemo.Infrastructure.Storage;

/// <summary>
/// 便签文件的原子写盘（§11.2）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>为什么不能用 <c>File.WriteAllText</c>：</strong>它先把文件截断为 0 字节再往里写。
/// 如果此刻断电、蓝屏、或者用户从任务管理器强杀进程，磁盘上留下的就是一个<strong>空文件</strong>——
/// 用户的便签内容直接消失。这不是理论风险，是这类程序最常见的丢数据方式。
/// §24.3 因此明令禁止在保存便签时使用它。
/// </para>
/// <para>
/// 本类改为：写到<strong>同目录</strong>下的临时文件 → 落盘（<c>Flush(flushToDisk: true)</c>）
/// → 用 <c>File.Replace</c> 原子换名。任何一步失败，原文件都还完好无损。
/// 临时文件必须与目标同目录，因为 <c>File.Replace</c> 要求两者同卷，
/// 跨卷会退化成「复制 + 删除」，原子性随之消失。
/// </para>
/// <para>
/// 本类只认字节，不认识 <c>Note</c> 也不认识 Front Matter——序列化由
/// <see cref="FrontMatterSerializer"/> 负责，这样原子性这件事只有一处实现、一处测试。
/// </para>
/// </remarks>
public sealed class AtomicFileWriter
{
    /// <summary>临时文件后缀。启动时按此后缀做一次全盘清理（§11.2、§5.5）。</summary>
    public const string TempSuffix = ".lumitmp";

    private readonly IClock _clock;
    private readonly IReadOnlyList<int> _retryDelays;

    /// <param name="clock">临时文件名里的时间戳来源（§21.5：不直接用 <c>DateTimeOffset.Now</c>）。</param>
    /// <param name="retryDelaysMilliseconds">
    /// 换名失败时的重试间隔。默认 100/300/600 毫秒（§11.2）；测试传一个空集合来关掉等待。
    /// </param>
    public AtomicFileWriter(IClock clock, IReadOnlyList<int>? retryDelaysMilliseconds = null)
    {
        ArgumentNullException.ThrowIfNull(clock);

        _clock = clock;
        _retryDelays = retryDelaysMilliseconds ?? StorageRetry.DefaultDelaysMilliseconds;
    }

    /// <summary>
    /// 把 <paramref name="bytes"/> 原子地写入 <paramref name="target"/>。
    /// </summary>
    /// <param name="target">目标文件完整路径。目录不存在会自动创建。</param>
    /// <param name="bytes">文件的<strong>全部</strong>字节，含 BOM。</param>
    /// <param name="lastWriteTime">写入成功后设置的文件修改时间，通常取 <c>Note.UpdatedAt</c>。</param>
    /// <param name="cancellationToken">取消标记。取消时若尚未换名，临时文件会被删除。</param>
    /// <remarks>
    /// <para>
    /// 本方法<strong>总是</strong>执行写入，不做「内容没变就跳过」的判断。§5.9 的那个自检放在
    /// 仓储层（<c>MarkdownNoteRepository</c>），因为那条规则要求比较的是
    /// 「将要写出的字节」与<strong>读入时的原始字节</strong>，而「读入时是什么」只有仓储层知道。
    /// </para>
    /// <para>
    /// 换个问法为什么不在这里比：这里能比对的只有磁盘<strong>当前</strong>的字节。两者在
    /// 「便签没有改动、但文件被外部程序改过」时结论相反——比磁盘当前内容会认为「不一样」，
    /// 于是把内存里的旧内容写回去，正好<strong>覆盖掉用户刚在外部做的修改</strong>。
    /// 按文档的原意比对读入时的字节，这种情况会判定「没变化、跳过写入」，是安全的那一侧。
    /// </para>
    /// </remarks>
    public async Task WriteAsync(
        string target,
        byte[] bytes,
        DateTimeOffset lastWriteTime,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentNullException.ThrowIfNull(bytes);

        target = LongPath.Ensure(target);

        string? directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = BuildTempPath(target);

        try
        {
            WriteTempFile(tempPath, bytes);
            await PublishAsync(tempPath, target, cancellationToken).ConfigureAwait(false);

            // 临时文件是刚创建的，时间戳是「现在」。必须显式改回便签自己的修改时间，
            // 否则外部编辑器、文件管理器和同步工具看到的都是错的（§11.2）。
            File.SetLastWriteTimeUtc(target, lastWriteTime.UtcDateTime);
        }
        catch (Exception)
        {
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// 递归删除笔记目录下所有残留的 <c>.lumitmp</c>（§11.2、§5.5 启动流程）。
    /// </summary>
    /// <param name="root">笔记目录根路径。</param>
    /// <returns>实际删掉的文件个数，供启动日志记录。</returns>
    /// <remarks>
    /// <para>
    /// 上次运行被强杀时，临时文件会留在磁盘上。它在同一目录里会骚扰用户
    /// （外部编辑器的文件列表、Everything 搜索、同步工具的上传队列），而且它已经没有任何用处——
    /// 换名是原子操作，能看到的 <c>.lumitmp</c> 必定是没换成功的残骸。
    /// </para>
    /// <para>
    /// 删不掉的文件<strong>不报错</strong>：可能被另一个实例或同步工具占用，
    /// 而启动流程不该因为一个垃圾文件失败。下一个启动周期会再试一次。
    /// </para>
    /// <para>
    /// 递归时跳过访问不了的目录（<see cref="EnumerationOptions.IgnoreInaccessible"/>），
    /// 理由同上：清理垃圾不值得让扫描中止。
    /// </para>
    /// </remarks>
    public static int CleanupOrphanedTempFiles(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);

        if (!Directory.Exists(root))
        {
            return 0;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        int deleted = 0;
        foreach (string file in Directory.EnumerateFiles(LongPath.Ensure(root), "*" + TempSuffix, options))
        {
            if (TryDelete(file))
            {
                deleted++;
            }
        }

        return deleted;
    }

    /// <summary>把字节写进临时文件，并强制刷到物理磁盘。</summary>
    /// <remarks>
    /// <para>
    /// <c>FileMode.CreateNew</c>：临时文件名里带了时间戳和随机串，本就不该存在同名文件。
    /// 万一撞上（几乎不可能），宁可直接失败也不要覆盖别人的文件。
    /// </para>
    /// <para>
    /// <c>flushToDisk: true</c> 是这一整套机制的关键一环。不这样做的话，
    /// 数据只是进了操作系统的缓存，换名之后的文件「看起来写好了」，断电后却可能变回旧内容甚至全空。
    /// 只做换名而不落盘，等于把赌注押在「不会断电」上。
    /// </para>
    /// </remarks>
    private static void WriteTempFile(string tempPath, byte[] bytes)
    {
        using var stream = new FileStream(
            tempPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);

        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>把临时文件换名成目标文件，失败时按 §11.2 的节奏重试。</summary>
    /// <remarks>
    /// 只重试换名这一步，不重试「写临时文件」：临时文件名每次都不同，
    /// 让它失败的原因（磁盘满、无权限）不是等几百毫秒就能好的，
    /// 而重试还会把上一次的残骸留在磁盘上。
    /// </remarks>
    private Task PublishAsync(string tempPath, string target, CancellationToken cancellationToken) =>
        StorageRetry.RunAsync(
            () =>
            {
                if (File.Exists(target))
                {
                    // ignoreMetadataErrors: true —— 把「元数据搬不过去」与「文件换名失败」区分开。
                    // 某些网络盘、FAT 盘不支持完整的元数据操作，此时换名本身是成功的，
                    // 若因此抛异常，保存会被报成失败，而磁盘上其实已经是新内容了。
                    File.Replace(tempPath, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, target);
                }
            },
            _retryDelays,
            cancellationToken);

    private string BuildTempPath(string target)
    {
        string timestamp = _clock.Now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        string random = RandomNumberGenerator.GetHexString(8, lowercase: true);
        return $"{target}.{timestamp}-{random}{TempSuffix}";
    }

    /// <summary>尽力删除一个文件，删不掉就算了。</summary>
    /// <remarks>
    /// 清理是<strong>尽力而为</strong>的辅助动作，不该反过来把一个正在处理的错误盖掉：
    /// 若在「保存失败」的清理阶段再抛异常，调用方看到的是删文件失败，
    /// 而不是真正的原因（磁盘满、文件被占用）。这就是唯一允许吞掉异常的地方。
    /// </remarks>
    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
