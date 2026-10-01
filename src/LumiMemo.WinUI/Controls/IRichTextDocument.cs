namespace LumiMemo.WinUI.Controls;

/// <summary>编辑区对外的窄接口：纯文本投影、RTF 序列化、加载与「用户真的改了」事件。</summary>
/// <remarks>
/// 抽这一层是为了让 <c>NoteViewModel</c> 可测：真实实现（<c>RichEditorHost</c>）绑在
/// <c>RichEditBox</c> 上，无头测试进程里没有它；替身只需实现这四个成员。
/// </remarks>
public interface IRichTextDocument
{
    /// <summary>编辑区当前的纯文本（不含格式）。</summary>
    string PlainText { get; }

    /// <summary>把编辑区当前内容序列化为 RTF——权威内容。</summary>
    byte[] SaveRtf();

    /// <summary>把 RTF 载入编辑区。</summary>
    Task LoadAsync(byte[] rtf);

    /// <summary>用户真的改了内容（文字编辑、格式命令、插图都会触发）。</summary>
    event EventHandler? UserEdited;
}
