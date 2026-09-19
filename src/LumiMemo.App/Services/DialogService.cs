using System.Windows;
using LumiMemo.App.Abstractions;
using LumiMemo.App.Views;

namespace LumiMemo.App.Services;

/// <summary>
/// <see cref="IDialogService"/> 的生产实现。
/// </summary>
/// <remarks>
/// <para>
/// 本类存在的意义就是把 <c>MessageBox</c> 挡在 ViewModel 之外（§18.6）：
/// 它阻塞 UI 线程、在没有消息循环的测试进程里根本弹不出来，也没法统一视觉风格。
/// 挡在这一层之后，ViewModel 那边只看得见一个返回 <see cref="Task"/> 的接口。
/// </para>
/// <para>
/// <strong>只有两个纯提示还留在 <see cref="MessageBox"/> 上</strong>：它们只有一个「好」按钮，
/// 没有文案可定制，换成自绘对话框只是白写一遍。其余两类都要管按钮文字，走自绘的
/// <see cref="ChoiceDialog"/>：<see cref="ChooseAsync"/> 要 §7.3 恢复冲突的
/// 「重命名 / 恢复到笔记目录根 / 取消」三选一，<c>MessageBox</c> 放不下第三档；
/// <see cref="ConfirmAsync"/> 要 §7.4 的「永久删除」——<c>MessageBox</c> 的按钮文案
/// 由系统提供，换成它这条要求就落空了。
/// </para>
/// <para>
/// 两类自绘对话框的默认选项都落在「取消」那一侧：<see cref="ChooseAsync"/> 的调用方
/// 负责传 <c>defaultIndex</c>，而 <see cref="ConfirmAsync"/> 固定把默认放在第二项。
/// 敲回车时不该替用户按下那唯一不可逆的那个键。
/// </para>
/// </remarks>
public sealed class DialogService : IDialogService
{
    /// <inheritdoc />
    public Task<int> ChooseAsync(
        string title,
        string message,
        IReadOnlyList<string> choices,
        int defaultIndex = 0)
    {
        var dialog = new ChoiceDialog(title, message, choices, defaultIndex);

        return Task.FromResult(dialog.Ask(ActiveWindow()));
    }

    /// <summary>当前该被挡在后面的那个窗口。</summary>
    /// <remarks>
    /// 优先取被激活的那个。一个都没有时退到 <see cref="Application.MainWindow"/>，
    /// 它可能还没 <c>Show()</c> 过（启动早期的提示框就是这种情况），那种时候
    /// 交给 <see cref="ChoiceDialog"/> 改用屏幕居中，而不是硬塞一个不可见的宿主。
    /// </remarks>
    private static Window? ActiveWindow()
    {
        Application? app = Application.Current;

        if (app is null)
        {
            return null;
        }

        foreach (Window window in app.Windows)
        {
            if (window.IsActive)
            {
                return window;
            }
        }

        return app.MainWindow is { IsLoaded: true } main ? main : null;
    }

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText)
    {
        // 也走自绘对话框，为的是那两个按钮上的文字。§7.4 的第二次确认要求按钮写着
        // 「永久删除」而不是「确定」——那是全程序唯一不可逆的一步，用户不该有看错的余地。
        // 换成 MessageBox 之后按钮会变成系统的「是 / 否」，这条要求就落空了。
        var dialog = new ChoiceDialog(title, message, [confirmText, cancelText], defaultIndex: 1);

        return Task.FromResult(dialog.Ask(ActiveWindow()) == 0);
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
