using System;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Host.Core.Materialization;

namespace WorkPilot.Host.Hosting;

/// <summary>
/// The background host worker (T09) that drives the materialization/claim loop. It implements
/// <see cref="IHostWorker"/> (the seam T08 left) by delegating each heartbeat tick to the
/// platform-independent <see cref="IMaterializationEngine"/>. All scheduling, claim, lease and
/// recovery logic lives in the BCL engine (Host.Core) so it is unit-testable; this class is the thin
/// Windows glue and is delivered as source (its compile gate is deferred to a real Windows build per
/// doc 10 §16 / doc 14 §81).
/// </summary>
public sealed class MaterializationWorker : IHostWorker
{
    private readonly IMaterializationEngine _engine;

    public MaterializationWorker(IMaterializationEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    /// <summary>Invoked by <see cref="HostRunner"/> on each heartbeat tick while the Host is alive.</summary>
    public Task TickAsync(CancellationToken cancellationToken)
        => _engine.TickAsync(cancellationToken);
}
