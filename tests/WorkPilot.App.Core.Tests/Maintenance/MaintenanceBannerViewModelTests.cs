using WorkPilot.App.Core.Maintenance;
using WorkPilot.Domain.Schema;
using Xunit;

namespace WorkPilot.App.Core.Tests.Maintenance;

public class MaintenanceBannerViewModelTests
{
    [Fact]
    public void None_state_is_not_visible()
    {
        var provider = new MaintenanceStateProvider();
        var vm = new MaintenanceBannerViewModel(provider);
        Assert.Equal(MaintenanceState.None, vm.State);
        Assert.False(vm.IsVisible);
        Assert.Equal(string.Empty, vm.TitleKey);
    }

    [Fact]
    public void ChecksumMismatch_is_visible_and_error_severity()
    {
        var provider = new MaintenanceStateProvider();
        var vm = new MaintenanceBannerViewModel(provider);
        provider.SetState(MaintenanceState.ChecksumMismatch);

        Assert.True(vm.IsVisible);
        Assert.Equal(MaintenanceState.ChecksumMismatch, vm.State);
        Assert.Equal("MAINTENANCE_CHECKSUM_MISMATCH", vm.TitleKey);
        Assert.Equal("error", vm.SeverityKey);
    }

    [Fact]
    public void DatabaseTooNew_is_error_severity()
    {
        var provider = new MaintenanceStateProvider();
        var vm = new MaintenanceBannerViewModel(provider);
        provider.SetState(MaintenanceState.DatabaseTooNew);

        Assert.True(vm.IsVisible);
        Assert.Equal("error", vm.SeverityKey);
        Assert.Equal("MAINTENANCE_DATABASE_TOO_NEW_BODY", vm.MessageKey);
    }

    [Fact]
    public void UpgradeRequired_is_warning_severity()
    {
        var provider = new MaintenanceStateProvider();
        var vm = new MaintenanceBannerViewModel(provider);
        provider.SetState(MaintenanceState.UpgradeRequired);

        Assert.True(vm.IsVisible);
        Assert.Equal("warning", vm.SeverityKey);
        Assert.Equal("MAINTENANCE_UPGRADE_REQUIRED", vm.TitleKey);
    }

    [Fact]
    public void Provider_change_event_updates_view_model()
    {
        var provider = new MaintenanceStateProvider();
        var vm = new MaintenanceBannerViewModel(provider);
        Assert.False(vm.IsVisible);

        provider.SetState(MaintenanceState.MaintenanceInProgress);
        Assert.True(vm.IsVisible);
        Assert.Equal(MaintenanceState.MaintenanceInProgress, vm.State);
        Assert.Equal("MAINTENANCE_IN_PROGRESS", vm.TitleKey);

        provider.SetState(MaintenanceState.None);
        Assert.False(vm.IsVisible);
    }
}
