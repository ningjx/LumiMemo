using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Events;

namespace LumiMemo.App.Tests.TestDoubles;

/// <summary>
/// 手摇的 <see cref="IFileWatcher"/>：由用例自己决定什么时候、报什么变化。
/// </summary>
/// <remarks>
/// <para>
/// 「真的 <c>FileSystemWatcher</c> 会不会报这个事件」是集成测试的事（§21.3）——
/// 那要真的落文件、还要轮询等待，没法在单元测试里确定性地验。
/// 这里要验的是<strong>报告之后的处理</strong>：去抖、去重、自写抑制、溢出恢复。
/// 那些逻辑只在「事件什么时候到」可控的前提下才验得动。
/// </para>
/// <para>
/// <see cref="Raise"/> 在调用它的那个线程上同步回调，与真实监听器一样
/// 不是 UI 线程（真实现是线程池线程，§3.4 规则 T3）——被测代码若忘了封送，
/// 用本替身的用例照样能抓到。
/// </para>
/// </remarks>
public sealed class FakeFileWatcher : IFileWatcher
{
    /// <inheritdoc />
    public event EventHandler<FileWatchChange>? FileChanged;

    /// <summary><see cref="Start"/> 被调了几次（含溢出之后的重新启动）。</summary>
    public int StartCount { get; private set; }

    /// <summary><see cref="Stop"/> 被调了几次。</summary>
    public int StopCount { get; private set; }

    /// <summary>是否已释放。</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>手动报一次变化。没有人订阅时什么都不做。</summary>
    public void Raise(FileWatchChangeKind kind, string path, string? oldPath = null) =>
        FileChanged?.Invoke(this, new FileWatchChange(kind, path, oldPath));

    /// <inheritdoc />
    public void Start() => StartCount++;

    /// <inheritdoc />
    public void Stop() => StopCount++;

    /// <inheritdoc />
    public void Dispose() => IsDisposed = true;
}
