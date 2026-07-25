using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Security.Audit;

namespace WorkPilot.Application.Security;

/// <summary>
/// Persistence port for the tamper-evident security audit log (SEC-106). The store inserts entries
/// exactly as given (the HMAC chain is computed by <see cref="AuditLogWriter"/>); it must also return
/// the entries in sequence order for verification and export.
/// </summary>
public interface IAuditLogStore
{
    Task<Result<AuditEntry>> AppendAsync(AuditEntry entry, CancellationToken ct);
    Task<AuditEntry?> GetLastAsync(CancellationToken ct);
    Task<IReadOnlyList<AuditEntry>> GetAllAsync(CancellationToken ct);
}
