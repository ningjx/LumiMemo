namespace LumiMemo.App.Views;

/// <summary>
/// <see cref="SettingsWindow"/> 的页签。
/// </summary>
/// <remarks>
/// <para>
/// <strong>它的值就是 XAML 里 <c>TabControl</c> 的顺序</strong>，因为
/// <see cref="SettingsWindow.SelectTab"/> 直接拿它当 <c>SelectedIndex</c> 用。
/// 在 <c>.xaml</c> 里插一个新页签而不同步这里，切页就会静默地切错——
/// 因此那两个文件互相指着对方，改一个必须看另一个。
/// </para>
/// <para>
/// 之所以要它，是因为托盘菜单的「回收站（N）...」得直接落到回收站那一页（§15.9），
/// 而 <c>TabControl</c> 的选中项没有绑 ViewModel（那是纯界面状态）。
/// 传布尔值也能用，但调用点会写成一个没人看得懂的 <c>Show(true)</c>。
/// </para>
/// </remarks>
public enum SettingsTab
{
    /// <summary>常规设置。也是不指定时的默认落点。</summary>
    General = 0,

    /// <summary>回收站。托盘菜单与设置窗口里的那一页共用同一个 ViewModel。</summary>
    Trash = 1,
}
