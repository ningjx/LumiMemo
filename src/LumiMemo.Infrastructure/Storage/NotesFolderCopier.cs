using LumiMemo.Infrastructure.Io;
using Microsoft.Extensions.Logging;

namespace LumiMemo.Infrastructure.Storage;

/// <summary>
/// 换笔记目录时的便签搬运：把源目录里的 <c>.lumi</c> 复制到目标目录。
/// </summary>
/// <remarks>
/// <para>
/// <strong>只读源、不删源</strong>：这是一次复制，不是移动。用户换目录的理由常常是
/// 「换台机器」「换个网盘同步」，原目录留着才是安全的默认；搬错了也不过是白占一份空间。
/// </para>
/// <para>
/// <strong>同名跳过、不覆盖</strong>：同名意味着同一个便签 id（文件名就是 id，见 §5.5），
/// 目标目录里那份可能更新——覆盖它就是拿旧数据盖新数据。跳过是唯一安全的选择。
/// </para>
/// <para>
/// 单个文件复制失败<strong>不中断整批</strong>：一个被占用的文件不该让其余几十张便签都过不去。
/// 失败计入 <see cref="CopyResult.Failed"/>，由调用方决定怎么告诉用户。
/// </para>
/// </remarks>
public sealed class NotesFolderCopier(ILogger<NotesFolderCopier> logger)
{
    /// <summary>源目录里便签文件的扩展名（与 <see cref="LumiNoteStorage"/> 一致）。</summary>
    private const string NoteSearchPattern = "*.lumi";

    /// <summary>数一个目录里有多少张便签（顶层，不递归）。目录不存在时为 0。</summary>
    /// <remarks>界面用它决定要不要问「是否复制现有便签」。</remarks>
    public static int CountNotes(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(LongPath.Ensure(folder)))
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateFiles(
                LongPath.Ensure(folder), NoteSearchPattern, SearchOption.TopDirectoryOnly).Count();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 数不出来就当没有：这只影响「要不要问一句」，不该让设置页报错。
            return 0;
        }
    }

    /// <summary>把 <paramref name="source"/> 里的便签复制到 <paramref name="destination"/>。</summary>
    /// <param name="source">源笔记目录。不存在时什么都不做（返回全零）。</param>
    /// <param name="destination">目标目录。不存在时创建。</param>
    /// <param name="ct">取消标记。</param>
    public Task<CopyResult> CopyAsync(
        string source, string destination, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        // File.Copy 没有异步版本。便签是几十张的量级，放线程池里跑掉即可，
        // 不必为此把整个方法改成异步文件 API 那套。
        return Task.Run(() => Copy(source, destination, ct), ct);
    }

    private CopyResult Copy(string source, string destination, CancellationToken ct)
    {
        if (!Directory.Exists(LongPath.Ensure(source)))
        {
            return new CopyResult(0, 0, 0);
        }

        Directory.CreateDirectory(LongPath.Ensure(destination));

        int copied = 0;
        int skipped = 0;
        int failed = 0;

        foreach (string path in Directory.EnumerateFiles(
                     LongPath.Ensure(source), NoteSearchPattern, SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();

            string target = Path.Combine(destination, Path.GetFileName(path));

            if (File.Exists(LongPath.Ensure(target)))
            {
                skipped++;
                continue;
            }

            try
            {
                File.Copy(LongPath.Ensure(path), LongPath.Ensure(target));
                copied++;
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                failed++;
                logger.LogWarning(
                    "复制便签失败：{Source}（{ExceptionType}）。",
                    path,
                    exception.GetType().Name);
            }
        }

        logger.LogInformation(
            "便签复制完成：成功 {Copied}，跳过 {Skipped}，失败 {Failed}。",
            copied,
            skipped,
            failed);

        return new CopyResult(copied, skipped, failed);
    }
}

/// <summary>一次便签复制的清点：成功、同名跳过、失败。</summary>
public readonly record struct CopyResult(int Copied, int Skipped, int Failed);
