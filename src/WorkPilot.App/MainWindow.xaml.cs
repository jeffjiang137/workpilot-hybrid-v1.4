using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WorkPilot.Models;
using WorkPilot.Views;

namespace WorkPilot;

public sealed partial class MainWindow : Window
{
    private bool _loadingSpaces;

    public MainWindow()
    {
        InitializeComponent(); Title = "WorkPilot";
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 800));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 1024; presenter.PreferredMinimumHeight = 720;
        }
        Nav.SelectedItem = Nav.MenuItems[0]; ContentFrame.Navigate(typeof(ChatPage));
        Activated += async (_, _) => { if (SpacePicker.Items.Count == 0) await ReloadSpacesAsync(); };
    }

    private async Task ReloadSpacesAsync(string? selectId = null)
    {
        _loadingSpaces = true;
        try
        {
            var spaces = await App.Services.Spaces.ListAsync(); SpacePicker.ItemsSource = spaces;
            SpacePicker.SelectedItem = spaces.FirstOrDefault(x => x.Id == (selectId ?? App.Services.ActiveSpace.Id)) ?? spaces.First();
        }
        finally { _loadingSpaces = false; }
    }

    private async void OnSpaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSpaces || SpacePicker.SelectedItem is not Space space || space.Id == App.Services.ActiveSpace.Id) return;
        try { SpacePicker.IsEnabled = false; await App.Services.SetActiveSpaceAsync(space); RefreshCurrentPage(); }
        catch (Exception error) { await ShowErrorAsync("切换空间失败", error); await ReloadSpacesAsync(App.Services.ActiveSpace.Id); }
        finally { SpacePicker.IsEnabled = true; }
    }

    private async void OnManageSpaces(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog { XamlRoot = Nav.XamlRoot, Title = "管理空间", CloseButtonText = "完成" };
        var list = new ListView { MinWidth = 620, MaxHeight = 420, DisplayMemberPath = "DisplayName" };
        var create = new Button { Content = "新建空间" }; var edit = new Button { Content = "编辑" };
        var archive = new Button { Content = "归档 / 恢复" }; var delete = new Button { Content = "删除空空间" };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(create); actions.Children.Add(edit); actions.Children.Add(archive); actions.Children.Add(delete);
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(list); panel.Children.Add(actions); dialog.Content = panel;
        async Task ReloadListAsync() { list.ItemsSource = await App.Services.Spaces.ListAsync(true); }
        create.Click += async (_, _) => { dialog.Hide(); await EditSpaceAsync(null); };
        edit.Click += async (_, _) => { if (list.SelectedItem is Space item) { dialog.Hide(); await EditSpaceAsync(item); } };
        archive.Click += async (_, _) =>
        {
            if (list.SelectedItem is not Space item) return;
            dialog.Hide();
            try
            {
                await App.Services.Spaces.ArchiveAsync(item, !item.IsArchived);
                if (!item.IsArchived && item.Id == App.Services.ActiveSpace.Id)
                {
                    var fallback = (await App.Services.Spaces.ListAsync()).First(x => x.Id != item.Id);
                    await App.Services.SetActiveSpaceAsync(fallback);
                }
                await ReloadListAsync();
            }
            catch (Exception error) { await ShowErrorAsync("空间操作失败", error); }
        };
        delete.Click += async (_, _) =>
        {
            if (list.SelectedItem is not Space item) return;
            dialog.Hide();
            try { await App.Services.Spaces.DeleteEmptyAsync(item); await ReloadListAsync(); }
            catch (Exception error) { await ShowErrorAsync("无法删除空间", error); }
        };
        await ReloadListAsync(); await dialog.ShowAsync(); await ReloadSpacesAsync(App.Services.ActiveSpace.Id);
    }

    private async Task<Space?> EditSpaceAsync(Space? existing)
    {
        var name = new TextBox { Header = "名称", Text = existing?.Name ?? "", MaxLength = 40 };
        var description = new TextBox { Header = "描述", Text = existing?.Description ?? "", MaxLength = 500, AcceptsReturn = true };
        var color = new ComboBox { Header = "颜色", ItemsSource = new[] { "green", "blue", "cyan", "violet", "amber", "orange", "rose", "slate" }, SelectedItem = existing?.ColorToken ?? "green" };
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(name); panel.Children.Add(description); panel.Children.Add(color);
        var dialog = new ContentDialog { XamlRoot = Nav.XamlRoot, Title = existing is null ? "新建空间" : "编辑空间",
            Content = panel, PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        try
        {
            var result = existing is null
                ? await App.Services.Spaces.CreateAsync(name.Text, description.Text, color.SelectedItem?.ToString() ?? "green")
                : await App.Services.Spaces.UpdateAsync(existing, name.Text, description.Text, color.SelectedItem?.ToString() ?? "green");
            if (existing is null) await App.Services.SetActiveSpaceAsync(result);
            return result;
        }
        catch (Exception error) { await ShowErrorAsync("保存空间失败", error); return null; }
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag) return;
        var page = tag switch { "tasks" => typeof(TasksPage), "projects" => typeof(ProjectsPage),
            "assets" => typeof(AssetsPage), "experts" => typeof(ExpertsPage), "skills" => typeof(SkillsPage),
            "connections" => typeof(ConnectionsPage), "security" => typeof(SecurityCenterPage),
            "automations" => typeof(AutomationsPage),
            "settings" => typeof(SettingsPage), _ => typeof(ChatPage) };
        if (ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page);
    }

    public void NavigateToChat()
    {
        Nav.SelectedItem = Nav.MenuItems[0]; ContentFrame.Navigate(typeof(ChatPage));
    }

    private void RefreshCurrentPage() { var type = ContentFrame.CurrentSourcePageType; if (type is not null) ContentFrame.Navigate(type); }
    private async Task ShowErrorAsync(string title, Exception error) => await new ContentDialog
        { XamlRoot = Nav.XamlRoot, Title = title, Content = error.Message, CloseButtonText = "知道了" }.ShowAsync();
}
