using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Security.Retention;

namespace WorkPilot.Application.Security.Retention;

/// <summary>
/// Reads / validates / persists retention settings (doc 05 §9). Out-of-range windows are clamped to
/// the schema CHECK bounds rather than rejected, so a corrupt or future value can never disable cleanup.
/// </summary>
public sealed class RetentionSettingsService
{
    private readonly IRetentionSettingsStore _store;

    public RetentionSettingsService(IRetentionSettingsStore store) => _store = store;

    public Task<Result<RetentionSettings>> GetAsync(CancellationToken ct = default) => _store.GetAsync(ct);

    public async Task<Result<RetentionSettings>> SaveAsync(RetentionSettings settings, CancellationToken ct = default)
    {
        var clamped = settings.Policy.Clamp();
        var next = settings with { Policy = clamped };
        var save = await _store.SaveAsync(next, ct).ConfigureAwait(false);
        return save.IsSuccess ? Result<RetentionSettings>.Ok(next) : Result<RetentionSettings>.Fail(save.Error!);
    }
}
