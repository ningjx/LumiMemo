using LumiMemo.App.Services;
using LumiMemo.App.ViewModels;
using LumiMemo.Core.Services;
using LumiMemo.Infrastructure.Io;

namespace LumiMemo.App.Tests.TestDoubles;

/// <summary>
/// 一套装配好的 <see cref="TrayViewModel"/> 与它的全部替身。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>ManagerHarness</c> 同一用意：<see cref="TrayViewModel"/> 有八个依赖，
/// 每个用例各拼一遍的话，「某个用例少了哪个替身」会变成一种看不出来的差异。
/// </para>
/// <para>
/// <strong>它包着 <see cref="ManagerHarness"/></strong> 而不是重造一份管理器：
/// 托盘转发过去的「新建」「显示全部」「收起全部」「重新加载」都是管理器那几个命令，
/// 断言它们有没有被触发，靠的正是那边已经记好的 <c>NoteService</c> 与 <c>Windows</c>。
/// </para>
/// <para>
/// <see cref="SettingsLauncher"/> 缺省是一个<strong>会抛</strong>的工厂：绝大多数用例
/// 不该打开设置窗口，而真的打开了在这里是立刻可见的失败，不是某个断言晚一点才红。
/// 需要验「落在哪一页」的用例自己传一个进来（见 <c>TrayViewModelTests</c>）。
/// </para>
/// </remarks>
public sealed class TrayHarness : IDisposable
{
    public TrayHarness(SettingsWindowLauncher? settingsLauncher = null)
    {
        Trash = new TrashService(TrashStore, Paths, Manager.Store, Manager.Index, new FakeNoteRepository());

        SettingsLauncher = settingsLauncher ?? new SettingsWindowLauncher(
            () => throw new InvalidOperationException("本用例不该打开设置窗口。"));

        Vm = new TrayViewModel(
            Manager.Vm, Presenter, SettingsLauncher, Shell, Paths, Dialogs, Trash, Lifetime);
    }

    /// <summary>
    /// 只做出内存的 <see cref="AppPaths"/>：<see cref="TrashService"/> 要的是具体类型，
    /// 而它的回收站目录由笔记目录派生。
    /// </summary>
    public AppPaths Paths { get; } = CreatePaths();

    public FakeTrashStore TrashStore { get; } = new();

    public TrashService Trash { get; }

    public ManagerHarness Manager { get; } = new();

    public RecordingShellLauncher Shell { get; } = new();

    public RecordingDialogService Dialogs { get; } = new();

    public RecordingManagerWindowPresenter Presenter { get; } = new();

    public RecordingApplicationLifetime Lifetime { get; } = new();

    public SettingsWindowLauncher SettingsLauncher { get; }

    public TrayViewModel Vm { get; }

    public void Dispose() => Manager.Dispose();

    private static AppPaths CreatePaths()
    {
        var paths = new AppPaths(@"C:\fake-local");
        paths.SetNotesFolder(@"D:\notes");

        return paths;
    }
}
