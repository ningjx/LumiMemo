using LumiMemo.App.Services;
using LumiMemo.App.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LumiMemo.App.Tests.Services;

/// <summary>
/// <see cref="SingleInstanceGuard"/> 的认领与通知（§17.2）。
/// </summary>
/// <remarks>
/// <para>
/// 这些用例碰的是真实的 Windows 内核对象（命名互斥体与命名管道），不是替身——
/// 而「第二个实例拿不到锁」这件事本身就是内核对象的行为，替身验不了它。
/// </para>
/// <para>
/// <strong>每个用例用一套全新的名字</strong>（<c>LumiMemo.Tests.{guid}</c>）。
/// 名字写死的话，用例之间会通过内核里那个跨进程的对象互相干扰，
/// 而 MTP 默认是并行跑测试类的。
/// </para>
/// <para>
/// <strong>不覆盖 <see cref="SingleInstanceChannel"/> 的 <c>MutexName</c> / <c>PipeName</c></strong>：
/// 那两个值取决于跑测试这台机器上的用户，断言它们等于把用例钉在某台机器上。
/// 该验的是「同一台机器上两次调用给出同一个值」，而这个性质由它们都是静态只读属性保证。
/// </para>
/// <para>
/// <strong>「上一个实例崩溃了」没有单独的用例</strong>，因为它在机制上与正常退出是同一件事：
/// 命名对象的存亡只看句柄数，而崩溃时操作系统同样会关掉那些句柄。
/// <see cref="第一个实例放掉锁之后_所有权可以让出去"/> 验的就是这条路径。
/// </para>
/// </remarks>
public sealed class SingleInstanceGuardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void 第一个实例_能认领到锁()
    {
        (string mutex, string pipe) = NewNames();
        using var guard = new SingleInstanceGuard(mutex, pipe, NullLogger.Instance);

        Assert.True(guard.IsFirstInstance);
    }

    [Fact]
    public void 第二个实例_认领不到锁()
    {
        (string mutex, string pipe) = NewNames();
        using var first = new SingleInstanceGuard(mutex, pipe, NullLogger.Instance);
        using var second = new SingleInstanceGuard(mutex, pipe, NullLogger.Instance);

        // 第二个实例该走的那条路是「发信号然后自己退出」，而这里要先保证它真的知道自己不是第一个。
        Assert.True(first.IsFirstInstance);
        Assert.False(second.IsFirstInstance);
    }

    [Fact]
    public void 不是第一个实例时_开始监听会抛()
    {
        (string mutex, string pipe) = NewNames();
        using var first = new SingleInstanceGuard(mutex, pipe, NullLogger.Instance);
        using var second = new SingleInstanceGuard(mutex, pipe, NullLogger.Instance);

        // 监听是第一个实例的义务。让它悄悄跑起来只会多一个谁也不知道存在的监听者。
        Assert.Null(Record.Exception(() => first.StartListening()));
        Assert.Throws<InvalidOperationException>(second.StartListening);
    }

    [Fact]
    public async Task 第二个实例发的信号_第一个实例收得到()
    {
        (string mutex, string pipe) = NewNames();
        var logger = new RecordingLogger<SingleInstanceGuard>();
        using var first = new SingleInstanceGuard(mutex, pipe, logger);

        var signalled = new TaskCompletionSource();
        first.SecondInstanceSignalled += (_, _) => signalled.TrySetResult();
        first.StartListening();

        // 管道服务端要一点时间才建起来，连不上时 Connect 会一直等到超时，
        // 所以这里给足预算——测的是「送得到」，不是「建得多快」。
        Assert.True(
            SingleInstanceChannel.TrySignal(pipe, TimeSpan.FromSeconds(5)),
            "第一个实例已经在听了，信号不该送不到。");

        Task finished = await Task.WhenAny(signalled.Task, Task.Delay(TimeSpan.FromSeconds(5), Ct));

        Assert.True(
            ReferenceEquals(signalled.Task, finished),
            $"信号没送到监听回调。监听循环的日志：{string.Join(" | ", logger.Messages)}");
    }

    [Fact]
    public void 没有人在听的时候_发信号等够了就返回假()
    {
        (_, string pipe) = NewNames();

        // 名字是全新的，管道不存在。它必须等满预算然后返回 false，
        // 而不是抛异常、更不是无限等下去——卡住比失败糟得多。
        Assert.False(SingleInstanceChannel.TrySignal(pipe, TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void 第一个实例放掉锁之后_所有权可以让出去()
    {
        (string mutex, string pipe) = NewNames();

        var first = new SingleInstanceGuard(mutex, pipe, NullLogger.Instance);
        Assert.True(first.IsFirstInstance);
        first.Dispose();

        // 释放之后要能立刻被下一个认领：否则用户关掉程序再打开，会被自己刚才那次运行挡在门外。
        using var second = new SingleInstanceGuard(mutex, pipe, NullLogger.Instance);
        Assert.True(second.IsFirstInstance);
    }

    [Fact]
    public void 释放可以在别的线程上发生()
    {
        (string mutex, string pipe) = NewNames();
        SingleInstanceGuard guard = new(mutex, pipe, NullLogger.Instance);

        // 这正是本类最初栽的那一跤：async 用例里 using 的 Dispose 落在 await 之后的线程上，
        // 而互斥体的所有权属于线程，ReleaseMutex 于是抛 ApplicationException。
        // 现在认的是句柄而不是所有权，所以「在哪个线程释放」不再是个问题。
        Exception? failure = null;
        Thread thread = new(() => failure = Record.Exception(guard.Dispose));
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    [Fact]
    public void 重复释放_不抛()
    {
        (string mutex, string pipe) = NewNames();

        SingleInstanceGuard guard = new(mutex, pipe, NullLogger.Instance);

        // 组合根里 OnExit 走一次、容器释放时可能再走一次，
        // 第二次撞上 ReleaseMutex 抛出来的话，用户的退出流程会以一个未处理异常收场。
        guard.Dispose();
        Assert.Null(Record.Exception(guard.Dispose));
    }

    /// <summary>一套这个用例独占的内核对象名字。</summary>
    private static (string MutexName, string PipeName) NewNames()
    {
        string key = "LumiMemo.Tests." + Guid.NewGuid().ToString("N");

        // 互斥体在会话命名空间里（与生产一致），管道名不能带反斜杠。
        return (@"Local\" + key, key);
    }
}
