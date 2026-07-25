using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Application.Security.Retention;

/// <summary>Builds a privacy-safe run report (LOG-005). Port so the bundle builder is testable.</summary>
public interface IRunReportExporter
{
    Task<Result<RunReport>> BuildAsync(RunId id, CancellationToken ct = default);
}
