using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Security.Retention;

namespace WorkPilot.Application.Security.Retention;

/// <summary>Persistence of the singleton <see cref="RetentionSettings"/> (doc 05 §9).</summary>
public interface IRetentionSettingsStore
{
    Task<Result<RetentionSettings>> GetAsync(CancellationToken ct = default);
    Task<Result> SaveAsync(RetentionSettings settings, CancellationToken ct = default);
}
