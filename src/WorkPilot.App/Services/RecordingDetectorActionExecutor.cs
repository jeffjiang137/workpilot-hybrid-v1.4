using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WorkPilot.Application.Security;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Security.Detectors;

namespace WorkPilot.Services;

/// <summary>
/// Host-side detector remediation executor (doc 06 §7 / T19 default). Records each detector action
/// without applying real side effects, so detectors can run end-to-end without the host taking
/// destructive action automatically. The Security Center exposes explicit governance commands for an
/// operator to act on detected issues, which is the supported remediation path. WinUI compilation is
/// gated to a real Windows build (doc 10 §16).
/// </summary>
public sealed class RecordingDetectorActionExecutor : IDetectorActionExecutor
{
    private readonly object _gate = new();
    private readonly List<DetectorAction> _applied = new();

    /// <summary>The actions recorded since this executor was created (diagnostics only).</summary>
    public IReadOnlyList<DetectorAction> Applied
    {
        get
        {
            lock (_gate) return _applied.ToList();
        }
    }

    public Task<Result> ApplyAsync(DetectorAction action, CancellationToken ct)
    {
        if (action is not null)
        {
            lock (_gate) _applied.Add(action);
        }
        return Task.FromResult(Result.Success());
    }
}
