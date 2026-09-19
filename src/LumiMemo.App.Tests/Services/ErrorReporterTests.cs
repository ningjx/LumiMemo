using LumiMemo.App.Services;
using LumiMemo.App.Tests.TestDoubles;
using Xunit;

namespace LumiMemo.App.Tests.Services;

/// <summary>
/// <see cref="ErrorReporter"/> 的单元测试：节流的两条规则，以及「它自己绝不能再抛」（§17.5 第 1 层）。
/// </summary>
/// <remarks>
/// <para>
/// 时间全靠 <see cref="FakeClock"/> 拨。真实的十秒与六十秒在测试里既慢又不确定，
/// 而这里要验的恰恰是「到那一刻到底报不报」——拨表是唯一能把这条边界钉死的办法。
/// </para>
/// <para>
/// 用例里抛出来的异常都是<strong>现造的</strong>、从没被 throw 过的对象。
/// 这不影响任何一条断言：记账看的是 <c>GetType()</c>，与有没有堆栈无关。
/// </para>
/// </remarks>
public sealed class ErrorReporterTests
{
    [Fact]
    public void 同一个异常类型十秒内只弹一次()
    {
        var h = Harness.Create();

        // 每一次都是一只新对象，但它们属于同一个类型——记账是按类型的，这正是要害：
        // 一个每帧都失败的绑定每次抛的都是新对象，按对象记账等于没有节流。
        h.Reporter.ReportRecoverable(new InvalidOperationException("第一次"));
        h.Reporter.ReportRecoverable(new InvalidOperationException("第二次"));

        Assert.Single(h.Dialogs.ErrorRequests);
    }

    [Fact]
    public void 过了十秒之后同一个类型能再弹一次()
    {
        var h = Harness.Create();

        h.Reporter.ReportRecoverable(new InvalidOperationException("第一次"));

        h.Clock.Advance(TimeSpan.FromSeconds(10));

        h.Reporter.ReportRecoverable(new InvalidOperationException("第二次"));

        Assert.Equal(2, h.Dialogs.ErrorRequests.Count);
    }

    [Fact]
    public void 被压掉的那几次不算报告_窗口不会一直往后推()
    {
        // 十秒的窗口是从「上一次真的报过」算起的，被压掉的不刷新那个时刻。
        // 若它刷新，一个每九秒抛一次的坏绑定就永远报不出来——而它恰恰最该报：
        // 症状持续存在，日志里一直在涨，界面上却一声不吭。
        var h = Harness.Create();

        for (int round = 0; round < 3; round++)
        {
            h.Reporter.ReportRecoverable(new InvalidOperationException($"第 {round} 轮"));

            h.Clock.Advance(TimeSpan.FromSeconds(9));
        }

        // t=0 报一次、t=9 压掉、t=18 又报一次。会把窗口往后推的实现只给出 1 次。
        Assert.Equal(2, h.Dialogs.ErrorRequests.Count);
    }

