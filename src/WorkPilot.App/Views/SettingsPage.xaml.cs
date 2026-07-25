using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WorkPilot.Views;

public sealed partial class SettingsPage : Page
{
    private readonly Services.AppServices _services = App.Services;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            EndpointBox.Text = _services.Settings.Endpoint;
            ModelBox.Text = _services.Settings.Model;
            SystemPromptBox.Text = _services.Settings.UserSystemPrompt;
            StatusText.Text = _services.Secrets.HasApiKey ? "已安全保存 API Key" : "尚未保存 API Key";
        };
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            var endpoint = EndpointBox.Text.Trim().TrimEnd('/');
            var model = ModelBox.Text.Trim();
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) throw new ArgumentException("API 端点不是有效 URL");
            if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback) throw new ArgumentException("远程 API 端点必须使用 HTTPS");
            if (model.Length is < 1 or > 120) throw new ArgumentException("模型名称需为 1–120 个字符");
            var systemPrompt = SystemPromptBox.Text.Trim();
            if (systemPrompt.Length > 8000) throw new ArgumentException("Agent 工作方式不能超过 8000 个字符");
            if (!string.IsNullOrWhiteSpace(ApiKeyBox.Password))
            {
                _services.Secrets.SaveApiKey(ApiKeyBox.Password.Trim());
                ApiKeyBox.Password = "";
            }
            await _services.SaveSettingsAsync(_services.Settings with { Endpoint = endpoint, Model = model, UserSystemPrompt = systemPrompt });
            StatusText.Text = "设置已保存";
        }
        catch (Exception error) { await ShowErrorAsync(error); }
    }

    private async void OnDeleteKey(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "删除已保存的 API Key？",
            Content = "删除后，新的 Agent 任务将无法调用模型，直到再次保存密钥。",
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _services.Secrets.DeleteApiKey(); ApiKeyBox.Password = ""; StatusText.Text = "API Key 已删除";
    }

    private async Task ShowErrorAsync(Exception error)
    {
        Services.AppLogger.Error("Settings operation failed", error);
        await new ContentDialog { XamlRoot = XamlRoot, Title = "设置保存失败", Content = error.Message, CloseButtonText = "知道了" }.ShowAsync();
    }
}
