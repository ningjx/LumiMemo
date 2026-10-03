namespace LumiMemo.WinUI.Controls;

/// <summary>编辑区对外的窄接口：纯文本投影、权威内容序列化、加载与「用户真的改了」事件。</summary>
/// <remarks>
/// <para>
/// 抽这一层是为了让 <c>NoteViewModel</c> 可测：真实实现绑在具体编辑器上，
/// 无头测试进程里没有它；替身只需实现这四个成员。
/// </para>
/// <para>
/// <strong>权威内容是不透明字节</strong>（O1，2026-10-03 拍板改名 <c>SaveContent</c>）：
/// 格式由编辑器内核决定（当前内核 <c>LumiEditor</c> 产 v2 JSON 字节），
/// VM 与 <c>Note.RichTextContent</c> 全程不解析——换内核 VM 零改动。
/// </para>
/// </remarks>
public interface IRichTextDocument
{
    /// <summary>编辑区当前的纯文本（不含格式）。</summary>
    string PlainText { get; }

    /// <summary>把编辑区当前内容序列化为权威格式字节（不透明，VM 不解析）。</summary>
    byte[] SaveContent();

    /// <summary>把权威格式字节载入编辑区。</summary>
    Task LoadAsync(byte[] content);

    /// <summary>用户真的改了内容（文字编辑、格式命令、插图都会触发）。</summary>
    event EventHandler? UserEdited;
}
