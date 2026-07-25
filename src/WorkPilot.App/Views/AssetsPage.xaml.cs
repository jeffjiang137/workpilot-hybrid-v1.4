using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WorkPilot.Models;

namespace WorkPilot.Views;

public sealed partial class AssetsPage : Page
{
    private readonly Services.AppServices _services = App.Services;
    private IReadOnlyList<Project> _projects = [];
    private CancellationTokenSource? _queryCancellation;
    private long _querySequence;
    private bool _loading;
    private AssetSearchResult? _selected;

    public AssetsPage()
    {
        InitializeComponent(); Loaded += async (_, _) => await LoadAsync();
        Unloaded += (_, _) => _queryCancellation?.Cancel();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            SpaceName.Text = _services.ActiveSpace.Name;
            _projects = await _services.Projects.GetBySpaceAsync(_services.ActiveSpace.Id);
            ProjectFilter.Items.Add(new ComboBoxItem { Content = "全部项目", Tag = null });
            foreach (var project in _projects) ProjectFilter.Items.Add(new ComboBoxItem { Content = project.Name, Tag = project.Id });
            ProjectFilter.SelectedIndex = 0; CategoryFilter.SelectedIndex = 0; StatusFilter.SelectedIndex = 0;
            await SearchAsync(); await RefreshIndexStatusAsync();
        }
        catch (Exception error) { ShowError(error); }
        finally { _loading = false; }
    }

    private async void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return; _queryCancellation?.Cancel(); _queryCancellation?.Dispose();
        _queryCancellation = new CancellationTokenSource();
        try { await Task.Delay(250, _queryCancellation.Token); await SearchAsync(_queryCancellation.Token); }
        catch (OperationCanceledException) { _queryCancellation = null; }
        catch (Exception error) { ShowError(error); }
    }

    private async void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        try { await SearchAsync(); await RefreshIndexStatusAsync(); }
        catch (Exception error) { ShowError(error); }
    }

    private async Task SearchAsync(CancellationToken cancellationToken = default)
    {
        var sequence = Interlocked.Increment(ref _querySequence);
        var project = (ProjectFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        var category = NullIfEmpty((CategoryFilter.SelectedItem as ComboBoxItem)?.Tag as string);
        var status = NullIfEmpty((StatusFilter.SelectedItem as ComboBoxItem)?.Tag as string);
        var results = await _services.AssetSearch.SearchAsync(new(_services.ActiveSpace.Id, SearchBox.Text,
            project, category, status), cancellationToken);
        if (sequence == Interlocked.Read(ref _querySequence)) ResultList.ItemsSource = results;
    }

    private async void OnResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not AssetSearchResult result) return;
        _selected = result;
        try
        {
            var preview = await _services.AssetSearch.GetPreviewAsync(result.AssetId);
            PreviewTitle.Text = preview.FileName;
            PreviewMeta.Text = $"{preview.ProjectName} · {preview.RelativePath}\n{preview.SizeBytes:N0} 字节 · {preview.TextStatus}";
            PreviewText.Text = preview.Content + (preview.Truncated ? "\n\n[预览已截断]" : "");
        }
        catch (Exception error) { ShowError(error); }
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage(); package.SetText(_selected.RelativePath);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package); IndexStatus.Text = "已复制相对路径";
    }

    private async void OnAddToChat(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try
        {
            var prompt = await _services.AssetSearch.BuildUserReferenceAsync(_selected.AssetId);
            var conversation = await _services.Database.EnsureConversationAsync(_services.ActiveSpace.Id, _selected.ProjectId);
            _services.OpenConversationDraft(conversation.Id, prompt);
            if (App.MainAppWindow is MainWindow window) window.NavigateToChat();
        }
        catch (Exception error) { ShowError(error); }
    }

    private async void OnIndex(object sender, RoutedEventArgs e)
    {
        var project = SelectedProject();
        if (project is null) { ShowError(new InvalidOperationException("请先在项目筛选器中选择一个项目")); return; }
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "重新构建资产索引？",
            Content = "将重新扫描项目并更新本地索引，不会修改或删除工作区文件。", PrimaryButtonText = "开始",
            CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            IndexButton.IsEnabled = false; IndexStatus.Text = "正在扫描…";
            await _services.AssetIndex.RequestFullScanAsync(project); await SearchAsync(); await RefreshIndexStatusAsync();
        }
        catch (Exception error) { ShowError(error); }
        finally { IndexButton.IsEnabled = true; }
    }

    private async void OnPause(object sender, RoutedEventArgs e)
    {
        var project = SelectedProject(); if (project is null) return;
        try { await _services.AssetIndex.PauseAsync(project.Id); await RefreshIndexStatusAsync(); }
        catch (Exception error) { ShowError(error); }
    }

    private async Task RefreshIndexStatusAsync()
    {
        var project = SelectedProject();
        if (project is null) { IndexStatus.Text = _projects.Count == 0 ? "当前空间没有项目" : "选择项目查看索引状态"; return; }
        var state = await _services.AssetIndex.GetStateAsync(project.Id);
        IndexStatus.Text = state is null ? "尚未索引" : $"{state.Status} · {state.ProcessedCount}/{state.DiscoveredCount} · 正文 {state.IndexedTextCount}";
    }

    private Project? SelectedProject()
    {
        var id = (ProjectFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        return _projects.FirstOrDefault(x => x.Id == id);
    }
    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
    private void ShowError(Exception error) { ErrorBar.Message = error.Message; ErrorBar.IsOpen = true; }
}