    [Fact]
    public void 一轮之内报满五次之后就不再弹()
    {
        var h = Harness.Create();

        // 每次隔十秒：正好够得上报告窗口，又远不到六十秒的翻篇线，于是都落在同一轮里。
        for (int round = 0; round < 10; round++)
        {
            h.Reporter.ReportRecoverable(new InvalidOperationException($"第 {round} 轮"));

            h.Clock.Advance(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(5, h.Dialogs.ErrorRequests.Count);
    }

    [Fact]
    public void 长久静默之后重新开一轮()
    {
        var h = Harness.Create();

        for (int round = 0; round < 10; round++)
        {
            h.Reporter.ReportRecoverable(new InvalidOperationException($"第 {round} 轮"));

            h.Clock.Advance(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(5, h.Dialogs.ErrorRequests.Count);

        // 早先那一阵已经把这一轮的额度用光了。几小时之后另一桩真事故若要因为
        // 「整个会话一共只许五次」而永远弹不出来，弹窗机制本身就失去了意义——
        // 静默够久说明前面那一阵已经过去了（见 ErrorReporter 的类说明）。
        h.Clock.Advance(TimeSpan.FromMinutes(10));

        h.Reporter.ReportRecoverable(new InvalidOperationException("后来的另一桩事故"));

        Assert.Equal(6, h.Dialogs.ErrorRequests.Count);
    }

    [Fact]
    public void 不同异常类型各记各的账()
    {
        var h = Harness.Create();

        h.Reporter.ReportRecoverable(new InvalidOperationException("甲"));
        h.Reporter.ReportRecoverable(new ArgumentException("乙"));

        // 两种异常一起出现时，把乙压掉就等于只报了半件事——出错的是甲，
        // 而用户拿到的提示里会完全没有乙的影子。
        Assert.Equal(2, h.Dialogs.ErrorRequests.Count);
    }

    [Fact]
    public void 被压掉的那几次照样写进日志()
    {
        // 节流省掉的是打扰，不是证据。事后翻日志要能看出「这个异常在这一轮里
        // 到底出现了多少次」，否则被压掉的那几百次在记录里等于从未发生。
        var h = Harness.Create();

        h.Reporter.ReportRecoverable(new InvalidOperationException("第一次"));
        h.Reporter.ReportRecoverable(new InvalidOperationException("第二次"));

        Assert.Single(h.Dialogs.ErrorRequests);

        Assert.Equal(
            2,
            h.Log.Messages.Count(
                message => message.Contains("已接住的未处理异常", StringComparison.Ordinal)));
    }

    [Fact]
    public void 提示文案给出异常类型与去日志里看()
    {
        var h = Harness.Create();

        h.Reporter.ReportRecoverable(
            new InvalidOperationException("磁盘上的那个文件被别的程序占着"));

        string error = Assert.Single(h.Dialogs.ErrorRequests);

        Assert.StartsWith("程序遇到了一个问题|", error, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", error, StringComparison.Ordinal);
        Assert.Contains("磁盘上的那个文件被别的程序占着", error, StringComparison.Ordinal);

        // 用户照着这一句能自己找过去。只给目录不给文件名：那个目录里只放本程序的日志，
        // 而文件名带滚动槽号，写死一个反而会指错文件。
        Assert.Contains(@"C:\fake\logs\", error, StringComparison.Ordinal);
    }

    [Fact]
    public void 提示框自己出问题时不再抛出去()
    {
        var h = Harness.Create();

        h.Dialogs.ErrorHandler = (_, _) => throw new InvalidOperationException("提示框自己炸了");

        // 它唯一的调用点是 DispatcherUnhandledException。从这里再抛出去，
        // 就是转着圈回到同一个处理器——用户面对的是停不下来的弹窗风暴，
        // 比原来那个异常更难收拾。
        h.Reporter.ReportRecoverable(new InvalidOperationException("外面的那个"));

        Assert.Contains(
            h.Log.Messages,
            message => message.Contains("错误提示框本身失败", StringComparison.Ordinal));
    }

    [Fact]
    public void 前一个框还开着时不再叠一个上去()
    {
        var h = Harness.Create();
        var gate = new TaskCompletionSource();

        // 模态框自己会泵消息，所以「框还开着的时候又来了一个异常」不是假想。
        // 叠上去的结果是关掉一个还有一个，而用户无从判断哪个才是刚才那件事。
        h.Dialogs.ErrorHandler = (_, _) => gate.Task;

        h.Reporter.ReportRecoverable(new InvalidOperationException("第一个"));

        // 换个类型，好让节流拦不住它——这里要验的正是节流之外的那道闸。
        h.Reporter.ReportRecoverable(new ArgumentException("第二种"));

        Assert.Single(h.Dialogs.ErrorRequests);

        gate.SetResult();
    }

    private sealed record Harness(
        ErrorReporter Reporter,
        FakeClock Clock,
        RecordingDialogService Dialogs,
        RecordingLogger<ErrorReporter> Log)
    {
        public static Harness Create()
        {
            var clock = new FakeClock();
            var dialogs = new RecordingDialogService();
            var log = new RecordingLogger<ErrorReporter>();

            return new Harness(
                new ErrorReporter(new FakeAppPaths(), clock, dialogs, log),
                clock,
                dialogs,
                log);
        }
    }
}
