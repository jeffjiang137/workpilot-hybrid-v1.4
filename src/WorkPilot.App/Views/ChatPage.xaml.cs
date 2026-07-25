using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WorkPilot.Models;

namespace WorkPilot.Views;

public sealed partial class ChatPage : Page
{
    private readonly Services.AppServices _services = App.Services;
    private Conversation? _conversation;
    private IReadOnlyList<Project> _projects = [];
    private IReadOnlyList<Expert> _experts = [];
    private CancellationTokenSource? _runCancellation;
    private TextBlock? _streamingText;
    private bool _loading;

    public ChatPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _runCancellation?.Cancel();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        try
        {
            PermissionPicker.SelectedIndex = Math.Clamp(_services.Settings.PermissionMode, 0, 2);
            _experts = await _services.Experts.ListAsync(); ExpertPicker.ItemsSource = _experts;
            ExpertPicker.SelectedItem = _experts.FirstOrDefault(x => x.Id == _services.Settings.ActiveExpertId) ?? _experts.First();
            await ReloadProjectsAsync();
            var pending = _services.ConsumeConversationDraft();
            await ReloadConversationsAsync(pending.ConversationId);
            if (!string.IsNullOrWhiteSpace(pending.Prompt)) PromptBox.Text = pending.Prompt;
        }
        catch (Exception error) { await ShowErrorAsync(error); }
        finally { _loading = false; }
    }

    private async Task ReloadProjectsAsync()
    {
        _projects = await _services.Projects.GetBySpaceAsync(_services.ActiveSpace.Id);
        ProjectPicker.Items.Clear();
        ProjectPicker.Items.Add(new ComboBoxItem { Content = "不使用项目", Tag = null });
        foreach (var project in _projects) ProjectPicker.Items.Add(new ComboBoxItem { Content = project.Name, Tag = project.Id });
        var index = _services.Settings.ActiveProjectId is null ? 0 :
            Math.Max(0, _projects.ToList().FindIndex(x => x.Id == _services.Settings.ActiveProjectId) + 1);
        ProjectPicker.SelectedIndex = index;
    }

    private async Task ReloadConversationsAsync(string? selectId = null)
    {
        var conversations = await _services.Database.GetConversationsAsync(_services.ActiveSpace.Id);
        if (conversations.Count == 0) conversations = [await _services.Database.EnsureConversationAsync(_services.ActiveSpace.Id)];
        ConversationPicker.Items.Clear();
        foreach (var item in conversations) ConversationPicker.Items.Add(new ComboBoxItem { Content = item.Title, Tag = item.Id });
        var index = selectId is null ? 0 : Math.Max(0, conversations.ToList().FindIndex(x => x.Id == selectId));
        ConversationPicker.SelectedIndex = index;
        _conversation = conversations[index];
        await LoadMessagesAsync();
    }

    private async Task LoadMessagesAsync()
    {
        MessageList.Children.Clear();
        if (_conversation is null) return;
        foreach (var message in await _services.Database.GetMessagesAsync(_conversation.Id))
            AddMessage(message.Role, message.Content);
        if (MessageList.Children.Count == 0) AddWelcome();
        ScrollToBottom();
    }

    private void AddWelcome()
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 100, 0, 20), Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "今天想完成什么？", FontSize = 32, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = "我可以阅读项目文件、回答问题，并在你确认后安全地修改文件。", Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 168, 168, 168)) });
        MessageList.Children.Add(panel);
    }

    private TextBlock AddMessage(string role, string content)
    {
        if (MessageList.Children.Count == 1 && MessageList.Children[0] is StackPanel) MessageList.Children.Clear();
        var text = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var border = new Border
        {
            Background = new SolidColorBrush(role == "user" ? Windows.UI.Color.FromArgb(255, 36, 54, 47) : Windows.UI.Color.FromArgb(255, 31, 31, 31)),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 52, 52, 52)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(14), MaxWidth = 720,
            HorizontalAlignment = role == "user" ? HorizontalAlignment.Right : HorizontalAlignment.Left, Child = text
        };
        MessageList.Children.Add(border);
        return text;
    }

    private async void OnSend(object sender, RoutedEventArgs e) => await SendAsync();

    private async Task SendAsync()
    {
        var prompt = PromptBox.Text.Trim();
        if (prompt.Length == 0 || _conversation is null || _runCancellation is not null) return;
        PromptBox.Text = "";
        AddMessage("user", prompt);
        _streamingText = AddMessage("assistant", "");
        SetRunning(true);
        _runCancellation = new CancellationTokenSource();
        try
        {
            var project = _projects.FirstOrDefault(x => x.Id == _services.Settings.ActiveProjectId);
            var progress = new Progress<AgentEvent>(HandleAgentEvent);
            var expert = ExpertPicker.SelectedItem as Expert;
            await _services.Agent.RunAsync(new(_conversation.Id, prompt, project, _services.Settings,
                expert?.Id, _services.ActiveSpace.Id), progress,
                ConfirmToolAsync, _runCancellation.Token);
            await ReloadConversationsAsync(_conversation.Id);
        }
        catch (OperationCanceledException) { StatusText.Text = "已停止"; if (_streamingText is not null) _streamingText.Text += "\n\n[已停止]"; }
        catch (Exception error) { await ShowErrorAsync(error); if (_streamingText is not null && _streamingText.Text.Length == 0) _streamingText.Text = "执行失败。"; }
        finally { _runCancellation.Dispose(); _runCancellation = null; _streamingText = null; SetRunning(false); }
    }

    private void HandleAgentEvent(AgentEvent value)
    {
        if (value.Kind == "delta" && _streamingText is not null) _streamingText.Text += value.Text;
        else if (value.Kind is "state" or "tool") StatusText.Text = value.Text;
        ScrollToBottom();
    }

    private async Task<bool> ConfirmToolAsync(AgentEvent request)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = request.Text,
            Content = $"能力：{request.ToolName}\n\n将发送的参数摘要：\n{SafeArgumentPreview(request.ToolArguments ?? "") }\n\n只授权本次调用；秘密字段不会在此显示。",
            PrimaryButtonText = "仅本次允许", CloseButtonText = "拒绝", DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OnNewConversation(object sender, RoutedEventArgs e)
    {
        try { var item = await _services.Database.EnsureConversationAsync(_services.ActiveSpace.Id); await ReloadConversationsAsync(item.Id); }
        catch (Exception error) { await ShowErrorAsync(error); }
    }

    private async void OnConversationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ConversationPicker.SelectedItem is not ComboBoxItem item || item.Tag is not string id) return;
        _conversation = (await _services.Database.GetConversationsAsync(_services.ActiveSpace.Id)).FirstOrDefault(x => x.Id == id);
        await LoadMessagesAsync();
    }

    private async void OnProjectChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ProjectPicker.SelectedItem is not ComboBoxItem item) return;
        await _services.SaveSettingsAsync(_services.Settings with { ActiveProjectId = item.Tag as string });
    }

    private async void OnPermissionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PermissionPicker.SelectedIndex < 0) return;
        await _services.SaveSettingsAsync(_services.Settings with { PermissionMode = PermissionPicker.SelectedIndex });
    }

    private async void OnExpertChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ExpertPicker.SelectedItem is not Expert expert) return;
        await _services.SaveSettingsAsync(_services.Settings with { ActiveExpertId = expert.Id });
        StatusText.Text = $"专家：{expert.Name} · 修订 {expert.RevisionNumber}";
    }

    private async void OnPromptKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        if (e.Key == VirtualKey.Enter && state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) { e.Handled = true; await SendAsync(); }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _runCancellation?.Cancel();
    private void SetRunning(bool value) { SendButton.IsEnabled = !value; ProjectPicker.IsEnabled = !value; ExpertPicker.IsEnabled = !value; PermissionPicker.IsEnabled = !value; CancelButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed; if (!value && StatusText.Text != "已停止") StatusText.Text = ExpertPicker.SelectedItem is Expert expert ? $"专家：{expert.Name} · 修订 {expert.RevisionNumber}" : "准备就绪"; }
    private void ScrollToBottom() => MessageScroll.ChangeView(null, MessageScroll.ScrollableHeight, null, true);
    private async Task ShowErrorAsync(Exception error) { Services.AppLogger.Error("Chat operation failed", error); await new ContentDialog { XamlRoot = XamlRoot, Title = "操作失败", Content = error.Message, CloseButtonText = "知道了" }.ShowAsync(); }
    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max] + "…";
    private static string SafeArgumentPreview(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var safe = Redact(document.RootElement); return Limit(System.Text.Json.JsonSerializer.Serialize(safe), 900);
        }
        catch (System.Text.Json.JsonException) { return "[参数无法安全预览]"; }
    }
    private static object? Redact(System.Text.Json.JsonElement value) => value.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Object => value.EnumerateObject().ToDictionary(x => x.Name,
            x => IsSensitive(x.Name) ? (object?)"••••" : Redact(x.Value)),
        System.Text.Json.JsonValueKind.Array => value.EnumerateArray().Take(50).Select(Redact).ToList(),
        System.Text.Json.JsonValueKind.String => value.GetString(),
        System.Text.Json.JsonValueKind.Number => value.TryGetInt64(out var number) ? number : value.GetDouble(),
        System.Text.Json.JsonValueKind.True => true, System.Text.Json.JsonValueKind.False => false,
        System.Text.Json.JsonValueKind.Null => null, _ => value.ToString()
    };
    private static bool IsSensitive(string name) => new[] { "token", "secret", "password", "authorization", "cookie", "verifier", "api_key" }
        .Any(x => name.Contains(x, StringComparison.OrdinalIgnoreCase));
}
