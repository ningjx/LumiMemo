using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Services;
using LumiMemo.WinUI.ViewModels;
using Microsoft.Extensions.Logging;

namespace LumiMemo.WinUI.Services;

/// <summary>组装一张便签的 ViewModel 与窗口。</summary>
/// <remarks>
/// per-note 的对象（便签、布局、回调）不进 DI 容器——那只会在容器里制造
/// 「构造参数从哪来」的假问题。这里是「带参数的窗口」的唯一构造点，
/// 对应 WPF 版曾经的 NoteViewModelFactory。
/// </remarks>
public sealed class NoteWindowFactory(
    INoteStorage storage,
    IClock clock,
    ILayoutStore layouts,
    AutoSaveService autoSave,
    NoteTitleCoordinator titles,
    ToolbarPreferences toolbarPreferences,
    ILoggerFactory loggerFactory)
{
    public MainWindow Create(
        Note note,
        NoteLayout layout,
        Action<Guid> onClosed,
        Action onNoteChanged,
        INoteWindowActions actions)
    {
        var viewModel = new NoteViewModel(
            note,
            layout,
            storage,
            clock,
            layouts,
            autoSave,
            titles,
            onNoteChanged,
            loggerFactory.CreateLogger<NoteViewModel>());

        return new MainWindow(viewModel, layout, layouts, onClosed, actions, toolbarPreferences);
    }
}
