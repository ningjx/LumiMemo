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

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText)
    {
        ConfirmRequests.Add($"{title}|{message}");
        ConfirmTexts.Add((confirmText, cancelText, message));

        return Task.FromResult(ConfirmHandler?.Invoke(title, message) ?? ConfirmResult);
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

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ShowInfoAsync(string title, string message)
    {
        InfoRequests.Add($"{title}|{message}");

        return Task.CompletedTask;
    }
}
