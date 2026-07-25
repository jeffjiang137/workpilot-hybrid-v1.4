using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WorkPilot.App.Core.Automation;
using WorkPilot.Application.Automation;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Scheduling;

namespace WorkPilot.Views;

/// <summary>
/// V1.5 five-step automation editor (doc 02 §4 / AUT-001..008). The view binds to
/// <see cref="AutomationEditorSession"/> (in <c>WorkPilot.App.Core</c>, a net8.0 BCL library shared
/// with the testable App.Core.Tests). This WinUI page owns only presentation: navigation rail,
/// loading/empty/error states, leave-unsaved dialog, trigger preview, preflight list, keyboard
/// shortcuts (Ctrl+S / Esc) and a11y names. All rules (dirty, conflict, optimistic concurrency,
/// space-immutability) live in the view-model. The host (T09 automation wiring) constructs the
/// session and assigns it to <see cref="Session"/>, or calls <see cref="LoadNewAsync"/> /
/// <see cref="LoadExistingAsync"/> after setting the three dependency properties.
///
/// NOTE: this WinUI project compiles only on a real Windows build (doc 10 §16); the logic it binds to
/// is fully unit-tested in WorkPilot.App.Core.Tests on any platform.
/// </summary>
public sealed partial class AutomationEditorPage : Page, INotifyPropertyChanged
{
    private AutomationEditorSession? _session;

    // Dependency seams supplied by the host (T09). The host builds the V1.5 service/clock/resolver.
    public IAutomationService? AutomationService { get; set; }
    public IClock? Clock { get; set; }
    public ITimeZoneResolver? TimeZoneResolver { get; set; }

