using System.Windows;
using System.Windows.Controls;

namespace LumiMemo.App.Views;

/// <summary>
/// 自绘的多选一对话框（§7.3 的恢复冲突用）。
/// </summary>
/// <remarks>
/// <para>
/// <strong>为什么不直接用 <c>MessageBox</c></strong>：它的按钮是系统给的
/// 「是 / 否 / 取消」，既改不了文案、也放不下第三档。而 §7.3 需要用户在
/// 「重命名 / 恢复到笔记目录根 / 取消」之间挑，且第二个确认框的按钮文字必须是
/// 「永久删除」这种一眼能分辨的词——用系统按钮的话，用户点错就是覆盖掉另一个文件。
/// </para>
/// <para>
/// 它是纯粹的呈现层：没有判断、不认识回收站、也不落任何盘。选项的含义由调用方
/// 按返回的下标解读（<c>TrashViewModel</c>）。
/// </para>
/// <para>
/// <strong>不放进自动化测试</strong>：<c>ShowDialog</c> 需要消息泵，而无头进程里
/// 点不到任何按钮（§21.3）。能自动验的是「XAML 能不能加载」，那由
/// <c>SettingsWindowTests</c> 那一路的构造型用例覆盖。
/// </para>
/// </remarks>
public partial class ChoiceDialog : Window
{
    /// <summary>用户没选任何一项就关掉了对话框。</summary>
    private const int NoChoice = -1;

    private int _result = NoChoice;

    /// <summary>按传入的选项搭出对话框。</summary>
    /// <param name="title">标题栏文字。</param>
    /// <param name="message">说明正文。</param>
    /// <param name="choices">选项文案，至少一项。</param>
    /// <param name="defaultIndex">默认选中的那一项。</param>
    public ChoiceDialog(string title, string message, IReadOnlyList<string> choices, int defaultIndex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(choices);

        if (choices.Count == 0)
        {
            throw new ArgumentException("至少要有一个选项。", nameof(choices));
        }

        InitializeComponent();

        Title = title;
        MessageText.Text = message;

        // 分组名必须显式给：不给的话同一次构造出来的 RadioButton 各自成组，
        // 于是多个选项能同时被选中——用户会以为自己一次挑了两件事。
        string group = $"choice-{Guid.NewGuid():N}";

        for (int i = 0; i < choices.Count; i++)
        {
            var radio = new RadioButton
            {
                Content = choices[i],
                GroupName = group,
                IsChecked = i == Math.Clamp(defaultIndex, 0, choices.Count - 1),
                Margin = new Thickness(0, 0, 0, 8),
                Tag = i,
            };

            ChoicePanel.Children.Add(radio);
        }
    }

    /// <summary>以模态方式显示，返回用户选中的下标。</summary>
    /// <param name="owner">
    /// 宿主窗口；传 <see langword="null"/> 表示当前没有窗口可以当宿主。
    /// </param>
    /// <returns>选中项的下标；用户关掉对话框时返回 <c>-1</c>。</returns>
    public int Ask(Window? owner)
    {
        if (owner is null)
        {
            // 没有宿主时不能留 WindowStartupLocation=CenterOwner：那会在没有 Owner
            // 的情况下静默地把窗口摆到屏幕左上角。
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
        // 取第一个被选中的那一个。单选组正常情况下只可能有一个，
        // 但这行不能写成「一定有一个」——分组一旦被改坏，这里要给出的是
        // 「没选」而不是随便挑一个替用户做决定。
        foreach (UIElement child in ChoicePanel.Children)
        {
            if (child is RadioButton { IsChecked: true, Tag: int index })
            {
                _result = index;

                break;
            }
        }

        if (_result != NoChoice)
        {
            Close();
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
