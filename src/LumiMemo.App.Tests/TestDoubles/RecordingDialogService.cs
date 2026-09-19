using LumiMemo.App.Abstractions;

namespace LumiMemo.App.Tests.TestDoubles;

/// <summary>
/// <see cref="IDialogService"/> 的记录型替身（§21.5 的替身表）。
/// </summary>
/// <remarks>
/// 它做两件事：记录「问过什么」，以及让测试决定「用户答了什么」。
/// 有了它，ViewModel 里「保存失败 → 提示用户」这类分支才能在不弹窗的情况下断言。
/// </remarks>
public sealed class RecordingDialogService : IDialogService
{
    /// <summary>所有的确认请求，格式为 <c>标题|正文</c>。</summary>
    public List<string> ConfirmRequests { get; } = [];

    /// <summary>确认框上那两个按钮的文字，按发生顺序。</summary>
    /// <remarks>
    /// 单独记一份是因为 §7.4 对按钮文字本身有要求（第二次必须是「永久删除」而不是「确定」），
    /// 而 <see cref="ConfirmRequests"/> 只记了标题与正文，验不到那一条。
    /// </remarks>
    public List<(string ConfirmText, string CancelText, string Message)> ConfirmTexts { get; } = [];

    /// <summary>所有的错误提示请求，格式为 <c>标题|正文</c>。</summary>
    public List<string> ErrorRequests { get; } = [];

    /// <summary>所有的信息提示请求，格式为 <c>标题|正文</c>。</summary>
    public List<string> InfoRequests { get; } = [];

    /// <summary>
    /// 需要自己决定那次错误提示怎么结束时设置它（抛异常、或者一直不结束），
    /// 优先级高于默认的「记一笔就返回」。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ConfirmHandler"/> 那几个同一形状。用得着它的只有
    /// <c>ErrorReporterTests</c>：「提示框自己炸了」和「上一个框还开着」这两种情形
    /// 都要求那次 <c>ShowErrorAsync</c> 的行为不是默认的那一种。
    /// </remarks>
    public Func<string, string, Task>? ErrorHandler { get; set; }

    /// <summary>用户对确认框的回答。默认「是」。</summary>
    public bool ConfirmResult { get; set; } = true;

    /// <summary>需要按内容决定回答时设置它，优先级高于 <see cref="ConfirmResult"/>。</summary>
    public Func<string, string, bool>? ConfirmHandler { get; set; }

    /// <summary>所有的多选一请求，格式为 <c>标题|正文|选项1,选项2,…</c>。</summary>
    public List<string> ChooseRequests { get; } = [];

    /// <summary>用户在多选一里选了第几项。<c>-1</c> 表示关掉了对话框。默认选第一项。</summary>
    public int ChooseResult { get; set; }

    /// <summary>需要按内容决定选第几项时设置它，优先级高于 <see cref="ChooseResult"/>。</summary>
    public Func<string, string, IReadOnlyList<string>, int>? ChooseHandler { get; set; }

    /// <summary>所有的输入框请求，格式为 <c>标题|正文|初值</c>。</summary>
    public List<string> PromptRequests { get; } = [];

    /// <summary>用户对输入框的回答。<c>null</c> 表示取消。</summary>
    /// <remarks>
    /// 默认是<b>取消</b>而不是「原样返回初值」：没有哪个答案算得上"默认的那个"，
    /// 而漏设时走取消那一路，至少不会让一个忘了配的用例悄悄改掉数据还万事大吉。
    /// </remarks>
    public string? PromptResult { get; set; }

    /// <summary>需要按初值决定输入什么时设置它，优先级高于 <see cref="PromptResult"/>。</summary>
    public Func<string, string, string, string?>? PromptHandler { get; set; }

    /// <summary>
    /// 调用方传进来的校验回调跑出来的结论，按发生顺序。<c>null</c> 表示那一次通过了。
    /// </summary>
    /// <remarks>
    /// 替身不会真的拦住什么，所以它把校验的结论记下来——生产实现里那个回调
    /// 「不通过就不关窗、把错误写在输入框下面」，而标签长度上限（§5.8）那条规则
    /// 只活在回调里，不去跑一遍就没地方验它。
    /// </remarks>
    public List<string?> PromptValidations { get; } = [];

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText)
    {
        ConfirmRequests.Add($"{title}|{message}");
        ConfirmTexts.Add((confirmText, cancelText, message));

        return Task.FromResult(ConfirmHandler?.Invoke(title, message) ?? ConfirmResult);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 顺序按生产实现来：先拿到用户打的字，再校验它，没过就<b>什么都不交回去</b>
    /// （真对话框那时还开着）。所以「校验没过」与「用户取消」在调用方那里是同一个结果——
    /// 真实流程里也确实是：用户看到红字之后要么改，要么关掉。
    /// </remarks>
    public Task<string?> PromptAsync(
        string title,
        string message,
        string initialValue,
        Func<string, string?>? validate = null)
    {
        PromptRequests.Add($"{title}|{message}|{initialValue}");

        string? answer = PromptHandler?.Invoke(title, message, initialValue) ?? PromptResult;

        if (answer is null)
        {
            // 取消：对话框根本没提交，回调不该跑。
            return Task.FromResult<string?>(null);
        }

        string? error = validate?.Invoke(answer);

        PromptValidations.Add(error);

        return Task.FromResult(error is null ? answer : null);
    }

    /// <inheritdoc />
    public Task<int> ChooseAsync(
        string title,
        string message,
        IReadOnlyList<string> choices,
        int defaultIndex = 0)
    {
        ChooseRequests.Add($"{title}|{message}|{string.Join(',', choices)}");

        return Task.FromResult(ChooseHandler?.Invoke(title, message, choices) ?? ChooseResult);
    }

    /// <inheritdoc />
    public Task ShowErrorAsync(string title, string message)
    {
        ErrorRequests.Add($"{title}|{message}");

        return ErrorHandler?.Invoke(title, message) ?? Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ShowInfoAsync(string title, string message)
    {
        InfoRequests.Add($"{title}|{message}");

        return Task.CompletedTask;
    }
}
