namespace LumiMemo.App.Abstractions;

/// <summary>
/// 把管理器窗口弄到前面：没显示就显示，最小化了就还原，然后激活它。
/// </summary>
/// <remarks>
/// <para>
/// <strong>它存在的理由与 <see cref="IDialogService"/>、<see cref="IApplicationLifetime"/> 一样：
/// 测试。</strong> 托盘菜单里的「便签列表...」需要 <see cref="System.Windows.Window"/>，
/// 而 WPF 的窗口只能在 STA 线程上构造，xunit.v3 的测试线程是 MTA——
/// 让 <c>TrayViewModel</c> 直接持有窗口，它的每一个用例都得挂到 STA 线程上、
/// 还得造一个真的管理器窗口出来。
/// </para>
/// <para>
/// <strong>名字不叫 <c>...Launcher</c>。</strong> 仓库里的
/// <c>SettingsWindowLauncher</c> 做的是「每次开一个新的窗口，并保证同时只有一个」；
/// 管理器是<strong>自始至终只有的那一个</strong>，这里没有「开」的语义，
/// 只有「弄到前面」。名字一样会让人以为这里也在管创建与生命周期。
/// </para>
/// </remarks>
public interface IManagerWindowPresenter
{
    /// <summary>把管理器窗口弄到前面。</summary>
    /// <remarks>
    /// 已经是前台窗口时也应当可重复调用（只是激活一下），调用方不需要先判断状态——
    /// 那样判断会在每个调用点各写一遍，且 Windows 的前台规则让它很难判断准。
    /// </remarks>
    void BringToFront();
}
