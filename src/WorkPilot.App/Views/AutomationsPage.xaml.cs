using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WorkPilot.Models;

namespace WorkPilot.Views;

public sealed partial class AutomationsPage : Page
{
    private readonly Services.AppServices _services = App.Services;
    private Automation? _selected;

    public AutomationsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _services.Scheduler.AutomationChanged += OnAutomationChanged;
        await ReloadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _services.Scheduler.AutomationChanged -= OnAutomationChanged;
    private async void OnAutomationChanged(object? sender, EventArgs e) =>
        await Services.DispatcherQueueExtensions.EnqueueAsync(DispatcherQueue, () => ReloadAsync(_selected?.Id));

    private async Task ReloadAsync(string? selectId = null)
    {
        var items = await _services.Automations.GetAllAsync();
        AutomationList.ItemsSource = items;
        var item = selectId is null ? items.FirstOrDefault() : items.FirstOrDefault(x => x.Id == selectId);
        if (item is not null) AutomationList.SelectedItem = item; else NewEditor();
    }

    private void OnSelected(object sender, SelectionChangedEventArgs e)
    {
        if (AutomationList.SelectedItem is not Automation item) return;
        _selected = item; NameBox.Text = item.Name; PromptBox.Text = item.Prompt; IntervalBox.Value = item.IntervalMinutes;
        EnabledSwitch.IsOn = item.Enabled; DeleteButton.IsEnabled = true;
        RunInfo.Text = $"下次：{item.NextRunAt.ToLocalTime():g}　最近：{item.LastRunAt?.ToLocalTime().ToString("g") ?? "从未"}";
    }

    private void OnNew(object sender, RoutedEventArgs e) => NewEditor();
    private void NewEditor() { AutomationList.SelectedItem = null; _selected = null; NameBox.Text = ""; PromptBox.Text = ""; IntervalBox.Value = 60; EnabledSwitch.IsOn = true; DeleteButton.IsEnabled = false; RunInfo.Text = ""; }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = NameBox.Text.Trim(); var prompt = PromptBox.Text.Trim();
            if (name.Length is < 1 or > 80) throw new ArgumentException("名称需为 1–80 个字符");
            if (prompt.Length is < 1 or > 8000) throw new ArgumentException("任务指令需为 1–8000 个字符");
            var interval = (int)Math.Clamp(IntervalBox.Value, 1, 10080); var now = DateTimeOffset.UtcNow;
            var item = new Automation(_selected?.Id ?? Guid.NewGuid().ToString("N"), name, prompt, interval,
                EnabledSwitch.IsOn, _selected?.LastRunAt, now.AddMinutes(interval), _selected?.LastStatus ?? "等待运行");
            await _services.Automations.SaveAsync(item); await ReloadAsync(item.Id);
        }
        catch (Exception error) { await ShowErrorAsync(error); }
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "删除自动化？", Content = _selected.Name,
            PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await _services.Automations.DeleteAsync(_selected.Id); await ReloadAsync();
    }

    private async Task ShowErrorAsync(Exception error)
    {
        Services.AppLogger.Error("Automation operation failed", error);
        await new ContentDialog { XamlRoot = XamlRoot, Title = "自动化操作失败", Content = error.Message, CloseButtonText = "知道了" }.ShowAsync();
    }
}
