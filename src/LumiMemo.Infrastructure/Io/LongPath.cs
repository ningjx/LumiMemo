namespace LumiMemo.Infrastructure.Io;

/// <summary>
/// 超长路径（&gt; 260 字符）的 <c>\\?\</c> 前缀处理（§5.10）。
/// </summary>
/// <remarks>
/// <para>
/// 笔记目录是用户自己的目录，可能被同步工具或整理癖好做成很深的层级。
/// 不加前缀时 Win32 会在 260 字符处拒绝，表现为「文件明明在却打不开」。
/// </para>
/// <para>
/// 刻意不提供「路径超长就提前报错」的入口：加了前缀之后这类路径本来就能正常读写，
/// 再拦一道只会让用户平白丢一个功能。需要的调用方可以自己用 <see cref="IsTooLong"/> 判断。
/// </para>
/// <para>
/// <strong>传进来的路径必须已经规范化</strong>（走一次 <see cref="Path.GetFullPath(string)"/>）：
/// <c>\\?\</c> 前缀会关掉 Win32 自己的规范化，路径里的 <c>.</c>、<c>..</c>、<c>/</c>
/// 不会再被展开或替换，带进去就是原样传给文件系统。
/// </para>
/// </remarks>
public static class LongPath
{
    /// <summary>Win32 的传统路径长度上限（含结尾的 <c>\0</c>，因此可用长度为 259）。</summary>
    public const int MaxPathLength = 260;

    private const string DevicePrefix = @"\\?\";
    private const string UncPrefix = @"\\";
    private const string UncDevicePrefix = @"\\?\UNC";

    /// <summary>路径是否达到 Win32 的传统长度上限，需要走 <c>\\?\</c>。</summary>
    /// <remarks>
    /// 用 <c>&gt;=</c> 而不是 <c>&gt;</c>：上限 260 是<strong>含结尾 <c>\0</c></strong> 算的，
    /// 所以 260 个字符的路径本身就已经越界了。
    /// </remarks>
    public static bool IsTooLong(string path) => path.Length >= MaxPathLength;

    /// <summary>需要时给路径加上 <c>\\?\</c> 前缀，否则原样返回。</summary>
    public static string Ensure(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!IsTooLong(path) || path.StartsWith(DevicePrefix, StringComparison.Ordinal))
        {
            return path;
        }

        // UNC 要写成 \\?\UNC\server\share，不能写成 \\?\\\server\share。
        if (path.StartsWith(UncPrefix, StringComparison.Ordinal))
        {
            return UncDevicePrefix + path[1..];
        }

        // 相对路径加前缀只会更糟（前缀要求完全限定路径），原样返回让调用方去撞正常的失败。
        return Path.IsPathFullyQualified(path) ? DevicePrefix + path : path;
    }
}
