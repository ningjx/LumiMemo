using System.Windows;

namespace LumiMemo.App.Views;

/// <summary>
/// 自绘的文本输入对话框（§15.8 的标签编辑用）。
/// </summary>
/// <remarks>
/// <para>
/// WPF 里没有 <c>InputBox</c>——那是 WinForms 的类型，而这个项目不开
/// <c>UseWindowsForms</c>；<c>MessageBox</c> 连输入框都没有。所以只能自绘。
/// </para>
/// <para>
/// <strong>校验回调由调用方传入</strong>，本类不认识任何具体规则（不认识标签、不认识长度上限）。
/// 它只负责「回调说有错就不关窗，把错误写出来」。这样下一个要输入的字段不必改本类一行。
/// </para>
/// <para>
/// <strong>不放进自动化测试</strong>：<c>ShowDialog</c> 需要消息泵，无头进程里点不到按钮（§21.3）。
/// 能自动验的是「XAML 能不能加载」，那一路由构造型用例覆盖。
/// </para>
/// </remarks>
public partial class PromptDialog : Window
{
    private readonly Func<string, string?>? _validate;

    private string? _result;

    /// <summary>按传入的内容搭出对话框。</summary>
    /// <param name="title">标题栏文字。</param>
    /// <param name="message">说明正文。</param>
    /// <param name="initialValue">输入框的初值。</param>
    /// <param name="validate">校验回调，见类注释。传 <see langword="null"/> 表示不校验。</param>
    public PromptDialog(
        string title,
        string message,
        string initialValue,
        Func<string, string?>? validate = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(initialValue);

        InitializeComponent();

        Title = title;
        MessageText.Text = message;
        InputBox.Text = initialValue;
        _validate = validate;

        // 初值全选：用户多半是要改掉它，而不是在末尾接着打。
        // 光标放在 Loaded 里而不是构造里——Show() 之前设的选中区会被首次布局冲掉。
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    /// <summary>以模态方式显示，返回用户输入的文本。</summary>
    /// <param name="owner">
    /// 宿主窗口；传 <see langword="null"/> 表示当前没有窗口可以当宿主。
    /// </param>
    /// <returns>输入的文本；用户取消或关掉对话框时返回 <c>null</c>。</returns>
    public string? Ask(Window? owner)
    {
        if (owner is null)
        {
            // 同 ChoiceDialog：没有宿主时留着 CenterOwner 会把窗口静默摆到屏幕左上角。
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        else
        {
            Owner = owner;
        }

        _ = ShowDialog();

        return _result;
    }

    private void OnAcceptClick(object sender, RoutedEventArgs e)
    {
        string text = InputBox.Text;
        string? error = _validate?.Invoke(text);

        if (error is not null)
        {
            // 不关窗。用户刚打的那一串还在输入框里，改一改就能接着提交。
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            InputBox.Focus();

            return;
        }

        _result = text;

        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
