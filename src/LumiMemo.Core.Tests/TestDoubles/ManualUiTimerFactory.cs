using LumiMemo.Core.Abstractions;

namespace LumiMemo.Core.Tests.TestDoubles;

/// <summary>
/// 造 <see cref="ManualUiTimer"/> 的工厂替身，并把造出来的每一个都记下来（§21.5）。
/// </summary>
/// <remarks>
/// 记下来是为了让用例能拿到被测服务内部那个定时器：它是私有字段，没有别的途径够得着，
/// 而「等到期」这件事必须由用例来推动。
/// </remarks>
public sealed class ManualUiTimerFactory : IUiTimerFactory
{
    private readonly List<ManualUiTimer> _timers = [];

    /// <summary>本工厂造出来的全部定时器，按创建顺序。</summary>
    public IReadOnlyList<ManualUiTimer> Created => _timers;

    /// <summary>最后造出来的那个。被测服务只建一个定时器时用它就够了。</summary>
    /// <exception cref="InvalidOperationException">还没造过任何定时器。</exception>
    public ManualUiTimer Last =>
        _timers.Count > 0
            ? _timers[^1]
            : throw new InvalidOperationException("还没有创建过定时器，被测服务的构造函数多半没走到。");

    public IUiTimer Create()
    {
        var timer = new ManualUiTimer();
        _timers.Add(timer);

        return timer;
    }
}
