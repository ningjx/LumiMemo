namespace LumiMemo.App.Abstractions;

/// <summary>
/// 让用户挑一个文件夹（§8.6 首次运行向导、§15.9 切换笔记目录）。
/// </summary>
/// <remarks>
/// 与 <see cref="IDialogService"/> 同一条理由：视图层的对话框不能直接出现在业务代码里，
/// 否则那段代码在没有窗口的测试进程里就跑不起来。生产实现是
/// <c>FolderPickerDialog</c>，测试用记录型替身。
/// </remarks>
public interface IFolderPicker
{
    /// <summary>弹出文件夹选择对话框。</summary>
    /// <param name="title">对话框标题。文案取自资源文件（§24.2）。</param>
    /// <param name="initialDirectory">初始位置；<c>null</c> 时由系统决定。</param>
    /// <returns>用户选中的绝对路径；用户取消时返回 <c>null</c>。</returns>
    string? PickFolder(string title, string? initialDirectory);
}
