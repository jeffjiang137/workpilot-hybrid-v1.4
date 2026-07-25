using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WorkPilot.Models;

namespace WorkPilot.Views;

public sealed partial class ConnectionsPage : Page
{
    private readonly Services.AppServices _services = App.Services;
    public ConnectionsPage() { InitializeComponent(); Loaded += async (_, _) => await ReloadAsync(); }
    private async Task ReloadAsync()
    {
        ConnectorList.ItemsSource = await _services.Connectors.ListAsync(); McpList.ItemsSource = await _services.Mcp.ListAsync();
    }

    private async void OnAddConnector(object sender, RoutedEventArgs e)
    {
        var kind = new ComboBox { Header = "类型", ItemsSource = new[] { "GitHub", "Notion" }, SelectedIndex = 0 };
        var name = new TextBox { Header = "连接名称", MaxLength = 80 };
        var token = new PasswordBox { Header = "Personal / Integration Token", PasswordRevealMode = PasswordRevealMode.Peek };
        var note = new TextBlock { Text = "凭据将由 Windows DPAPI 加密；默认只在当前空间启用。", TextWrapping = TextWrapping.Wrap, Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 168, 168, 168)) };
        var panel = new StackPanel { Spacing = 12, MinWidth = 520 }; panel.Children.Add(kind); panel.Children.Add(name); panel.Children.Add(token); panel.Children.Add(note);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "添加连接器", Content = panel, PrimaryButtonText = "测试并保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var connectorKind = kind.SelectedIndex == 0 ? "github" : "notion";
            await _services.Connectors.ConnectAsync(connectorKind, name.Text, token.Password, _services.ActiveSpace.Id);
            token.Password = ""; await ReloadAsync(); Show("连接器已连接。请在专家页面授权给需要的专家。", InfoBarSeverity.Success);
        }
        catch (Exception error) { token.Password = ""; Show(error.Message, InfoBarSeverity.Error); }
    }

    private async void OnTestConnector(object sender, RoutedEventArgs e)
    {
        if (ConnectorList.SelectedItem is not ConnectorAccount item) return;
        try { var identity = await _services.Connectors.TestAsync(item.Id); await ReloadAsync(); Show("连接成功：" + identity, InfoBarSeverity.Success); }
        catch (Exception error) { Show(error.Message, InfoBarSeverity.Error); }
    }
    private async void OnToggleConnector(object sender, RoutedEventArgs e)
    {
        if (ConnectorList.SelectedItem is not ConnectorAccount item) return;
        await _services.Connectors.SetEnabledAsync(item.Id, item.State == "disabled"); await ReloadAsync();
    }
    private async void OnDeleteConnector(object sender, RoutedEventArgs e)
    {
        if (ConnectorList.SelectedItem is not ConnectorAccount item) return;
        if (!await ConfirmAsync("删除连接器？", $"将删除 {item.DisplayName} 的本地凭据和所有授权。")) return;
        await _services.Connectors.DeleteAsync(item.Id); await ReloadAsync();
    }

    private async void OnAddMcp(object sender, RoutedEventArgs e)
    {
        var type = new ComboBox { Header = "传输", ItemsSource = new[] { "本地 stdio", "Streamable HTTP" }, SelectedIndex = 0 };
        var name = new TextBox { Header = "服务名称", MaxLength = 80 };
        var executable = new TextBox { Header = "EXE 绝对路径" }; var pick = new Button { Content = "选择 EXE" };
        pick.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".exe"); InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
            var file = await picker.PickSingleFileAsync(); if (file is not null) executable.Text = file.Path;
        };
        var arguments = new TextBox { Header = "参数（每行一个参数）", AcceptsReturn = true, MaxHeight = 120 };
        var endpoint = new TextBox { Header = "Endpoint URL", PlaceholderText = "https://example.com/mcp" };
        var localMode = new CheckBox { Content = "本地模式（只允许 loopback HTTP）" };
        var oauth = new CheckBox { Content = "使用 OAuth 2.1 + PKCE（远程服务）" };
        var token = new PasswordBox { Header = "Bearer token（可选）", PasswordRevealMode = PasswordRevealMode.Peek };
        var panel = new StackPanel { Spacing = 10, MinWidth = 560 };
        foreach (var control in new Control[] { type, name, executable, pick, arguments, endpoint, localMode, oauth, token }) panel.Children.Add(control);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "添加 MCP 服务", Content = new ScrollViewer { Content = panel, MaxHeight = 620 },
            PrimaryButtonText = "初始化、审查并保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var expert = await ResolveExpertAsync(); var isStdio = type.SelectedIndex == 0; var bearer = token.Password;
            if (!isStdio && oauth.IsChecked == true)
            {
                using var oauthService = new Services.McpOAuthService();
                bearer = (await oauthService.AuthenticateAsync(endpoint.Text, localMode.IsChecked == true, CancellationToken.None)).AccessToken;
            }
            var draft = new McpServerDraft(name.Text, isStdio ? "stdio" : "streamable_http", executable.Text,
                arguments.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), null,
                endpoint.Text, localMode.IsChecked == true, bearer);
            var saved = await _services.Mcp.AddAsync(draft, _services.ActiveSpace.Id, expert.Id); token.Password = "";
            var capabilities = await _services.Mcp.GetCapabilitiesAsync(saved.Id);
            var review = new ListView { ItemsSource = capabilities, DisplayMemberPath = "ReviewLabel",
                SelectionMode = ListViewSelectionMode.Multiple, MinWidth = 680, MaxHeight = 420 };
            var reviewDialog = new ContentDialog { XamlRoot = XamlRoot, Title = "审查并批准 MCP 能力",
                Content = new StackPanel { Spacing = 10, Children =
                {
                    new TextBlock { Text = "默认不授权。仅勾选你信任且确实需要的能力；以后 Schema 变化会自动暂停授权。", TextWrapping = TextWrapping.Wrap },
                    review
                } },
                PrimaryButtonText = "批准所选", CloseButtonText = "暂不授权", DefaultButton = ContentDialogButton.Close };
            if (await reviewDialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var approved = review.SelectedItems.Cast<McpCapability>().Select(x => x.Id).ToList();
                await _services.Mcp.ApproveCapabilitiesAsync(saved.Id, approved);
                await _services.Mcp.GrantCapabilitiesAsync(expert.Id, saved.Id, approved);
            }
            await ReloadAsync(); Show("MCP 服务已保存；只有明确批准的能力可用。", InfoBarSeverity.Success);
        }
        catch (Exception error) { token.Password = ""; Show(error.Message, InfoBarSeverity.Error); }
    }

    private async void OnRefreshMcp(object sender, RoutedEventArgs e)
    {
        if (McpList.SelectedItem is not McpServer item) return;
        try
        {
            var capabilities = await _services.Mcp.ConnectAndDiscoverAsync(item.Id);
            var pending = capabilities.Where(x => x.Status is "discovered" or "stale").ToList();
            if (pending.Count > 0 && await ConfirmAsync("能力已变化", $"发现 {pending.Count} 项新增或 Schema 已变化的能力。批准后才可由当前已授权专家使用。"))
            {
                await _services.Mcp.ApproveCapabilitiesAsync(item.Id, pending.Select(x => x.Id).ToList());
                var expert = await ResolveExpertAsync();
                var approved = (await _services.Mcp.GetCapabilitiesAsync(item.Id)).Where(x => x.Status == "approved").Select(x => x.Id).ToList();
                await _services.Mcp.GrantCapabilitiesAsync(expert.Id, item.Id, approved);
            }
            await ReloadAsync(); Show($"已同步 {capabilities.Count} 项能力", InfoBarSeverity.Success);
        }
        catch (Exception error) { Show(error.Message, InfoBarSeverity.Error); }
    }
    private async void OnToggleMcp(object sender, RoutedEventArgs e)
    {
        if (McpList.SelectedItem is not McpServer item) return; await _services.Mcp.SetEnabledAsync(item.Id, !item.Enabled); await ReloadAsync();
    }
    private async void OnDeleteMcp(object sender, RoutedEventArgs e)
    {
        if (McpList.SelectedItem is not McpServer item || !await ConfirmAsync("删除 MCP 服务？", "将停止本地进程/远程会话并撤销全部授权。")) return;
        await _services.Mcp.DeleteAsync(item.Id); await ReloadAsync();
    }
    private async void OnMcpSelected(object sender, SelectionChangedEventArgs e)
    {
        if (McpList.SelectedItem is not McpServer item) return;
        try { var count = (await _services.Mcp.GetCapabilitiesAsync(item.Id)).Count; Show($"{item.DisplayName} · {item.NegotiatedProtocol ?? "未协商"} · {count} 项能力", InfoBarSeverity.Informational); }
        catch (Exception error) { Show(error.Message, InfoBarSeverity.Error); }
    }

    private async Task<Expert> ResolveExpertAsync()
    {
        if (_services.Settings.ActiveExpertId is { } id && await _services.Experts.GetAsync(id) is { } selected) return selected;
        return (await _services.Experts.ListAsync()).First();
    }
    private async Task<bool> ConfirmAsync(string title, string content) => await new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = content, PrimaryButtonText = "确认", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close }.ShowAsync() == ContentDialogResult.Primary;
    private void Show(string message, InfoBarSeverity severity) { InfoBar.Message = message; InfoBar.Severity = severity; InfoBar.IsOpen = true; }
}
