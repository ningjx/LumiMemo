using LumiMemo.WinUI.Controls;

namespace LumiMemo.WinUI.Tests.TestDoubles;

/// <summary>不出 UI 的 <see cref="IRichTextDocument"/> 替身。</summary>
/// <remarks>
/// <see cref="Type"/> 模拟用户输入：更新纯文本并发 <see cref="UserEdited"/>。
/// RTF 用 <see cref="Rtf"/> 显式脚本化——保存测试要断言「RTF 原样落进 Note」。
/// </remarks>
public sealed class FakeRichDocument : IRichTextDocument
{
    private string _plainText = string.Empty;

    /// <summary><see cref="SaveRtf"/> 要交出去的字节。</summary>
    public byte[] Rtf { get; set; } = [];

    public string PlainText => _plainText;

    public event EventHandler? UserEdited;

    public byte[] SaveRtf() => Rtf;

    public Task LoadAsync(byte[] rtf)
    {
        Rtf = rtf;

        return Task.CompletedTask;
    }

    /// <summary>模拟用户输入：换掉纯文本并触发一次编辑事件。</summary>
    public void Type(string text)
    {
        _plainText = text;
        UserEdited?.Invoke(this, EventArgs.Empty);
    }
}
