namespace LumiMemo.Infrastructure.Storage;

/// <summary>
/// 文件系统操作的短暂失败重试（§11.2、§5.10）。
/// </summary>
/// <remarks>
/// <para>
/// 只为<strong>短暂</strong>失败而存在：OneDrive / Dropbox 正在同步、杀毒软件正在扫描、
/// 别的编辑器刚把文件打开——这些都会让 <c>File.Replace</c> 或读取抛
/// <see cref="IOException"/>，而等待几百毫秒后通常就能成功。
/// </para>
/// <para>
/// 重试到最后一次仍失败时原样抛出，由调用方决定怎么办：写盘路径会删掉临时文件再抛（§11.2），
/// 读取路径会保留上一份内容并标记为「暂时锁定」（§5.10）。
/// </para>
/// <para>
/// 操作委托是<strong>同步</strong>的，重试间隔用 <see cref="Task.Delay(int)"/> 让出线程。
/// 便签文件都很小（单个文件最大 1MB），同步读写省掉一层异步状态机，
/// 也让「重试」这个逻辑不必区分同步异步两种形态。
/// </para>
/// </remarks>
internal static class StorageRetry
{
    /// <summary>§11.2 与 §5.10 规定的重试间隔，两个场景共用同一套节奏。</summary>
    public static readonly int[] DefaultDelaysMilliseconds = [100, 300, 600];

    /// <summary>
    /// 执行 <paramref name="operation"/>，遇到短暂 IO 故障时按
    /// <paramref name="delaysMilliseconds"/> 逐次等待后重试。
    /// </summary>
    /// <remarks>
    /// 重试次数等于间隔个数——传三个间隔就是「最多四次尝试」，最后一次失败不再等待。
    /// 等待期间被取消会抛 <see cref="OperationCanceledException"/>，这是正确行为：
    /// 关机或退出程序时不该还在为一次保存苦等。
    /// </remarks>
    public static async Task<T> RunAsync<T>(
        Func<T> operation,
        IReadOnlyList<int> delaysMilliseconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(delaysMilliseconds);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return operation();
            }
            catch (IOException) when (attempt < delaysMilliseconds.Count)
            {
                await DelayAsync(delaysMilliseconds[attempt], cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < delaysMilliseconds.Count)
            {
                await DelayAsync(delaysMilliseconds[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>无返回值的 <see cref="RunAsync{T}"/>。文件换名这类操作只需要「成功或抛异常」。</summary>
    public static Task RunAsync(
        Action operation,
        IReadOnlyList<int> delaysMilliseconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return RunAsync(
            () =>
            {
                operation();
                return true;
            },
            delaysMilliseconds,
            cancellationToken);
    }

    private static Task DelayAsync(int milliseconds, CancellationToken cancellationToken) =>
        milliseconds <= 0
            ? Task.CompletedTask
            : Task.Delay(milliseconds, cancellationToken);
}
