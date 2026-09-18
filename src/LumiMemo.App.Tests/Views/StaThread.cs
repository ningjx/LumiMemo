using System.Threading;

namespace LumiMemo.App.Tests.Views;

/// <summary>
/// 在 STA 线程上跑一段代码，并把异常原样带回来。
/// </summary>
/// <remarks>
/// <para>
/// <strong>为什么必须另起线程</strong>：WPF 的 <c>Window</c> 只能在 STA 线程上构造，
/// 而 xunit.v3 的测试线程是 MTA——直接在测试方法里 <c>new Window()</c> 会抛
/// <c>InvalidOperationException</c>。这不是「最好不要」，是硬性要求。
/// </para>
/// <para>
/// <strong>只构造、不显示</strong>：本辅助方法不跑消息泵。<c>new Window()</c> 会加载
/// BAML 并搭出整棵元素树，这一步不需要消息泵；需要消息泵的是 <c>Show()</c> 之后的布局与渲染。
/// 因此它能验证的范围是「XAML 结构与属性是否合法」（这里出问题会直接抛
/// <c>XamlParseException</c>，表现为程序一开窗就崩），
/// <strong>不覆盖</strong>绑定表达式求值是否正确——绑定写错只会静默输出到调试通道，
/// 那是肉眼验收的事。
/// </para>
/// <para>
/// 线程设为后台线程：万一某个测试里构造窗口时卡住，进程退出不该被它拖住。
/// </para>
/// </remarks>
internal static class StaThread
{
    /// <returns>代码抛出的异常；一切正常时返回 <see langword="null"/>。</returns>
    public static Exception? Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "STA-XAML-探针",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return failure;
    }
}
