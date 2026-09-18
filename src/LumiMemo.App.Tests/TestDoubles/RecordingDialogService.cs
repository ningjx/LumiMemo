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

    /// <summary>所有的错误提示请求，格式为 <c>标题|正文</c>。</summary>
    public List<string> ErrorRequests { get; } = [];

    /// <summary>所有的信息提示请求，格式为 <c>标题|正文</c>。</summary>
    public List<string> InfoRequests { get; } = [];

    /// <summary>用户对确认框的回答。默认「是」。</summary>
    public bool ConfirmResult { get; set; } = true;

    /// <summary>需要按内容决定回答时设置它，优先级高于 <see cref="ConfirmResult"/>。</summary>
    public Func<string, string, bool>? ConfirmHandler { get; set; }

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText)
    {
        ConfirmRequests.Add($"{title}|{message}");

        return Task.FromResult(ConfirmHandler?.Invoke(title, message) ?? ConfirmResult);
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
