using System.IO;
using System.IO.Pipes;
using System.Security.Principal;

namespace LumiMemo.App.Services;

/// <summary>
/// 单实例要用的那两个名字，以及「通知已有实例」这条通道（§17.2）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>名字带上当前用户的 SID</strong>：同一台机器上多个用户同时登录时，
/// 各自的实例不该互相拦截（Windows 的 <c>Local\</c> 命名空间本身也是按会话分的，
/// 但两个人可以是同一个会话里的不同用户，SID 才是真正的身份）。
/// </para>
/// <para>
/// <strong>互斥体用 <c>Local\</c>、管道不带前缀</strong>：管道名里<strong>不能有反斜杠</strong>，
/// 那是路径分隔符。两者从同一个用户键派生，但形态不同不是笔误。
/// </para>
/// </remarks>
public static class SingleInstanceChannel
{
    /// <summary>
    /// 握手用的那个字节。写什么值不重要，写同一个值回来是为了「回音确实来自我们那一段代码」。
    /// </summary>
    public const byte SignalByte = 0x5A;

    private const int RetryDelayMilliseconds = 50;

    private static readonly string UserKey = ResolveUserKey();

    /// <summary>互斥体的全名。</summary>
    public static string MutexName { get; } = @"Local\LumiMemo.SingleInstance." + UserKey;

    /// <summary>命名管道的名字。</summary>
    public static string PipeName { get; } = "LumiMemo.SingleInstance." + UserKey;

    /// <summary>
    /// 在 <paramref name="timeout"/> 之内尽力把这个信号送到正在监听的那个实例。
    /// </summary>
    /// <returns>送达到了返回 <c>true</c>；超时或没人听返回 <c>false</c>。</returns>
    /// <remarks>
    /// <para>
    /// <strong>它必须先尽力，再放弃。</strong> 调用方是第二个实例，它接下来要么
    /// 把第一个实例唤到前台、要么干脆自己退出——两种处理都成立，所以这里返回布尔值
    /// 而不是抛异常。真正不可接受的只有一种：<strong>卡住</strong>，因此整段交换
    /// 都卡在 <paramref name="timeout"/> 这个预算之内，包括等回音那一步。
    /// </para>
    /// <para>
    /// <strong>为什么要等回音，而不是「连上就算数」。</strong>
    /// 本类最初的实现就是连上即返回，测试里大概每三次红一次。
    /// 原因是客户端连上之后<strong>立刻关掉句柄</strong>时，服务端挂着的
    /// <c>WaitForConnectionAsync</c> 会以 <c>IOException: 管道正在被关闭</c> 收场——
    /// 那一次连接本该算数，却变成了一次异常。让客户端写完等一个回音，
    /// 它就会一直握着句柄直到服务端确认收到，那个窗口自然消失。
    /// </para>
    /// <para>
    /// <strong>为什么要重试</strong>：监听方是「收一个连接、扔掉那个管道实例、再建一个新的」，
    /// 换实例的那几毫秒里连接会撞上 <c>ERROR_PIPE_BUSY</c>。这个窗口很窄但确实存在，
    /// 且刚好落在「用户连点两下程序图标」这种最容易被注意到的时机上。
    /// </para>
    /// </remarks>
    public static bool TrySignal(string pipeName, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);

        // 调用方都在同步的启动路径上（OnStartup 容不下 await），
        // 所以这里同步等一个异步的握手。它是有上限的，不会把启动卡住。
        return ExchangeAsync(pipeName, timeout).GetAwaiter().GetResult();
    }

    private static async Task<bool> ExchangeAsync(string pipeName, TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;

            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            try
            {
                using var client = new NamedPipeClientStream(
                    ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

                // 管道还没建起来时 Connect 会一直等到超时，所以预算直接给它。
                client.Connect((int)remaining.TotalMilliseconds);

                byte[] buffer = [SignalByte];

                await client.WriteAsync(buffer).ConfigureAwait(false);

                using var readCts = new CancellationTokenSource(Remaining(deadline));
                int read = await client.ReadAsync(buffer, readCts.Token).ConfigureAwait(false);

                if (read == sizeof(byte) && buffer[0] == SignalByte)
                {
                    return true;
                }

                // 回音不对：对端那个管道实例已经坏了。退一步，换一个新的再试。
                Thread.Sleep(RetryDelayMilliseconds);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // 没人听，或者听到了却不回话——再等下去也不会变好。
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // 这个名字被别人占了。
                return false;
            }
            catch (IOException)
            {
                // ERROR_PIPE_BUSY：监听方正在换一个新的管道实例。
                Thread.Sleep(RetryDelayMilliseconds);
            }
        }
    }

    /// <summary>到 <paramref name="deadline"/> 还剩多久；已经过了就返回一个极短的值。</summary>
    /// <remarks>
    /// 不能返回零或负数：<see cref="CancellationTokenSource"/> 的构造函数把非正数
    /// 当作「立刻取消」，那样这一轮会连回音都来不及等就直接放弃。
    /// </remarks>
    private static TimeSpan Remaining(DateTimeOffset deadline)
    {
        TimeSpan left = deadline - DateTimeOffset.UtcNow;

        return left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1);
    }

    /// <summary>
    /// 当前用户的稳定标识：优先 SID，取不到才退回用户名。
    /// </summary>
    /// <remarks>
    /// 取 SID 要开线程令牌，理论上会因权限不足失败。退回用户名不是等价替代——
    /// 两个域用户可能同名——但那种情况下「多开了一个实例」比「程序起不来」轻得多。
    /// </remarks>
    private static string ResolveUserKey()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();

            if (identity.User?.Value is { Length: > 0 } sid)
            {
                return sid;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }

        return Environment.UserName;
    }
}
