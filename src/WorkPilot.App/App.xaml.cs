using Microsoft.UI.Xaml;
using WorkPilot.Services;

namespace WorkPilot;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;
    public static Window MainAppWindow { get; private set; } = null!;
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            AppLogger.Error("Unhandled UI exception", args.Exception);
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Services = await AppServices.CreateAsync(); _window = new MainWindow(); MainAppWindow = _window;
            _window.Closed += async (_, _) =>
            {
                try { await Services.DisposeAsync(); }
                catch (Exception error) { AppLogger.Error("Application shutdown failed", error); }
            };
            _window.Activate();
        }
        catch (Exception error)
        {
            AppLogger.Error("Application initialization failed", error);
            _window = new Window { Title = "WorkPilot - 启动失败", Content = new Microsoft.UI.Xaml.Controls.TextBlock
                { Text = "本地数据库初始化或迁移失败。数据未被静默覆盖。\n\n" + error.Message +
                    "\n\n请保留 %LOCALAPPDATA%\\WorkPilot 后联系支持，或从最近的 workpilot.pre-v14 / workpilot.pre-v13 备份恢复。",
                  TextWrapping = TextWrapping.Wrap, Margin = new Thickness(32), IsTextSelectionEnabled = true } };
            MainAppWindow = _window; _window.Activate();
        }
    }
}
