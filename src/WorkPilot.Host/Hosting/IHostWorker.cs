using System.Threading;
using System.Threading.Tasks;

namespace WorkPilot.Host.Hosting;

/// <summary>
/// The long-running background worker the Host executes while it holds the single-instance mutex.
/// In v1.5 this is the materializer/claim loop (T09+); for T08 the Host idles when no worker is
/// supplied. This port is the composition seam T09 fills — it is intentionally not implemented in
/// T08 to honor "one task at a time".
/// </summary>
public interface IHostWorker
{
    /// <summary>Invoked on each heartbeat tick while the Host is alive.</summary>
    Task TickAsync(CancellationToken cancellationToken);
}
