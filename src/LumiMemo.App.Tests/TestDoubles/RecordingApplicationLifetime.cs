using LumiMemo.App.Abstractions;

namespace LumiMemo.App.Tests.TestDoubles;

/// <summary>
/// <see cref="IApplicationLifetime"/> 的记录型替身。
/// </summary>
/// <remarks>
/// 托盘菜单的「退出」必须验到，而它的生产实现会真的把测试进程关掉
/// （<c>Application.Current?.Shutdown()</c>）——这个替身正是为了在那儿拦一道。
/// </remarks>
public sealed class RecordingApplicationLifetime : IApplicationLifetime
{
    /// <summary>被请求退出的次数。<c>0</c> 就说明那个菜单项没接上。</summary>
    public int ShutdownCount { get; private set; }

    /// <inheritdoc />
    public void RequestShutdown() => ShutdownCount++;
}
