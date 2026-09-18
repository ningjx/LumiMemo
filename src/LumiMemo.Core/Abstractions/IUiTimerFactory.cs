namespace LumiMemo.Core.Abstractions;

/// <summary>
/// 造 <see cref="IUiTimer"/> 的工厂（§21.5 的替身表）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么给定时器配一个工厂，而不是直接把 <see cref="IUiTimer"/> 注入进去：
/// 定时器带状态（当前回调、剩余时间），而 <c>DispatcherTimer</c> 还绑死在创建它的
/// 那个线程上（§3.4 规则 T4 的同一个理由）。工厂本身无状态，可以随便注入；
/// 真正要用定时器的服务在自己的构造里 <see cref="Create"/> 一个，
/// 于是「在哪个线程上创建」这件事跟着服务走，不需要额外的约定。
/// </para>
/// <para>
/// 生产实现是 App 层的 <c>DispatcherTimerFactory</c>（内部就是 <c>DispatcherTimer</c>），
/// 替身是 Core.Tests 的 <c>ManualUiTimerFactory</c>。
/// </para>
/// </remarks>
public interface IUiTimerFactory
{
    /// <summary>创建一个尚未启动的定时器。</summary>
    IUiTimer Create();
}
