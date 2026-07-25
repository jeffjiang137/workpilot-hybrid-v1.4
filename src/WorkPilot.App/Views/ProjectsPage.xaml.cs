using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WorkPilot.Models;

namespace WorkPilot.Views;

public sealed partial class ProjectsPage : Page
{
    private readonly Services.AppServices _services = App.Services;
    private Project? _selected;

    public ProjectsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync(string? selectId = null)
    {
        var items = await _services.Projects.GetBySpaceAsync(_services.ActiveSpace.Id);
        ProjectList.ItemsSource = items;
        var selected = selectId is null ? items.FirstOrDefault() : items.FirstOrDefault(x => x.Id == selectId);
        if (selected is not null) ProjectList.SelectedItem = selected;
        else NewEditor();
    }

    private async void OnSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectList.SelectedItem is not Project item) return;
        _selected = item; NameBox.Text = item.Name; PathBox.Text = item.WorkspacePath;
        InstructionsBox.Text = item.Instructions; IgnoreRulesBox.Text = item.IgnoreRules;
        IncludeHiddenSwitch.IsOn = item.IncludeHidden; DeleteButton.IsEnabled = true;
        await RefreshIndexStatusAsync(item.Id);
    }

    private void OnNew(object sender, RoutedEventArgs e) => NewEditor();

    private void NewEditor()
    {
        ProjectList.SelectedItem = null; _selected = null; NameBox.Text = ""; PathBox.Text = "";
        InstructionsBox.Text = ""; IgnoreRulesBox.Text = ""; IncludeHiddenSwitch.IsOn = false;
        IndexStatusText.Text = "尚未索引"; DeleteButton.IsEnabled = false; NameBox.Focus(FocusState.Programmatic);
    }

    private async void OnPickFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null) PathBox.Text = folder.Path;
        }
        catch (Exception error) { await ShowErrorAsync(error); }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = NameBox.Text.Trim(); var path = PathBox.Text.Trim();
            if (name.Length is < 1 or > 80) throw new ArgumentException("项目名称需为 1–80 个字符");
            if (!Directory.Exists(path)) throw new ArgumentException("请选择存在的工作区目录");
            using (var session = _services.Native.Open(path)) { }
            var now = DateTimeOffset.UtcNow;
            var item = new Project(_selected?.Id ?? Guid.NewGuid().ToString("N"), _services.ActiveSpace.Id, name, path,
                InstructionsBox.Text.Trim(), IgnoreRulesBox.Text, IncludeHiddenSwitch.IsOn,
                _selected?.CreatedAt ?? now, now, _selected?.RowVersion ?? 1);
            item = await _services.Projects.SaveAsync(item);
            await _services.AssetIndex.QueueFullScanAsync(item);
            await ReloadAsync(item.Id);
        }
        catch (Exception error) { await ShowErrorAsync(error); }
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "删除项目？",
            Content = "只删除 WorkPilot 中的项目配置，不会删除本地文件。", PrimaryButtonText = "删除",
            CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await _services.Projects.DeleteAsync(_selected.Id);
        if (_services.Settings.ActiveProjectId == _selected.Id)
            await _services.SaveSettingsAsync(_services.Settings with { ActiveProjectId = null });
        _services.AssetIndex.RemoveProject(_selected.Id);
        await ReloadAsync();
    }

    private async void OnIndex(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try { IndexStatusText.Text = "正在扫描…"; await _services.AssetIndex.RequestFullScanAsync(_selected); await RefreshIndexStatusAsync(_selected.Id); }
        catch (Exception error) { await ShowErrorAsync(error); }
    }

    private async void OnPauseIndex(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try { await _services.AssetIndex.PauseAsync(_selected.Id); await RefreshIndexStatusAsync(_selected.Id); }
        catch (Exception error) { await ShowErrorAsync(error); }
    }

    private async Task RefreshIndexStatusAsync(string projectId)
    {
        var state = await _services.AssetIndex.GetStateAsync(projectId);
        IndexStatusText.Text = state is null ? "尚未索引" : $"{state.Status} · {state.ProcessedCount}/{state.DiscoveredCount} · 正文 {state.IndexedTextCount}";
    }

    private async Task ShowErrorAsync(Exception error)
    {
        Services.AppLogger.Error("Project operation failed", error);
        await new ContentDialog { XamlRoot = XamlRoot, Title = "项目操作失败", Content = error.Message, CloseButtonText = "知道了" }.ShowAsync();
    }
}
