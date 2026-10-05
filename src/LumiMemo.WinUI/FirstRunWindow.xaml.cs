using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Infrastructure.Io;
using LumiMemo.WinUI.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace LumiMemo.WinUI;

/// <summary>
/// 首次运行的存储位置向导：没有笔记目录时，启动从这里开始。
/// </summary>
/// <remarks>
/// <para>
/// <strong>它跑在容器建立之前</strong>（§17.1 两段式启动的第一段）：笔记目录定了，
/// <c>LumiNoteStorage</c> / <c>FileSystemTrashStore</c> / 便签快照才有得构造。
/// 所以它只依赖前段手工拼出来的 <see cref="AppSettings"/> 与 <see cref="ISettingsStore"/>，
/// 不从容器取任何东西。
/// </para>
/// <para>
/// <strong>关窗＝放弃启动</strong>：没有笔记目录，程序没有可用的工作状态。
/// 直接退出、下次启动再弹，比先按某个默认目录跑起来再纠正更干净——
/// 后者会在用户不知情的时候凭空造出一个目录。
/// </para>
/// </remarks>
public sealed partial class FirstRunWindow : Window
{
    private const int WindowWidth = 580;
    private const int WindowHeight = 320;

    /// <summary>用户选定的目录；关窗放弃时为 <see langword="null"/>。</summary>
    private readonly TaskCompletionSource<string?> _completion = new();

    private readonly AppSettings _settings;
    private readonly ISettingsStore _store;

    public FirstRunWindow(AppSettings settings, ISettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);

        _settings = settings;
        _store = store;

        InitializeComponent();

        PathBox.Text = SuggestedDefaultPath();
        CenterOnScreen();

        // 用户点 X 关窗与点「退出」是同一条路：放弃启动。
        Closed += (_, _) => _completion.TrySetResult(null);
    }

    /// <summary>显示向导并等用户选完；放弃时返回 <see langword="null"/>。</summary>
    public static async Task<string?> ChooseNotesFolderAsync(
        AppSettings settings, ISettingsStore store)
    {
        var window = new FirstRunWindow(settings, store);

        window.Activate();

        string? chosen = await window._completion.Task;

        // 这一句让编译器把 window 提升进状态机字段：await 之后仍引用它，
        // 整个等待期间窗口对象都不会失去托管引用（WinUI 的窗口在这里没有别的长引用兜底）。
        GC.KeepAlive(window);

        return chosen;
    }

    /// <summary>
    /// 预填的建议位置：<c>我的文档\LumiMemo</c>。
    /// </summary>
    /// <remarks>
    /// 这正是改动之前程序静默使用的那个位置——现在它降级成一个「默认选中」的选项，
    /// 用户点一下确定即可，但至少知道自己存在哪儿。
    /// </remarks>
    private static string SuggestedDefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "LumiMemo");

    private void CenterOnScreen()
    {
        DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        RectInt32 work = area.WorkArea;

        AppWindow.Resize(new SizeInt32(WindowWidth, WindowHeight));
        AppWindow.Move(new PointInt32(
            work.X + ((work.Width - WindowWidth) / 2),
            work.Y + ((work.Height - WindowHeight) / 2)));
    }

    private async void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string? picked = await FolderPickerHelper.PickFolderAsync(this);
            if (picked is not null)
            {
                PathBox.Text = picked;
                ErrorText.Text = string.Empty;
            }
        }
        catch (Exception exception)
        {
            ErrorText.Text = $"打开文件夹选择器失败：{exception.Message}";
        }
    }

    private void OnQuitClick(object sender, RoutedEventArgs e) => Close();

    private async void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = string.Empty;

        string path = PathBox.Text.Trim();
        string? error = NotesFolderProbe.TryEnsureUsable(path);
        if (error is not null)
        {
            ErrorText.Text = error;
            return;
        }

        try
        {
            _settings.NotesFolder = path;
            await _store.SaveAsync(_settings);
        }
        catch (Exception exception)
        {
            ErrorText.Text = $"设置保存失败：{exception.Message}";
            return;
        }

        _completion.TrySetResult(path);
        Close();
    }
}
