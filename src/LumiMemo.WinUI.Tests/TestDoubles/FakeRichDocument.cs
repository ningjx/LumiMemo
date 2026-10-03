using LumiMemo.WinUI.Controls;

namespace LumiMemo.WinUI.Tests.TestDoubles;

/// <summary>不出 UI 的 <see cref="IRichTextDocument"/> 替身。</summary>
/// <remarks>
/// <see cref="Type"/> 模拟用户输入：更新纯文本并发 <see cref="UserEdited"/>。
/// 权威字节用 <see cref="Content"/> 显式脚本化——保存测试要断言「字节原样落进 Note」。
/// </remarks>
public sealed class FakeRichDocument : IRichTextDocument
{
    private string _plainText = string.Empty;

    /// <summary><see cref="SaveContent"/> 要交出去的字节。</summary>
    public byte[] Content { get; set; } = [];

    public string PlainText => _plainText;

    public event EventHandler? UserEdited;

    public byte[] SaveContent() => Content;

    public Task LoadAsync(byte[] content)
    {
        Content = content;

        return Task.CompletedTask;
    }

    /// <summary>模拟用户输入：换掉纯文本并触发一次编辑事件。</summary>
    public void Type(string text)
    {
        _plainText = text;
        UserEdited?.Invoke(this, EventArgs.Empty);
    }
}
