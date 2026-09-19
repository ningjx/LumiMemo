namespace LumiMemo.App.Abstractions;

/// <summary>
/// 把「弹一个模态对话框」抽象出来，使 ViewModel 不依赖 <c>System.Windows.MessageBox</c>（§18.6）。
/// </summary>
/// <remarks>
/// <para>
/// §18.6 禁止 ViewModel 直接调用 <c>MessageBox.Show</c>：
/// 它阻塞 UI 线程、无法在无界面进程里测试、也没有办法统一视觉风格。
/// </para>
/// <para>
/// 这里的方法都返回 <see cref="Task"/>，因为便签窗口的内容需要先落盘再去问用户
/// （§11.5 的保存失败提示、§11.4 的冲突对话框），
/// 而落盘是异步的（§3.4 规则 T6）。
/// </para>
/// <para>
/// 所有文案由调用方传入，且必须取自 <c>Resources/Strings.resx</c>（§24.2）。
/// 本服务不认识具体文案，只负责呈现。
/// </para>
/// </remarks>
public interface IDialogService
{
    /// <summary>
    /// 询问用户「是 / 否」。
    /// </summary>
    /// <returns>用户选择「是」返回 <c>true</c>。</returns>
    /// <remarks>
    /// 用在 §5.7 删除文件夹、§7.2 清空回收站这类不可逆操作之前。
    /// </remarks>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText);

    /// <summary>
    /// 让用户在若干选项里挑一个。
    /// </summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="message">说明正文。</param>
    /// <param name="choices">选项文案，至少两项。</param>
    /// <param name="defaultIndex">默认选中的那一项。</param>
    /// <returns>选中项的下标；用户直接关掉对话框时返回 <c>-1</c>。</returns>
    /// <remarks>
    /// 为 §7.3 的恢复冲突而生：那里要的正是<strong>三选一</strong>
    /// （重命名 / 恢复到笔记目录根 / 取消），而「是 / 否」表达不了。
    /// 返回 <c>-1</c> 而不是抛异常：关掉对话框与点「取消」对调用方是同一件事，
    /// 让它们走同一条分支比逼每个调用点各判一次要好。
    /// </remarks>
    Task<int> ChooseAsync(
        string title,
        string message,
        IReadOnlyList<string> choices,
        int defaultIndex = 0);

    /// <summary>
    /// 让用户输入一段文本。
    /// </summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="message">说明正文。</param>
    /// <param name="initialValue">输入框的初值。</param>
    /// <param name="validate">
    /// 校验回调：返回一段错误文案表示拒绝，返回 <see langword="null"/> 表示接受。
    /// 传 <see langword="null"/> 就是不校验。
    /// </param>
    /// <returns>用户输入的文本；点「取消」或直接关掉对话框时返回 <c>null</c>。</returns>
    /// <remarks>
    /// <para>
    /// 为 §15.8 的标签编辑而生——文档没有给这个对话框的样子，它是自拟的。
    /// </para>
    /// <para>
    /// <strong>校验回调交给调用方传进来</strong>，而不是让本服务去认标签规则（§5.8 那套
    /// 长度上限、非法字符）：服务层不该认识某一个具体业务，否则下一个要输入的字段
    /// 就得往这里再加一个方法。校验不通过时对话框<strong>不关闭</strong>，
    /// 用户改一改就能接着提交——关掉再弹一个错误框的话，他刚打的那一串就没了。
    /// </para>
    /// <para>
    /// 返回类型是可空的：用户取消与「输入了空串」是两件事。空串对标签编辑是有意义的
    /// （清空全部标签），所以不能拿它兼任「取消」。
    /// </para>
    /// </remarks>
    Task<string?> PromptAsync(
        string title,
        string message,
        string initialValue,
        Func<string, string?>? validate = null);

    /// <summary>提示错误。用于 §11.5 的保存失败、§10.4 的目录不可访问。</summary>
    Task ShowErrorAsync(string title, string message);

    /// <summary>提示普通信息。</summary>
    Task ShowInfoAsync(string title, string message);
}
