using LumiMemo.Core.Abstractions;
using LumiMemo.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LumiMemo.WinUI.Services;

/// <summary>WinUI 自动标题配置界面；持久化仍由 ISettingsStore 负责。</summary>
public static class LlmSettingsDialog
{
    public static async Task ShowAsync(XamlRoot root, AppSettings appSettings, ISettingsStore store)
    {
        LlmSettings current = appSettings.Llm;
        var enabled = new ToggleSwitch { Header = "自动生成标题", IsOn = current.Enabled };
        var endpoint = new TextBox { Header = "端点", Text = current.Endpoint,
            PlaceholderText = "https://api.openai.com/v1/chat/completions" };
        var model = new TextBox { Header = "模型", Text = current.Model, PlaceholderText = "填写服务商提供的模型 ID" };
        var key = new PasswordBox { Header = "API 密钥", PlaceholderText =
            string.IsNullOrEmpty(current.ApiKey) ? "可留空（本地模型）" : "已保存；留空则保持原密钥" };
        var clearKey = new CheckBox { Content = "清除已保存的密钥" };
        var keyHint = new TextBlock { Text = "密钥以明文保存在本机 settings.json。", TextWrapping = TextWrapping.Wrap };
        var prompt = new TextBox { Header = "生成标题的提示词", Text = current.Prompt,
            TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Height = 104 };
        var error = new TextBlock { Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(255, 180, 45, 60)), TextWrapping = TextWrapping.Wrap };
        var fields = new StackPanel { Spacing = 12 };
        foreach (UIElement field in new UIElement[] { enabled, endpoint, model, key, clearKey, keyHint, prompt, error })
        {
            fields.Children.Add(field);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "自动标题设置",
            Content = new ScrollViewer { Content = fields, MaxHeight = 480 },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (enabled.IsOn && (string.IsNullOrWhiteSpace(model.Text)
                || !Uri.TryCreate(endpoint.Text.Trim(), UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))))
            {
                error.Text = "启用时请填写模型和 HTTPS 端点；本机 HTTP 端点也可使用。";
                args.Cancel = true;
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var updated = new LlmSettings
        {
            Enabled = enabled.IsOn,
            Endpoint = endpoint.Text.Trim(),
            Model = model.Text.Trim(),
            Prompt = string.IsNullOrWhiteSpace(prompt.Text) ? LlmSettings.DefaultPrompt : prompt.Text.Trim(),
            ApiKey = clearKey.IsChecked is true ? ""
                : string.IsNullOrEmpty(key.Password) ? current.ApiKey
                : key.Password
        };
        appSettings.Llm = updated;
        await store.SaveAsync(appSettings);
    }
}
