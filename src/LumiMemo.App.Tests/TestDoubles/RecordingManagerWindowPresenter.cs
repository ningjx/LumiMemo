using LumiMemo.App.Abstractions;

namespace LumiMemo.App.Tests.TestDoubles;

/// <summary>
/// <see cref="IManagerWindowPresenter"/> 的记录型替身。
/// </summary>
/// <remarks>
/// 它保证用例跑起来不会真的去 <c>Show()</c> 一个窗口——那需要 STA 线程与消息泵，
/// 而这里要验的只是「托盘菜单有没有把这件事交给它」。
/// </remarks>
public sealed class RecordingManagerWindowPresenter : IManagerWindowPresenter
{
    /// <summary>被请求「弄到前面」的次数。</summary>
    public int BringToFrontCount { get; private set; }

    /// <inheritdoc />
    public void BringToFront() => BringToFrontCount++;
}