    /// <summary>The editor view-model. Assigning it (re)binds the UI and subscribes to its changes.</summary>
    public AutomationEditorSession? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(_session, value)) return;
            if (_session is not null) _session.PropertyChanged -= OnSessionPropertyChanged;
            _session = value;
            OnPropertyChanged();
            if (_session is not null)
            {
                _session.PropertyChanged += OnSessionPropertyChanged;
                SyncCombos();
                OnPropertyChanged(nameof(SpaceIdText));
            }
            RefreshFromSession();
        }
    }

    /// <summary>Two-way bridge between the SpaceId struct (VM) and the plain TextBox (view).</summary>
    public string SpaceIdText
    {
        get => _session?.SpaceId?.Value ?? string.Empty;
        set
        {
            if (_session is null) return;
            var v = (value ?? string.Empty).Trim();
            _session.SpaceId = string.IsNullOrEmpty(v) ? null : SpaceId.Parse(v);
            OnPropertyChanged();
        }
    }

    /// <summary>Raised when the user asks to close (Esc / accelerator) so the host can navigate away.</summary>
    public event EventHandler? RequestClose;
    /// <summary>Raised when a save attempt finishes, carrying success/conflict so the host can react.</summary>
    public event EventHandler<EditorSaveResult>? SaveCompleted;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public AutomationEditorPage()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) => RefreshFromSession();
    }

    // ---- Loading ----
    public async Task LoadNewAsync(SpaceId space)
    {
        var (svc, clock, tz) = (AutomationService, Clock, TimeZoneResolver);
        if (svc is null || clock is null || tz is null) return;
        Session = new AutomationEditorSession(svc, clock, tz);
        Session.LoadNew(space);
    }

    public async Task LoadExistingAsync(AutomationId id)
    {
        var (svc, clock, tz) = (AutomationService, Clock, TimeZoneResolver);
        if (svc is null || clock is null || tz is null) return;
        Session = new AutomationEditorSession(svc, clock, tz);
        await Session.LoadExistingAsync(id, CancellationToken.None);
    }

    // ---- Command handlers ----
    private async void OnSaveDraftClick(object sender, RoutedEventArgs e) => await SaveDraftAsync();
    private async void OnSaveAndEnableClick(object sender, RoutedEventArgs e) => await SaveAndEnableAsync();

    private async Task SaveDraftAsync()
    {
        if (_session is null || _session.State == SaveState.Saving) return;
        var result = await _session.SaveDraftAsync(CancellationToken.None);
        SaveCompleted?.Invoke(this, result);
        RefreshFromSession();
    }

    private async Task SaveAndEnableAsync()
    {
        if (_session is null || _session.State == SaveState.Saving) return;
        var result = await _session.SaveAndEnableAsync(CancellationToken.None);
        SaveCompleted?.Invoke(this, result);
        RefreshFromSession();
    }

    private void OnPreviewClick(object sender, RoutedEventArgs e) => _session?.PreviewTrigger();
    private void OnPreflightClick(object sender, RoutedEventArgs e) => _session?.RunPreflight();

    // ---- Navigation ----
    private void OnNavStep(object sender, RoutedEventArgs e)
    {
        if (_session is null || sender is not Button { Tag: string tag }) return;
        if (int.TryParse(tag, out var idx) && idx is >= 0 and <= 4)
            _session.CurrentStep = (EditorStep)idx;
    }

    // ---- ComboBox bridges ----
    private void SyncCombos()
    {
        TriggerTypeBox.ItemsSource = Enum.GetValues(typeof(TriggerType));
        OverlapBox.ItemsSource = Enum.GetValues(typeof(OverlapPolicy));
        MissedBox.ItemsSource = Enum.GetValues(typeof(MissedRunPolicy));
    }

    private void OnTriggerTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_session?.TriggerSession is null || TriggerTypeBox.SelectedItem is not TriggerType t) return;
        _session.TriggerSession.ChangeType(t, DateTimeOffset.UtcNow);
    }

    private void OnOverlapChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_session is not null && OverlapBox.SelectedItem is OverlapPolicy o) _session.OverlapPolicy = o;
    }

    private void OnMissedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_session is not null && MissedBox.SelectedItem is MissedRunPolicy m) _session.MissedRunPolicy = m;
    }

    // ---- Keyboard ----
    private async void OnSaveAccelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await SaveDraftAsync();
    }

    private async void OnEscapeAccelerator(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await TryCloseAsync();
    }

    /// <summary>If dirty, asks the user how to leave (save / discard / cancel); otherwise requests close.</summary>
    private async Task TryCloseAsync()
    {
        if (_session is null || !_session.IsDirty)
        {
            RequestClose?.Invoke(this, EventArgs.Empty);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "未保存的更改",
            Content = "自动化有未保存的修改。要保存后再关闭，还是放弃更改？",
            PrimaryButtonText = "保存并关闭",
            SecondaryButtonText = "放弃更改",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.Primary)
        {
            var result = await _session.SaveDraftAsync(CancellationToken.None);
            SaveCompleted?.Invoke(this, result);
            if (result.Succeeded) RequestClose?.Invoke(this, EventArgs.Empty);
        }
        else if (choice == ContentDialogResult.Secondary)
        {
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---- Reactive UI sync ----
    private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AutomationEditorSession.CurrentStep)
            or nameof(AutomationEditorSession.State)
            or nameof(AutomationEditorSession.IsDirty)
            or nameof(AutomationEditorSession.HasConflict)
            or nameof(AutomationEditorSession.CanSaveAndEnable)
            or nameof(AutomationEditorSession.HasBlockingErrors)
            or nameof(AutomationEditorSession.HasWarnings)
            or nameof(AutomationEditorSession.WarningsAcknowledged)
            or nameof(AutomationEditorSession.SpaceChangedAfterLoad)
            or nameof(AutomationEditorSession.SpaceId)
            or nameof(AutomationEditorSession.TriggerSession)
            or nameof(AutomationEditorSession.WorkflowSession))
        {
            OnPropertyChanged(nameof(SpaceIdText)); // keep the space TextBox in sync
            RefreshFromSession();
        }
    }

    private void RefreshFromSession()
    {
        var s = _session;
        var has = s is not null;

        LoadingRing.IsActive = has && s!.State == SaveState.Saving;
        ErrorBar.IsOpen = has && s!.State == SaveState.Error;
        ErrorBar.Message = has && s!.LastError is not null ? s!.LastError!.Message : string.Empty;
        ConflictBar.IsOpen = has && s!.HasConflict;

        DirtyIndicator.Visibility = has && s!.IsDirty ? Visibility.Visible : Visibility.Collapsed;
        StateText.Text = has ? s!.State switch
        {
            SaveState.Saving => "保存中…",
            SaveState.Saved => s!.SpaceChangedAfterLoad ? "已另存为副本" : "已保存",
            SaveState.Error => "保存失败",
            SaveState.Conflict => "并发冲突",
            _ => string.Empty
        } : string.Empty;

        SaveEnableButton.IsEnabled = has && s!.CanSaveAndEnable;

        // Step visibility + nav highlight
        var step = has ? s!.CurrentStep : EditorStep.BasicInfo;
        StepBasic.Visibility = step == EditorStep.BasicInfo ? Visibility.Visible : Visibility.Collapsed;
        StepTrigger.Visibility = step == EditorStep.Trigger ? Visibility.Visible : Visibility.Collapsed;
        StepWorkflow.Visibility = step == EditorStep.Workflow ? Visibility.Visible : Visibility.Collapsed;
        StepPermissions.Visibility = step == EditorStep.Permissions ? Visibility.Visible : Visibility.Collapsed;
        StepTest.Visibility = step == EditorStep.TestEnable ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < 5; i++)
        {
            var btn = i switch { 0 => Nav0, 1 => Nav1, 2 => Nav2, 3 => Nav3, _ => Nav4 };
            btn.Opacity = i == (int)step ? 1.0 : 0.55;
            btn.FontWeight = i == (int)step ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }

        // Keep combos in sync with the view-model.
        if (has && s!.TriggerSession is not null) TriggerTypeBox.SelectedItem = s!.TriggerSession.Type;
        if (has) { OverlapBox.SelectedItem = s!.OverlapPolicy; MissedBox.SelectedItem = s!.MissedRunPolicy; }

        // Per-step validation hints.
        TriggerErrorBar.IsOpen = has && s!.TriggerSession is not null && s!.TriggerSession.Validation.HasErrors;
        TriggerErrorBar.Message = has && s!.TriggerSession?.Validation.HasErrors == true
            ? "触发器存在校验错误，请修正后再启用。" : string.Empty;
        WorkflowErrorText.Text = has && s!.WorkflowSession is not null && s!.WorkflowSession.Validation.HasErrors
            ? "工作流存在校验错误，请修正后再启用。" : string.Empty;

        BlockBar.IsOpen = has && s!.HasBlockingErrors;
    }
}
