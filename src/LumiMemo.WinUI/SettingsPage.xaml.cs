using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.Infrastructure.Io;
using LumiMemo.Infrastructure.Storage;
using LumiMemo.WinUI.Controls;
using LumiMemo.WinUI.Services;
using LumiText.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LumiMemo.WinUI.Pages;

/// <summary>设置页面：存储位置与自动标题配置；持久化由 <see cref="ISettingsStore"/> 负责。</summary>
/// <remarks>
/// <para>
/// 原来的实现是代码构建的 ContentDialog；改成页面后校验与保存语义不变，
/// 由壳在每次切入本页时调 <see cref="Reload"/> 刷新字段。
/// </para>
/// <para>
/// <strong>存储位置自成一事、不走下面那个「保存」按钮</strong>：它选完目录要问「要不要搬便签」、
/// 存完还要提示重启，是一整条事务；而「保存」按钮只管自动标题那几项，
/// 两者混在一起会让「改了目录但没点保存」这种中间状态变得没法解释。
/// </para>
/// </remarks>
public sealed partial class SettingsPage : UserControl
{
    private readonly AppSettings _settings;
    private readonly ISettingsStore _store;
    private readonly IAppPaths _paths;
    private readonly NotesFolderCopier _copier;
    private readonly IAppLifecycle _lifecycle;
    private readonly Func<Task<string?>> _pickFolder;

    public SettingsPage(
        AppSettings settings,
        ISettingsStore store,
        IAppPaths paths,
        NotesFolderCopier copier,
        IAppLifecycle lifecycle,
        Func<Task<string?>> pickFolder)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(copier);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(pickFolder);

        _settings = settings;
        _store = store;
        _paths = paths;
        _copier = copier;
        _lifecycle = lifecycle;
        _pickFolder = pickFolder;

        InitializeComponent();
        LoadFromSettings();

