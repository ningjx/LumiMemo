using LumiMemo.Core.Events;

namespace LumiMemo.Core.Abstractions;

/// <summary>
/// 笔记目录的文件变化来源（§10）。
/// </summary>
/// <remarks>
/// <para>
/// 这是<strong>原始事件源</strong>，只负责把 Windows 的 <c>FileSystemWatcher</c> 包装成
/// 平台无关的事件。按路径去抖（§10.2）、防自触发抑制（§10.3）、以及封送到 UI 线程（§3.4 T3）
/// 都在 App 层的 <c>FileWatchService</c> 里做。
/// </para>
/// <para>
/// 实现必须显式设置 64KB 缓冲区并订阅错误事件（§10.1）。
/// </para>
/// </remarks>
public interface IFileWatcher : IDisposable
{
    /// <summary>文件变化。在<strong>线程池线程</strong>上触发，消费方自行封送（§3.4 T3）。</summary>
    event EventHandler<FileWatchChange>? FileChanged;

    /// <summary>开始监听。重复调用应当无效而不是抛异常。</summary>
    void Start();

    /// <summary>停止监听，但保留实例以便再次 <see cref="Start"/>。</summary>
    void Stop();
}
