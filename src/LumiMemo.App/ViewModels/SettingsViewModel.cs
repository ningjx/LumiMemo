using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using LumiMemo.App.Abstractions;
using LumiMemo.App.Services;
using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Core.Services;
using LumiMemo.Infrastructure.Io;
using LumiMemo.Infrastructure.Settings;

namespace LumiMemo.App.ViewModels;

/// <summary>
/// 设置窗口的界面状态（§15.9、§18.1）。<strong>回收站是它的一个页签</strong>，不单独开窗口。
/// </summary>
/// <remarks>
/// <para>
/// <strong>本轮只暴露「改了立刻生效」的设置项。</strong> 设置窗口里画一个改了没反应的开关，
/// 比不画它更糟：用户会以为程序坏了，而不是以为那个功能还没做。
/// 因此下面这些<strong>刻意不出现</strong>，各自等它背后的功能落地：
/// </para>
/// <list type="bullet">
///   <item>主题、开机启动、关闭时最小化到托盘、托盘图标、单击托盘动作——托盘还没接上（阶段 9）。</item>
///   <item>全局热键（速记 / 显示全部）——速记浮窗整块还没做。</item>
///   <item>日志级别、最大同时打开窗口数——前者没有日志 provider，后者没有消费方。</item>
///   <item>默认颜色——便签窗口的配色表现在写死在 XAML 里（§15.3 只给了黄色一套，
///   其余六色没落地），选了颜色也只有 Front Matter 会变、窗口外观不变。</item>
///   <item>附件文件夹名——附件的插入与渲染整块还没做（§6.1）。</item>
///   <item>更改笔记目录——§8.6 是一整条流程（可写性探测、嵌套检查、layout 三选一、中止条件），
///   单独一个阶段。本页只把当前目录<strong>显示</strong>出来并给一个「打开」按钮。</item>
/// </list>
/// <para>
/// <strong>保存时改的是同一份 <see cref="AppSettings"/> 实例</strong>：没显示出来的字段
/// （主题、热键……）原样留在对象上，一并写回。若在这里重新 <c>new</c> 一份设置，
/// 用户改一次自动保存延迟就会把日志级别重置成默认值。
/// </para>
/// <para>
/// <strong>不持有窗口引用</strong>（§18.3）：关闭走 ViewModel 的 <see cref="CloseRequested"/>，
/// 由窗口自己去订阅。
/// </para>
/// </remarks>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>便签窗口默认宽高的允许区间（DIP）。</summary>
    /// <remarks>
    /// 这只是挡住手输的荒唐值（0、负数、比屏幕还宽）。真正的「放进工作区」由
    /// <c>LayoutMath.Clamp</c> 在每次摆放时做——两处都要有，各自管各自的事。
    /// </remarks>
    public const double MinDefaultSize = 200;

    /// <summary>见 <see cref="MinDefaultSize"/>。</summary>
    public const double MaxDefaultSize = 1600;

    /// <summary>内容缩放的允许区间（§15.7）。</summary>
    public const double MinContentScale = 0.5;

    /// <summary>见 <see cref="MinContentScale"/>。</summary>
    public const double MaxContentScale = 3.0;

    private readonly ISettingsStore _settingsStore;
    private readonly AppPaths _paths;
    private readonly ISettingsApplier _applier;
    private readonly IDialogService _dialogs;
    private readonly IShellLauncher _shell;
    private readonly IDispatcher _dispatcher;

    /// <summary>
    /// 磁盘上那一份设置。页面上没显示的字段靠它带着走。
    /// </summary>
    /// <remarks>
    /// 载入之前是 <see langword="null"/>，此时保存会退回重新读一次磁盘——
    /// 那比用一份只填了几个字段的空对象覆盖掉用户的全部设置要安全得多。
    /// </remarks>
    private AppSettings? _settings;

    public SettingsViewModel(
        ISettingsStore settingsStore,
        AppPaths paths,
        TrashService trashService,
        ISettingsApplier applier,
        IDialogService dialogs,
        IShellLauncher shell,
        IDispatcher dispatcher,
        IMessenger messenger)
    {
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(trashService);
        ArgumentNullException.ThrowIfNull(applier);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(messenger);

        _settingsStore = settingsStore;
        _paths = paths;
        _applier = applier;
        _dialogs = dialogs;
        _shell = shell;
        _dispatcher = dispatcher;

        Trash = new TrashViewModel(trashService, paths, dialogs, shell, dispatcher, messenger);
    }

    /// <summary>窗口关闭请求。保存之后由窗口决定要不要自己关掉。</summary>
    public event EventHandler? CloseRequested;

    /// <summary>回收站页签（§7.4）。</summary>
    public TrashViewModel Trash { get; }

    /// <summary>窗口标题。</summary>
    public string Title => "设置";

    /// <summary>当前笔记文件夹，只读显示。</summary>
    public string NotesFolderText => _paths.NotesFolder ?? "（尚未选定）";

    /// <summary>保留期的可选项（§7.4）。</summary>
    public IReadOnlyList<RetentionOption> RetentionOptions { get; } =
    [
        new(7, "7 天"),
        new(30, "30 天"),
        new(90, "90 天"),

        // 0 是策略而不是天数：TrashService 见到 0 就一次都不清理（§7.4）。
        // 界面上必须写成「永不清理」，写「0 天」会被读成「立刻清空」。
        new(0, "永不清理"),
    ];

    [ObservableProperty]
    private double _defaultWidth = 360;

    [ObservableProperty]
    private double _defaultHeight = 420;

    [ObservableProperty]
    private double _contentScale = 1.0;

    [ObservableProperty]
    private bool _showStatusBar = true;

    [ObservableProperty]
    private bool _restoreAfterShowDesktop = true;

    [ObservableProperty]
    private int _autoSaveDelayMs = 500;

    [ObservableProperty]
    private int _searchDebounceMs = 150;

    [ObservableProperty]
    private RetentionOption _selectedRetention = new(30, "30 天");

    /// <summary>保存按钮下方那行状态文字。</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>
    /// 自动保存延迟的允许区间，直接来自存储层的钳制边界（§8.4）。
    /// </summary>
    /// <remarks>
    /// 做成实例属性而不是 <c>static</c>，是为了让 XAML 能用
    /// <c>{Binding MinAutoSaveDelayMs}</c> 直接取到它（静态成员绑不上）。
    /// 引用存储层的常量而不是在这里另写一遍，是为了不让两处的边界各说各话。
    /// </remarks>
    public int MinAutoSaveDelayMs => JsonSettingsStore.MinAutoSaveDelayMs;

    /// <summary>见 <see cref="MinAutoSaveDelayMs"/>。</summary>
    public int MaxAutoSaveDelayMs => JsonSettingsStore.MaxAutoSaveDelayMs;

    /// <summary>搜索去抖的允许区间，直接来自存储层的钳制边界（§8.4）。</summary>
    public int MinSearchDebounceMs => JsonSettingsStore.MinSearchDebounceMs;

    /// <summary>见 <see cref="MinSearchDebounceMs"/>。</summary>
    public int MaxSearchDebounceMs => JsonSettingsStore.MaxSearchDebounceMs;

    /// <summary>
    /// 从磁盘读一遍设置并填进各字段，顺带刷新回收站页签。
    /// </summary>
    /// <remarks>
    /// 窗口构造时不能 <c>await</c>，所以由 <c>SettingsWindow</c> 在 <c>Loaded</c> 里发起。
    /// <strong>每次打开都重新读</strong>：设置文件是用户能直接编辑的，
    /// 而且托盘那边的「回收站（N）」也依赖这里刚刷过的计数。
    /// </remarks>
    [RelayCommand]
    public async Task LoadAsync(CancellationToken ct = default)
    {
        _dispatcher.VerifyAccess();

        _settings = await _settingsStore.LoadAsync(ct);

        DefaultWidth = _settings.DefaultWidth;
        DefaultHeight = _settings.DefaultHeight;
        ContentScale = _settings.DefaultContentScale;
        ShowStatusBar = _settings.ShowStatusBar;
        RestoreAfterShowDesktop = _settings.RestoreAfterShowDesktop;
        AutoSaveDelayMs = _settings.AutoSaveDelayMs;
        SearchDebounceMs = _settings.SearchDebounceMs;

        SelectedRetention = RetentionOptions.FirstOrDefault(
            option => option.Days == _settings.TrashRetentionDays)
            ?? RetentionOptions.Single(option => option.Days == 30);

        StatusText = string.Empty;

        OnPropertyChanged(nameof(NotesFolderText));

        await Trash.RefreshAsync(ct);
    }

    /// <summary>
    /// 把页面上的值写回设置文件，并让它们立刻生效。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>先钳制再写盘</strong>，用的是存储层那两个常量。存储层在<strong>读</strong>的时候
    /// 也会钳一次，但那是兜底：让一个越界的值先落进文件、下次启动再被悄悄改掉，
    /// 用户会看到「我明明填了 1000」——不如在保存这一瞬间就改成 800 摆在他眼前。
    /// </para>
    /// <para>
    /// <strong>改完立刻灌进对象图</strong>（<see cref="ISettingsApplier.Apply"/>），
    /// 不要求用户重启。这与启动序列走的是同一段代码，因此不存在
    /// 「启动时生效一个值、设置窗口里生效另一个值」这种两套规则。
    /// </para>
    /// </remarks>
    [RelayCommand]
    public async Task SaveAsync(CancellationToken ct = default)
    {
        _dispatcher.VerifyAccess();

        // 从没载入过（窗口刚构造就被点了保存）时先补一次读，否则会拿一份空设置把用户的
        // 主题、热键、日志级别全部覆盖成默认值。
        _settings ??= await _settingsStore.LoadAsync(ct);

        DefaultWidth = Math.Clamp(DefaultWidth, MinDefaultSize, MaxDefaultSize);
        DefaultHeight = Math.Clamp(DefaultHeight, MinDefaultSize, MaxDefaultSize);
        ContentScale = Math.Clamp(ContentScale, MinContentScale, MaxContentScale);
        AutoSaveDelayMs = Math.Clamp(AutoSaveDelayMs, MinAutoSaveDelayMs, MaxAutoSaveDelayMs);
        SearchDebounceMs = Math.Clamp(SearchDebounceMs, MinSearchDebounceMs, MaxSearchDebounceMs);

        _settings.DefaultWidth = DefaultWidth;
        _settings.DefaultHeight = DefaultHeight;
        _settings.DefaultContentScale = ContentScale;
        _settings.ShowStatusBar = ShowStatusBar;
        _settings.RestoreAfterShowDesktop = RestoreAfterShowDesktop;
        _settings.AutoSaveDelayMs = AutoSaveDelayMs;
        _settings.SearchDebounceMs = SearchDebounceMs;
        _settings.TrashRetentionDays = SelectedRetention.Days;

        await _settingsStore.SaveAsync(_settings, ct);

        _applier.Apply(_settings);

        StatusText = "已保存";
    }

    /// <summary>用资源管理器打开笔记文件夹。</summary>
    [RelayCommand]
    public async Task OpenNotesFolderAsync(CancellationToken ct = default)
    {
        if (_paths.NotesFolder is not { } folder)
        {
            await _dialogs.ShowInfoAsync("设置", "还没有选定笔记文件夹。");

            return;
        }

        if (!_shell.OpenFolder(folder))
        {
            await _dialogs.ShowErrorAsync("设置", $"打不开这个文件夹：\n{folder}");
        }
    }

    /// <summary>关闭窗口。</summary>
    [RelayCommand]
    public void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 回收站保留期的一个可选项（§7.4）。
/// </summary>
/// <param name="Days">天数；<c>0</c> 表示永不清理。</param>
/// <param name="Name">界面上显示的文字。</param>
/// <remarks>
/// 做成一个类型而不是直接绑 <c>int</c>：<c>0</c> 在界面上必须显示成「永不清理」，
/// 绑 <c>int</c> 的话用户会看到一个「0 天」，而那读起来像是「立刻清空」。
/// </remarks>
public sealed record RetentionOption(int Days, string Name);
