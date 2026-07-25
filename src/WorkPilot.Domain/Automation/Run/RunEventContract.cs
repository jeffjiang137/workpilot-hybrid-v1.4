using System.Collections.Generic;
using System.Collections.Immutable;

namespace WorkPilot.Domain.Automation.Run;

/// <summary>Allowed safe-property value kinds. Only these may appear in a run event (doc 05 §3).</summary>
public enum SafePropertyType
{
    EnumString,   // arbitrary allowed string (state names, node kinds)
    StableId,     // irreversible stable identifier (<=128)
    Int,          // signed integer
    Bool,         // true/false
    Count,        // non-negative integer count
    ByteSize,     // non-negative byte count
    Hash,         // short irreversible hash / alias (<=64)
    DurationMs,   // 0..3_600_000
    DurationSeconds,
    ErrorCode,    // stable error code string
    Risk          // Low|Medium|High|Critical
}

/// <summary>Compile-time description of one allowed property for a run-event kind (doc 05 §3 / ADR-1505).</summary>
public sealed record SafePropertySpec(
    string Name,
    SafePropertyType Type,
    int MaxLength = 256,
    long Min = 0,
    long Max = 0)
{
    public bool IsNumeric => Type is SafePropertyType.Int or SafePropertyType.Count
        or SafePropertyType.ByteSize or SafePropertyType.DurationMs or SafePropertyType.DurationSeconds;
}

/// <summary>A typed, allowlist-only descriptor for one run-event <c>kind</c>.</summary>
public sealed record RunEventDescriptor(string Kind, IReadOnlyList<SafePropertySpec> Properties)
{
    private readonly Dictionary<string, SafePropertySpec> _byName =
        Properties.ToDictionary(p => p.Name, p => p, StringComparer.Ordinal);

    public bool Contains(string name) => _byName.ContainsKey(name);
    public SafePropertySpec? Get(string name) => _byName.TryGetValue(name, out var s) ? s : null;
}

/// <summary>
/// Compile-time catalog of allowed run-event kinds and their safe-property allowlists (doc 05 §2.1 / ADR-1505).
/// Adding a kind requires a schema bump; unknown kinds are rejected at persistence time.
/// </summary>
public static class RunEventCatalog
{
    private static readonly Dictionary<string, RunEventDescriptor> Descriptors = Build();
    public static IReadOnlyCollection<string> KnownKinds => Descriptors.Keys.ToImmutableArray();

    public static bool TryGet(string kind, out RunEventDescriptor descriptor)
        => Descriptors.TryGetValue(kind, out descriptor!);

    public static bool IsKnownKind(string kind) => Descriptors.ContainsKey(kind);

