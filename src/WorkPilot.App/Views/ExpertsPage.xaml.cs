using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WorkPilot.Models;

namespace WorkPilot.Views;

public sealed partial class ExpertsPage : Page
{
    private readonly Services.AppServices _services = App.Services;
    private IReadOnlyList<Expert> _items = []; private Expert? _selected;

    public ExpertsPage() { InitializeComponent(); Loaded += async (_, _) => await ReloadAsync(); }

    private async Task ReloadAsync(string? selectedId = null)
    {
        _items = await _services.Experts.ListAsync(true); ApplyFilter();
        var selected = _items.FirstOrDefault(x => x.Id == selectedId) ?? _items.FirstOrDefault();
        if (selected is not null) ExpertList.SelectedItem = selected;
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim(); ExpertList.ItemsSource = string.IsNullOrEmpty(query) ? _items :
            _items.Where(x => x.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                x.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private void OnSearch(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async void OnSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ExpertList.SelectedItem is not Expert expert) return; _selected = expert;
        NameText.Text = expert.DisplayName; DescriptionText.Text = expert.Description;
        RevisionText.Text = expert.RevisionNumber.ToString(); SkillCountText.Text = expert.SkillCount.ToString();
        ConnectionCountText.Text = expert.ConnectionCount.ToString();
        var revision = await _services.Experts.GetCurrentRevisionAsync(expert.Id);
        InstructionText.Text = string.IsNullOrWhiteSpace(revision.SystemInstruction) ? "未配置额外指令" : revision.SystemInstruction;
        EditButton.IsEnabled = CopyButton.IsEnabled = ArchiveButton.IsEnabled = true;
    }

    private async void OnNew(object sender, RoutedEventArgs e) => await EditAsync(null, null);
    private async void OnEdit(object sender, RoutedEventArgs e) { if (_selected is not null) await EditAsync(_selected, await _services.Experts.GetDraftAsync(_selected.Id)); }
    private async void OnCopy(object sender, RoutedEventArgs e) { if (_selected is null) return; var draft = await _services.Experts.GetDraftAsync(_selected.Id); await EditAsync(null, draft with { Name = draft.Name + " 副本" }); }

    private async Task EditAsync(Expert? existing, ExpertDraft? source)
    {
        try
        {
            var skills = (await _services.Skills.GetEnabledVersionsAsync()).Select(x => new SkillVersionChoice(x.VersionId, x.Skill)).ToList();
            var connectors = await _services.Connectors.ListAsync();
            var servers = await _services.Mcp.ListAsync(); source ??= new("", "", "green", "", "", [], [], [], RiskLevel.High);
            var name = new TextBox { Header = "名称", Text = source.Name, MaxLength = 60 };
            var description = new TextBox { Header = "描述", Text = source.Description, MaxLength = 400, AcceptsReturn = true };
            var model = new TextBox { Header = "模型偏好（留空使用全局模型）", Text = source.ModelPreference, MaxLength = 100 };
            var instruction = new TextBox { Header = "系统指令", Text = source.SystemInstruction, MaxLength = 32000, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 130 };
            var skillList = new ListView { Header = "技能（最多 20 个）", SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 150, DisplayMemberPath = "DisplayName" };
            skillList.ItemsSource = skills;
            var automaticSkills = new CheckBox { Content = "按当前消息自动选择所勾选技能（不勾选则全部固定启用）",
                IsChecked = source.SkillVersionIds.Count > 0 && source.SkillVersionIds.All(x =>
                    source.AutomaticSkillVersionIds?.Contains(x, StringComparer.Ordinal) == true) };
            var connectorList = new ListView { Header = "连接器账号", SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 120, DisplayMemberPath = "DisplayName", ItemsSource = connectors };
            var serverList = new ListView { Header = "MCP 服务", SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = 120, DisplayMemberPath = "DisplayName", ItemsSource = servers };
            foreach (var item in skills.Where(x => source.SkillVersionIds.Contains(x.VersionId))) skillList.SelectedItems.Add(item);
            foreach (var item in connectors.Where(x => source.ConnectorAccountIds.Contains(x.Id))) connectorList.SelectedItems.Add(item);
            foreach (var item in servers.Where(x => source.McpServerIds.Contains(x.Id))) serverList.SelectedItems.Add(item);
            var panel = new StackPanel { Spacing = 12, MinWidth = 620 };
            foreach (var control in new Control[] { name, description, model, instruction, skillList,
                automaticSkills, connectorList, serverList }) panel.Children.Add(control);
            var scroll = new ScrollViewer { Content = panel, MaxHeight = 620 };
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = existing is null ? "新建专家" : "编辑专家",
                Content = scroll, PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var selectedSkills = skillList.SelectedItems.Cast<SkillVersionChoice>().Select(x => x.VersionId).ToList();
            var draft = new ExpertDraft(name.Text, description.Text, "green", model.Text, instruction.Text,
                selectedSkills,
                connectorList.SelectedItems.Cast<ConnectorAccount>().Select(x => x.Id).ToList(),
                serverList.SelectedItems.Cast<McpServer>().Select(x => x.Id).ToList(), RiskLevel.High,
                automaticSkills.IsChecked == true ? selectedSkills : []);
            var saved = existing is null ? await _services.Experts.CreateAsync(draft) : await _services.Experts.UpdateAsync(existing, draft);
            await ReloadAsync(saved.Id);
        }
        catch (Exception error) { ShowError(error); }
    }

    private async void OnArchive(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try { await _services.Experts.ArchiveAsync(_selected, _selected.Status != "archived"); await ReloadAsync(); }
        catch (Exception error) { ShowError(error); }
    }

    private void ShowError(Exception error) { Services.AppLogger.Error("Expert operation failed", error); ErrorBar.Message = error.Message; ErrorBar.IsOpen = true; }
}
