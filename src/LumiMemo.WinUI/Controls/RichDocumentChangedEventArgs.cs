namespace LumiMemo.WinUI.Controls;

/// <summary>区分文本事件与用户主动执行的富文本命令。</summary>
public sealed class RichDocumentChangedEventArgs(bool isUserCommand) : EventArgs
{
    public bool IsUserCommand { get; } = isUserCommand;
}
