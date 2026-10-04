using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using LumiMemo.WinUI.Controls;
using LumiText.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LumiMemo.WinUI.Pages;

/// <summary>设置页面（当前是自动标题配置）；持久化由 <see cref="ISettingsStore"/> 负责。</summary>
/// <remarks>
/// 原来的实现是代码构建的 ContentDialog；改成页面后校验与保存语义不变，
/// 由壳在每次切入本页时调 <see cref="Reload"/> 刷新字段。
/// </remarks>
public sealed partial class SettingsPage : UserControl
{
    private readonly AppSettings _settings;
    private readonly ISettingsStore _store;

    public SettingsPage(AppSettings settings, ISettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);

        _settings = settings;
        _store = store;
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
    }

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
