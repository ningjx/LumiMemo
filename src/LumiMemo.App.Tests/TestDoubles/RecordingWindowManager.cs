using LumiMemo.App.Abstractions;
using LumiMemo.App.ViewModels;
using LumiMemo.Core.Models;

namespace LumiMemo.App.Tests.TestDoubles;

/// <summary>
/// <see cref="IWindowManager"/> 的记录型替身（§14.2：「接口……让窗口层可被替换成记录型替身」）。
/// </summary>
/// <remarks>
/// <para>
/// 单元测试里没有真实窗口，也不该有。ViewModel 对窗口的全部诉求就是「按 id 做这几件事」，
/// 因此只需要记录调用序列，让测试断言「命令触发了正确的窗口操作」即可。
/// </para>
/// <para>
/// 它同时维护一个「哪些便签有窗口」的集合，好让 <see cref="IsNoteOpen"/> 的语义与真实实现一致，
/// 而不是永远返回 <c>false</c>——那样会让依赖它的分支永远走不到。
/// </para>
/// </remarks>
public sealed class RecordingWindowManager : IWindowManager
{
    private readonly Dictionary<Guid, NoteViewModel> _openNotes = [];

    /// <summary>所有被调用过的方法名与参数，按发生顺序。</summary>
    public List<string> Calls { get; } = [];

    /// <summary>所有被 <see cref="RefreshNote"/> 刷新过的便签 id，按发生顺序。</summary>
    public List<Guid> RefreshedNotes { get; } = [];

    /// <summary>最后一次 <see cref="ShowNote"/> 收到的 ViewModel。</summary>
    public NoteViewModel? LastShownViewModel { get; private set; }

    /// <summary>最后一次 <see cref="ShowNote"/> 收到的布局。</summary>
    public NoteLayout? LastShownLayout { get; private set; }

    /// <summary>所有被关闭过的便签 id，按发生顺序。</summary>
    public List<Guid> ClosedNotes { get; } = [];

    /// <summary><see cref="TryApplyClickThrough"/> 的返回值。用于模拟 §13.7 原型失败的环境。</summary>
    public bool ClickThroughSupported { get; set; } = true;

    /// <inheritdoc />
    public void ShowNote(NoteViewModel viewModel, NoteLayout layout)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(layout);

        Calls.Add($"ShowNote({viewModel.Id})");
        LastShownViewModel = viewModel;
        LastShownLayout = layout;
        _openNotes[viewModel.Id] = viewModel;
    }

    /// <inheritdoc />
    public void CloseNote(Guid noteId)
    {
        Calls.Add($"CloseNote({noteId})");
        ClosedNotes.Add(noteId);
        _openNotes.Remove(noteId);
    }

    /// <inheritdoc />
    public void RefreshNote(Guid noteId)
    {
        Calls.Add($"RefreshNote({noteId})");
        RefreshedNotes.Add(noteId);

        // 与真实现一样去动那扇窗里的 ViewModel，而不是只记一笔：
        // 「外部改了之后窗口里的正文真的换成磁盘版了吗」是本方法存在的全部理由，
        // 只记调用的话，一个把 RefreshFromNote 写反了（搬成内存版覆盖磁盘版）的实现照样是绿的。
        if (_openNotes.TryGetValue(noteId, out NoteViewModel? viewModel))
        {
            viewModel.RefreshFromNote();
        }
    }

    /// <inheritdoc />
    public void HideAllNotes()
    {
        Calls.Add("HideAllNotes()");
        _openNotes.Clear();
    }

    /// <inheritdoc />
    public void ShowAllNotes() => Calls.Add("ShowAllNotes()");

    /// <inheritdoc />
    public void CaptureGeometry(Guid noteId, NoteLayout target)
    {
        ArgumentNullException.ThrowIfNull(target);

        Calls.Add($"CaptureGeometry({noteId})");
    }

    /// <inheritdoc />
    public void ApplyCollapsed(Guid noteId, bool collapsed) => Calls.Add($"ApplyCollapsed({noteId}, {collapsed})");

    /// <inheritdoc />
    public void ApplyTopMost(Guid noteId, bool topMost) => Calls.Add($"ApplyTopMost({noteId}, {topMost})");

    /// <inheritdoc />
    public void ApplyLocked(Guid noteId, bool locked) => Calls.Add($"ApplyLocked({noteId}, {locked})");

    /// <inheritdoc />
    public bool TryApplyClickThrough(Guid noteId, bool enabled)
    {
        Calls.Add($"TryApplyClickThrough({noteId}, {enabled})");

        return ClickThroughSupported;
    }

    /// <inheritdoc />
    public void OnDisplayConfigurationChanged() => Calls.Add("OnDisplayConfigurationChanged()");

    /// <inheritdoc />
    public bool IsNoteOpen(Guid noteId) => _openNotes.ContainsKey(noteId);

    /// <inheritdoc />
    public void BeginShutdown() => Calls.Add("BeginShutdown()");
}
