namespace LumiMemo.App.ViewModels;

/// <summary>
/// 便签的保存状态，纯粹是界面提示用的临时状态（§18.1）。
/// </summary>
/// <remarks>
/// <strong>绝不落盘。</strong>它属于 §18.1 的「第三类状态」——
/// 与 <c>CaretIndex</c>、<c>IsComposing</c> 一样只活在内存里。
/// 把它写进 Markdown 或 layout.json 都会引入一个每次按键都要写盘的字段。
/// </remarks>
public enum SaveStatus
{
    /// <summary>内容与磁盘一致，没有待写盘的改动。</summary>
    Saved,

    /// <summary>有改动尚未写盘（去抖窗口内，或保存正在进行）。</summary>
    Pending,

    /// <summary>正在写盘。</summary>
    Saving,

    /// <summary>最近一次写盘失败。详情由界面另行提示（§11.5）。</summary>
    Failed,
}