    private static Dictionary<string, RunEventDescriptor> Build()
    {
        var d = new Dictionary<string, RunEventDescriptor>(StringComparer.Ordinal);

        // Run lifecycle
        Add(d, "run_created", Spec("automation_id", SafePropertyType.StableId), Spec("priority", SafePropertyType.EnumString, 16));
        Add(d, "claimed", Spec("worker_id", SafePropertyType.StableId));
        Add(d, "started", Spec("node_id", SafePropertyType.StableId));
        Add(d, "deferred", Spec("reason", SafePropertyType.EnumString, 32), Spec("delay_seconds", SafePropertyType.DurationSeconds, 0, 0, 86400));
        Add(d, "completed", Spec("duration_ms", SafePropertyType.DurationMs, 0, 0, 3_600_000));
        Add(d, "failed", Spec("error_code", SafePropertyType.ErrorCode, 64), Spec("node_id", SafePropertyType.StableId));
        Add(d, "cancel_requested", Spec("reason", SafePropertyType.EnumString, 32));
        Add(d, "cancelled", Spec("error_code", SafePropertyType.ErrorCode, 64));
        Add(d, "recovered", Spec("recovery_count", SafePropertyType.Count, 0, 0, 100), Spec("strategy", SafePropertyType.EnumString, 32));

        // Trigger
        Add(d, "trigger_materialized", Spec("trigger_id", SafePropertyType.StableId), Spec("occurrence_id", SafePropertyType.StableId));
        Add(d, "missed", Spec("missed_count", SafePropertyType.Count, 0, 0, 1000), Spec("reason", SafePropertyType.EnumString, 32));
        Add(d, "coalesced", Spec("coalesced_count", SafePropertyType.Count, 0, 0, 1000));

        // Step
        Add(d, "step_ready", Spec("node_id", SafePropertyType.StableId), Spec("node_kind", SafePropertyType.EnumString, 24));
        Add(d, "started", Spec("node_id", SafePropertyType.StableId));
        Add(d, "retrying", Spec("attempt", SafePropertyType.Count, 0, 0, 100), Spec("delay_seconds", SafePropertyType.DurationSeconds, 0, 0, 900), Spec("reason", SafePropertyType.EnumString, 32));
        Add(d, "waiting", Spec("reason", SafePropertyType.EnumString, 32));
        Add(d, "succeeded", Spec("duration_ms", SafePropertyType.DurationMs, 0, 0, 3_600_000), Spec("item_count", SafePropertyType.Count, 0, 0, 100000));
        Add(d, "skipped", Spec("reason", SafePropertyType.EnumString, 32));
        Add(d, "failed", Spec("error_code", SafePropertyType.ErrorCode, 64), Spec("node_id", SafePropertyType.StableId), Spec("attempt", SafePropertyType.Count, 0, 0, 100));
        Add(d, "outcome_unknown", Spec("node_id", SafePropertyType.StableId), Spec("error_code", SafePropertyType.ErrorCode, 64));

        // Permission
        Add(d, "permission_evaluated", Spec("source_id", SafePropertyType.StableId), Spec("decision", SafePropertyType.EnumString, 16), Spec("risk", SafePropertyType.Risk, 8));
        Add(d, "approval_requested", Spec("approval_id", SafePropertyType.StableId), Spec("risk", SafePropertyType.Risk, 8));
        Add(d, "approved", Spec("approval_id", SafePropertyType.StableId), Spec("receipt_id", SafePropertyType.StableId));
        Add(d, "denied", Spec("approval_id", SafePropertyType.StableId), Spec("reason", SafePropertyType.EnumString, 32));
        Add(d, "expired", Spec("approval_id", SafePropertyType.StableId));
        Add(d, "permit_consumed", Spec("node_id", SafePropertyType.StableId), Spec("permit_id", SafePropertyType.Hash, 64));

        // Capability
        Add(d, "capability_started", Spec("source_id", SafePropertyType.StableId), Spec("capability_stable_id", SafePropertyType.StableId), Spec("risk", SafePropertyType.Risk, 8));
        Add(d, "capability_completed", Spec("source_id", SafePropertyType.StableId), Spec("capability_stable_id", SafePropertyType.StableId), Spec("risk", SafePropertyType.Risk, 8), Spec("duration_ms", SafePropertyType.DurationMs, 0, 0, 3_600_000), Spec("item_count", SafePropertyType.Count, 0, 0, 100000), Spec("result_size_bucket", SafePropertyType.EnumString, 16), Spec("truncated", SafePropertyType.Bool));
        Add(d, "capability_failed", Spec("capability_stable_id", SafePropertyType.StableId), Spec("error_code", SafePropertyType.ErrorCode, 64));
        Add(d, "schema_stale", Spec("capability_stable_id", SafePropertyType.StableId), Spec("current_schema_sha", SafePropertyType.Hash, 64));

        // Budget
        Add(d, "budget_reserved", Spec("kind", SafePropertyType.EnumString, 24), Spec("amount", SafePropertyType.Int, 0, 0, 1_000_000_000));
        Add(d, "budget_exhausted", Spec("kind", SafePropertyType.EnumString, 24), Spec("limit", SafePropertyType.Int, 0, 0, 1_000_000_000));

        // Worker
        Add(d, "lease_acquired", Spec("lease_owner", SafePropertyType.StableId), Spec("ttl_seconds", SafePropertyType.DurationSeconds, 0, 0, 600));
        Add(d, "heartbeat_lost", Spec("lease_owner", SafePropertyType.StableId));
        Add(d, "worker_shutdown", Spec("reason", SafePropertyType.EnumString, 32));

        // T13 recovery / approval events
        Add(d, "run.recovery", Spec("recovery_count", SafePropertyType.Count, 0, 0, 100), Spec("action", SafePropertyType.EnumString, 32));
        Add(d, "run.approval", Spec("approval_id", SafePropertyType.StableId), Spec("status", SafePropertyType.EnumString, 16));
        Add(d, "run.receipt", Spec("receipt_id", SafePropertyType.StableId), Spec("consumed", SafePropertyType.Bool));

        return d;
    }

    private static void Add(Dictionary<string, RunEventDescriptor> d, string kind, params SafePropertySpec[] specs)
        => d[kind] = new RunEventDescriptor(kind, specs);

    private static SafePropertySpec Spec(string name, SafePropertyType type, int maxLength = 256, long min = 0, long max = 0)
        => new(name, type, maxLength, min, max);
}
