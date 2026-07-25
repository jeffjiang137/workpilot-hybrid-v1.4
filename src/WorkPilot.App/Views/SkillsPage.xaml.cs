using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using WorkPilot.Models;

namespace WorkPilot.Views;

public sealed partial class SkillsPage : Page
{
    private readonly Services.AppServices _services = App.Services; private Skill? _selected;
    public SkillsPage() { InitializeComponent(); Loaded += async (_, _) => await ReloadAsync(); }
    private async Task ReloadAsync(string? id = null)
    {
        var items = await _services.Skills.ListAsync(); SkillList.ItemsSource = items;
        SkillList.SelectedItem = items.FirstOrDefault(x => x.Id == id) ?? items.FirstOrDefault();
    }
    private void OnSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SkillList.SelectedItem is not Skill item) return; _selected = item; TitleText.Text = item.DisplayName;
        DescriptionText.Text = item.Description; MetadataText.Text = $"{item.Publisher} · {item.Version}\n状态：{item.Status}\n包哈希：{item.PackageSha256}\n已绑定专家：{item.ExpertCount}";
        ToggleButton.IsEnabled = DeleteButton.IsEnabled = true;
    }
    private async void OnImport(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".zip");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainAppWindow));
            var file = await picker.PickSingleFileAsync(); if (file is null) return;
            var inspection = await _services.Skills.InspectAsync(file.Path);
            var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "安装技能？",
                Content = $"{inspection.Manifest.Name} · {inspection.Manifest.Version}\n发布者：{inspection.Manifest.Publisher}\n文件：{inspection.FileCount}\n解压大小：{inspection.UncompressedBytes:N0} 字节\n哈希：{inspection.PackageSha256}",
                PrimaryButtonText = "安装", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var installed = await _services.Skills.InstallAsync(inspection.Token); await ReloadAsync(installed.Id);
            ShowInfo("技能已安全安装", InfoBarSeverity.Success);
        }
        catch (Exception error) { Services.AppLogger.Error("Skill import failed", error); ShowInfo(error.Message, InfoBarSeverity.Error); }
    }
    private async void OnToggle(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return; await _services.Skills.SetEnabledAsync(_selected.Id, _selected.Status != "enabled"); await ReloadAsync(_selected.Id);
    }
    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return; var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "卸载技能？",
            Content = $"将解除 {_selected.ExpertCount} 个专家绑定并删除本机全部已安装版本。运行中的快照不受影响。",
            PrimaryButtonText = "解除绑定并卸载", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try { await _services.Skills.UninstallAsync(_selected.Id); await ReloadAsync(); }
        catch (Exception error) { ShowInfo(error.Message, InfoBarSeverity.Error); }
    }
    private void ShowInfo(string message, InfoBarSeverity severity) { InfoBar.Message = message; InfoBar.Severity = severity; InfoBar.IsOpen = true; }
}
