using System.Windows;
using LumiMemo.App.Abstractions;

namespace LumiMemo.App.Services;

/// <summary>
/// <see cref="IDialogService"/> 的生产实现，内部是 <see cref="MessageBox"/>。
/// </summary>
/// <remarks>
/// <para>
/// 本类存在的意义就是把 <c>MessageBox</c> 挡在 ViewModel 之外（§18.6）：
/// 它阻塞 UI 线程、在没有消息循环的测试进程里根本弹不出来，也没法统一视觉风格。
/// 挡在这一层之后，ViewModel 那边只看得见一个返回 <see cref="Task"/> 的接口。
/// </para>
/// <para>
/// <strong>已知局限</strong>：<see cref="MessageBox"/> 的按钮文案由系统提供，
/// 无法改成调用方传进来的 <c>confirmText</c> / <c>cancelText</c>。
/// 目前这两个参数只用于（未来的）无障碍说明，实际显示的是系统语言的「是 / 否」。
/// 要做到完全自定义，得换成自绘的模态对话框——那属于设置窗口那一批 UI 工作，
/// 不在本轮跑通链路的范围内。
/// </para>
/// </remarks>
public sealed class DialogService : IDialogService
{
    /// <inheritdoc />
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText)
    {
        MessageBoxResult result = MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        return Task.FromResult(result == MessageBoxResult.Yes);
    }

    /// <inheritdoc />
    public Task ShowErrorAsync(string title, string message)
    {
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ShowInfoAsync(string title, string message)
    {
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

        return Task.CompletedTask;
    }
}
