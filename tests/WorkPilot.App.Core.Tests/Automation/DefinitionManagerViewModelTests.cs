using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using WorkPilot.Application.Automation;
using WorkPilot.Application.Automation.Definition;
using WorkPilot.Application.Automation.Run;
using WorkPilot.App.Core.Automation;
using WorkPilot.App.Core.Primitives;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Validation;
using WorkPilot.Domain.PermissionGovernance;
using WorkPilot.Domain.PermissionGovernance.Evaluation;
using WorkPilot.Application.Permission.Policy;
using System.Linq;
using Xunit;

namespace WorkPilot.App.Core.Tests.Automation;

public class DefinitionManagerViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly AutomationId Id = AutomationId.Parse("auto-1");

    private sealed class FakeManager : IDefinitionManager
    {
        public Func<AutomationId, Result<DefinitionExport>>? ExportFn = _ => Result<DefinitionExport>.Ok(new DefinitionExport("{x}", "f.json", "h", 1, Now));
        public Func<string, Result<ImportedAutomation>>? ImportFn = _ => Result<ImportedAutomation>.Ok(new ImportedAutomation(Id, AutomationRevisionId.Parse("r1"), false, Array.Empty<ImportWarning>(), Now));
        public Func<AutomationId, DryRunPlan>? DryRunFn = _ => new DryRunPlan(true, "Completed", Array.Empty<DryRunStepPlan>(), false, 0, 0, null);
        public Func<AutomationId, EvaluationContext, PreflightResult>? PreflightFn = (_, _) => new PreflightResult(true, new ValidationResult(Array.Empty<ValidationIssue>()), Array.Empty<EffectiveCapabilityView>().ToList());

        public Task<Result<DefinitionExport>> ExportAsync(AutomationId id, CancellationToken ct = default) => Task.FromResult(ExportFn!(id));
        public Task<Result<ImportedAutomation>> ImportAsync(string json, CancellationToken ct = default) => Task.FromResult(ImportFn!(json));
        public Task<DryRunPlan> DryRunAsync(AutomationId id, CancellationToken ct = default) => Task.FromResult(DryRunFn!(id));
        public Task<PreflightResult> PreflightAsync(AutomationId id, EvaluationContext ctx, CancellationToken ct = default) => Task.FromResult(PreflightFn!(id, ctx));
    }

    private static async Task RunCommandAsync(ICommand cmd, object? param)
    {
        var tcs = new TaskCompletionSource<bool>();
        if (cmd is AsyncRelayCommand a) a.ExecutionCompleted += (_, _) => tcs.TrySetResult(true);
        cmd.Execute(param);
        await Task.WhenAny(tcs.Task, Task.Delay(5000));
    }

    private static EvaluationContext Ctx() => new(
        PolicySubject.AutomationPrincipal, "src-1", null, true, false, true, "space-1",
        true, false, 0, true, Now, "automation", "manual", 0, 0, "healthy");

    [Fact]
    public async Task Export_success_sets_LastExport()
    {
        var vm = new DefinitionManagerViewModel(new FakeManager());
        await RunCommandAsync(vm.ExportCommand, "auto-1");
        Assert.NotNull(vm.LastExport);
        Assert.Equal("f.json", vm.LastExport!.FileName);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Export_failure_sets_ErrorMessage()
    {
        var fake = new FakeManager { ExportFn = _ => Result<DefinitionExport>.Fail(new AppError("AUT_DEF_IMPORT", ErrorCategory.Validation, "Definition.ImportFailed", false)) };
        var vm = new DefinitionManagerViewModel(fake);
        await RunCommandAsync(vm.ExportCommand, "auto-1");
        Assert.Null(vm.LastExport);
        Assert.NotNull(vm.ErrorMessage);
    }

    [Fact]
    public async Task Export_invalid_id_sets_error()
    {
        var vm = new DefinitionManagerViewModel(new FakeManager());
        await RunCommandAsync(vm.ExportCommand, string.Empty);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Null(vm.LastExport);
    }

    [Fact]
    public async Task Import_success_sets_LastImport()
    {
        var vm = new DefinitionManagerViewModel(new FakeManager());
        await RunCommandAsync(vm.ImportCommand, "{json}");
        Assert.NotNull(vm.LastImport);
        Assert.Equal(Id, vm.LastImport!.NewAutomationId);
    }

    [Fact]
    public async Task DryRun_success_sets_LastDryRun()
    {
        var vm = new DefinitionManagerViewModel(new FakeManager());
        await RunCommandAsync(vm.DryRunCommand, "auto-1");
        Assert.NotNull(vm.LastDryRun);
        Assert.True(vm.LastDryRun!.IsValid);
    }

    [Fact]
    public async Task Preflight_success_sets_LastPreflight()
    {
        var vm = new DefinitionManagerViewModel(new FakeManager()) { PreflightContext = Ctx() };
        await RunCommandAsync(vm.PreflightCommand, "auto-1");
        Assert.NotNull(vm.LastPreflight);
        Assert.True(vm.LastPreflight!.CanEnable);
    }

    [Fact]
    public async Task Preflight_without_context_sets_error()
    {
        var vm = new DefinitionManagerViewModel(new FakeManager());
        await RunCommandAsync(vm.PreflightCommand, "auto-1");
        Assert.NotNull(vm.ErrorMessage);
        Assert.Null(vm.LastPreflight);
    }

    [Fact]
    public async Task Busy_flag_cleared_after_run()
    {
        var vm = new DefinitionManagerViewModel(new FakeManager());
        await RunCommandAsync(vm.ExportCommand, "auto-1");
        Assert.False(vm.IsBusy);
    }
}
