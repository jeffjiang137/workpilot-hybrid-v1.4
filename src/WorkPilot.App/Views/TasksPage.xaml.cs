using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WorkPilot.Models;
using Windows.ApplicationModel.DataTransfer;

namespace WorkPilot.Views;

public sealed partial class TasksPage : Page
{
    private readonly Services.AppServices _services = App.Services;
    private IReadOnlyList<WorkTask> _items = [];
    private IReadOnlyList<Project> _projects = [];
    private CancellationTokenSource? _searchCancellation;
    private bool _loading;
    private readonly LinkedList<(WorkTask Item, string PreviousStatus, DateTimeOffset CreatedAt)> _undo = [];

    public TasksPage()
    {
        InitializeComponent(); Loaded += async (_, _) => await LoadAsync();
        Unloaded += (_, _) => _searchCancellation?.Cancel();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            SpaceName.Text = _services.ActiveSpace.Name;
            _projects = await _services.Projects.GetBySpaceAsync(_services.ActiveSpace.Id);
            ProjectFilter.Items.Clear(); ProjectFilter.Items.Add(new ComboBoxItem { Content = "全部项目", Tag = null });
            foreach (var item in _projects) ProjectFilter.Items.Add(new ComboBoxItem { Content = item.Name, Tag = item.Id });
            ProjectFilter.SelectedIndex = 0; PriorityFilter.SelectedIndex = 0;
            ViewToggle.IsChecked = _services.Settings.TaskView == "list"; ApplyView(); await ReloadAsync();
        }
        catch (Exception error) { ShowError(error); }
        finally { _loading = false; }
    }

    private async Task ReloadAsync()
    {
        _items = await _services.Tasks.QueryAsync(_services.ActiveSpace.Id, SearchBox.Text);
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        var project = (ProjectFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        var priority = (PriorityFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        var filtered = _items.Where(x => (project is null || x.ProjectId == project) &&
            (string.IsNullOrEmpty(priority) || x.Priority == priority)).ToArray();
        BacklogList.ItemsSource = filtered.Where(x => x.Status == "backlog"); TodoList.ItemsSource = filtered.Where(x => x.Status == "todo");
        ProgressList.ItemsSource = filtered.Where(x => x.Status == "in_progress"); BlockedList.ItemsSource = filtered.Where(x => x.Status == "blocked");
        DoneList.ItemsSource = filtered.Where(x => x.Status == "done"); TaskList.ItemsSource = filtered;
    }

    private async void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return; _searchCancellation?.Cancel(); _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        try { await Task.Delay(250, _searchCancellation.Token); await ReloadAsync(); }
        catch (OperationCanceledException) { _searchCancellation = null; }
        catch (Exception error) { ShowError(error); }
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) { if (!_loading) ApplyFilters(); }
    private async void OnToggleView(object sender, RoutedEventArgs e)
    {
        ApplyView();
        await _services.SaveSettingsAsync(_services.Settings with { TaskView = ViewToggle.IsChecked == true ? "list" : "board" });
    }
    private void ApplyView()
    {
        var list = ViewToggle.IsChecked == true; BoardScroll.Visibility = list ? Visibility.Collapsed : Visibility.Visible;
        TaskList.Visibility = list ? Visibility.Visible : Visibility.Collapsed; ViewToggle.Content = list ? "看板视图" : "列表视图";
    }

    private async void OnNew(object sender, RoutedEventArgs e) => await EditAsync(null);
    private async void OnTaskClick(object sender, ItemClickEventArgs e) { if (e.ClickedItem is WorkTask item) await EditAsync(item); }

    private void OnDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is WorkTask item) { e.Data.SetText(item.Id); e.Data.RequestedOperation = DataPackageOperation.Move; }
        else e.Cancel = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Move; e.DragUIOverride.Caption = "移动任务";
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (sender is not ListView target || target.Tag is not string status || !e.DataView.Contains(StandardDataFormats.Text)) return;
        try
        {
            var id = await e.DataView.GetTextAsync(); var item = _items.FirstOrDefault(x => x.Id == id);
            if (item is null || item.Status == status) return;
            var updated = await _services.Tasks.ChangeStatusAsync(item, status);
            _undo.AddFirst((updated, item.Status, DateTimeOffset.UtcNow));
            while (_undo.Count > 20) _undo.RemoveLast(); UndoButton.IsEnabled = true; await ReloadAsync();
        }
        catch (Exception error) { ShowError(error); await ReloadAsync(); }
    }

    private async void OnUndo(object sender, RoutedEventArgs e)
    {
        while (_undo.Last is not null && DateTimeOffset.UtcNow - _undo.Last.Value.CreatedAt > TimeSpan.FromMinutes(10)) _undo.RemoveLast();
        if (_undo.First is null) { UndoButton.IsEnabled = false; return; }
        var move = _undo.First.Value; _undo.RemoveFirst();
        try { await _services.Tasks.ChangeStatusAsync(move.Item, move.PreviousStatus); await ReloadAsync(); }
        catch (Exception error) { ShowError(error); await ReloadAsync(); }
        UndoButton.IsEnabled = _undo.Count > 0;
    }

    private async Task EditAsync(WorkTask? existing)
    {
        var title = new TextBox { Header = "标题", Text = existing?.Title ?? "", MaxLength = 120 };
        var status = Combo("状态", new[] { ("待规划", "backlog"), ("待处理", "todo"), ("进行中", "in_progress"), ("已阻塞", "blocked"), ("已完成", "done"), ("已取消", "cancelled") }, existing?.Status ?? "todo");
        var priority = Combo("优先级", new[] { ("低", "low"), ("普通", "normal"), ("高", "high"), ("紧急", "urgent") }, existing?.Priority ?? "normal");
        var project = Combo("项目", _projects.Select(x => (x.Name, x.Id)).Prepend(("不关联项目", "")).ToArray(), existing?.ProjectId ?? "");
        var due = new CalendarDatePicker { Header = "截止日期", Date = existing?.DueDate is null ? null : new DateTimeOffset(existing.DueDate.Value.ToDateTime(TimeOnly.MinValue)) };
        var description = new TextBox { Header = "描述", Text = existing?.Description ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 130, MaxLength = 10_000 };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var start = new Button { Content = existing?.MainConversationId is null ? "开始任务" : "继续任务", IsEnabled = existing is not null };
        var delete = new Button { Content = "删除", IsEnabled = existing is not null }; actions.Children.Add(delete); actions.Children.Add(start);
        var panel = new StackPanel { Spacing = 10, MinWidth = 480 };
        foreach (var child in new UIElement[] { title, status, priority, project, due, description, actions }) panel.Children.Add(child);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = existing is null ? "新建任务" : "任务详情", Content = panel,
            PrimaryButtonText = "保存", CloseButtonText = "关闭", DefaultButton = ContentDialogButton.Primary };
        var closeWithoutSave = false; var startRequested = false; var deleteRequested = false;
        start.Click += (_, _) => { startRequested = true; closeWithoutSave = true; dialog.Hide(); };
        delete.Click += (_, _) => { deleteRequested = true; closeWithoutSave = true; dialog.Hide(); };
        var result = await dialog.ShowAsync();
        try
        {
            if (closeWithoutSave)
            {
                if (deleteRequested && existing is not null) await _services.Tasks.DeleteAsync(existing);
                if (startRequested && existing is not null) await StartTaskAsync(existing);
                await ReloadAsync(); return;
            }
            if (result != ContentDialogResult.Primary) return;
            var now = DateTimeOffset.UtcNow; var targetStatus = SelectedTag(status);
            if (existing is not null) Services.TaskRules.ValidateTransition(existing.Status, targetStatus);
            DateTimeOffset? completed = targetStatus == "done" ? existing?.CompletedAt ?? now : null;
            var item = new WorkTask(existing?.Id ?? Guid.NewGuid().ToString("N"), _services.ActiveSpace.Id,
                NullIfEmpty(SelectedTag(project)), existing?.MainConversationId, title.Text.Trim(), description.Text,
                targetStatus, SelectedTag(priority), due.Date is null ? null : DateOnly.FromDateTime(due.Date.Value.DateTime),
                existing?.SortKey ?? Services.TaskRules.NextSortKey(_items, targetStatus), completed,
                existing?.CreatedAt ?? now, now, existing?.RowVersion ?? 1);
            await _services.Tasks.SaveAsync(item); await ReloadAsync();
        }
        catch (Exception error) { ShowError(error); }
    }

    private async Task StartTaskAsync(WorkTask item)
    {
        var conversation = await _services.Tasks.EnsureConversationAsync(item);
        var project = _projects.FirstOrDefault(x => x.Id == item.ProjectId);
        var prompt = $"任务：{item.Title}\n\n描述：{item.Description}" +
            (project is null ? "" : $"\n\n项目说明：{project.Instructions}");
        _services.OpenConversationDraft(conversation.Id, prompt);
        if (App.MainAppWindow is MainWindow window) window.NavigateToChat();
    }

    private static ComboBox Combo(string header, IReadOnlyList<(string Label, string Value)> items, string selected)
    {
        var combo = new ComboBox { Header = header };
        foreach (var item in items) combo.Items.Add(new ComboBoxItem { Content = item.Label, Tag = item.Value });
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().First(x => (string)x.Tag == selected); return combo;
    }
    private static string SelectedTag(ComboBox combo) => (string)((ComboBoxItem)combo.SelectedItem).Tag;
    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
    private void ShowError(Exception error) { ErrorBar.Message = error.Message; ErrorBar.IsOpen = true; }
}