        // 设置页的滚动条也穿同款（显隐行为见 ScrollBarReveal）。
        SettingsScroll.Loaded += (_, _) => ScrollBarReveal.AttachTo(SettingsScroll);
    }

    /// <summary>每次显示本页时调：重新载入当前设置值并清掉上一次的提示。</summary>
    public void Reload()
    {
        LoadFromSettings();
        StatusText.Text = string.Empty;
        ErrorText.Text = string.Empty;
        NotesFolderStatusText.Text = string.Empty;
        NotesFolderErrorText.Text = string.Empty;
    }

    private void LoadFromSettings()
    {
        LlmSettings current = _settings.Llm;
        EnabledSwitch.IsOn = current.Enabled;
        EndpointBox.Text = current.Endpoint;
        ModelBox.Text = current.Model;

        // 密钥不回填明文：留空表示保持原密钥。
        KeyBox.Password = string.Empty;
        KeyBox.PlaceholderText = string.IsNullOrEmpty(current.ApiKey)
            ? "可留空（本地模型）"
            : "已保存；留空则保持原密钥";
        ClearKeyCheck.IsChecked = false;
        PromptBox.Text = current.Prompt;

        LoadNotesFolderSection();
    }

    /// <summary>
    /// 存储位置一节：显示<strong>当前生效</strong>的目录，另按需要提示「已改、待重启」。
    /// </summary>
    /// <remarks>
    /// 显示的必须是 <see cref="IAppPaths.NotesFolder"/> 而不是 <see cref="AppSettings.NotesFolder"/>：
    /// 用户改完设置、还没重启的那段时间里，程序实际读写的仍是前者。
    /// 两者不一致时把待生效的新值连同一颗「立即重启」按钮摆出来。
    /// </remarks>
    private void LoadNotesFolderSection()
    {
        string active = _paths.NotesFolder ?? string.Empty;
        NotesFolderBox.Text = active;

        string pending = _settings.NotesFolder;
        bool restartNeeded = !string.IsNullOrWhiteSpace(pending)
            && !string.Equals(pending, active, StringComparison.OrdinalIgnoreCase);

        if (restartNeeded)
        {
            RestartText.Text = $"存储位置已改为 {pending}，重启后生效。";
        }

        RestartPanel.Visibility = restartNeeded ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnChangeNotesFolderClick(object sender, RoutedEventArgs e)
    {
        NotesFolderStatusText.Text = string.Empty;
        NotesFolderErrorText.Text = string.Empty;

        string current = _paths.NotesFolder ?? string.Empty;

        string? picked;
        try
        {
            picked = await _pickFolder();
        }
        catch (Exception exception)
        {
            NotesFolderErrorText.Text = $"打开文件夹选择器失败：{exception.Message}";
            return;
        }

        if (picked is null
            || string.Equals(picked, current, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? error = NotesFolderProbe.TryEnsureUsable(picked);
        if (error is not null)
        {
            NotesFolderErrorText.Text = error;
            return;
        }

        int noteCount = NotesFolderCopier.CountNotes(current);
        if (noteCount > 0)
        {
            bool? copy = await AskWhetherToCopyAsync(noteCount);
            if (copy is null)
            {
                return;   // 取消：整条流程中止，设置一个字都不动
            }

            if (copy is true && !await TryCopyNotesAsync(current, picked))
            {
                return;
            }
        }

        try
        {
            _settings.NotesFolder = picked;
            await _store.SaveAsync(_settings);
        }
        catch (Exception exception)
        {
            NotesFolderErrorText.Text = $"设置保存失败：{exception.Message}";
            return;
        }

        LoadNotesFolderSection();
    }

    /// <summary>问要不要把现有便签搬到新文件夹；返回 <see langword="null"/> 表示用户取消。</summary>
    private async Task<bool?> AskWhetherToCopyAsync(int noteCount)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "复制现有便签？",
            Content = $"当前文件夹里有 {noteCount} 张便签。要把它们复制到新文件夹吗？"
                + "原文件会留在原处，不会被删除。",
            PrimaryButtonText = "复制",
            SecondaryButtonText = "不复制",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        ContentDialogResult result = await dialog.ShowAsync();

        return result switch
        {
            ContentDialogResult.Primary => true,
            ContentDialogResult.Secondary => false,
            _ => null,
        };
    }

    /// <summary>复制便签；失败返回 <see langword="false"/> 并已写明原因。</summary>
    /// <remarks>复制失败就不改设置——否则用户会得到一个「指向空目录的新配置」。</remarks>
    private async Task<bool> TryCopyNotesAsync(string source, string destination)
    {
        CopyResult result;
        try
        {
            result = await _copier.CopyAsync(source, destination);
        }
        catch (Exception exception)
        {
            NotesFolderErrorText.Text = $"复制便签失败：{exception.Message}";
            return false;
        }

        NotesFolderStatusText.Text = DescribeCopyResult(result);

        return true;
    }

    private static string DescribeCopyResult(CopyResult result)
    {
        var parts = new List<string> { $"已复制 {result.Copied} 张便签" };

        if (result.Skipped > 0)
        {
            parts.Add($"跳过 {result.Skipped} 张（新文件夹里已有同名）");
        }

        if (result.Failed > 0)
        {
            parts.Add($"{result.Failed} 张复制失败");
        }

        return string.Join("，", parts) + "。";
    }

    private void OnRestartClick(object sender, RoutedEventArgs e) => _lifecycle.Relaunch();

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        StatusText.Text = string.Empty;
        ErrorText.Text = string.Empty;

        LlmSettings current = _settings.Llm;

        if (EnabledSwitch.IsOn && (string.IsNullOrWhiteSpace(ModelBox.Text)
            || !Uri.TryCreate(EndpointBox.Text.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttps
                && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))))
        {
            ErrorText.Text = "启用时请填写模型和 HTTPS 端点；本机 HTTP 端点也可使用。";
            return;
        }

        var updated = new LlmSettings
        {
            Enabled = EnabledSwitch.IsOn,
            Endpoint = EndpointBox.Text.Trim(),
            Model = ModelBox.Text.Trim(),
            Prompt = string.IsNullOrWhiteSpace(PromptBox.Text) ? LlmSettings.DefaultPrompt : PromptBox.Text.Trim(),
            ApiKey = ClearKeyCheck.IsChecked is true ? ""
                : string.IsNullOrEmpty(KeyBox.Password) ? current.ApiKey
                : KeyBox.Password,
        };

        try
        {
            _settings.Llm = updated;
            await _store.SaveAsync(_settings);
            LoadFromSettings();
            StatusText.Text = "已保存";
        }
        catch (Exception exception)
        {
            ErrorText.Text = $"设置保存失败：{exception.Message}";
        }
    }
}
