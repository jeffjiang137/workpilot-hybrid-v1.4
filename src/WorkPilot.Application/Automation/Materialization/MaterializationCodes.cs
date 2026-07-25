using WorkPilot.Domain.Automation;
using WorkPilot.Domain.Automation.Run;

namespace WorkPilot.Application.Automation.Materialization;

/// <summary>
/// Stable event kinds and message keys emitted by the materializer/claim/lease engine. Only kinds
/// enumerated in spec doc 05 §2.1 are used (unknown kinds are forbidden by the V1 event contract);
/// occurrence dispositions carry the skip/coalesce/blocked telemetry instead of inventing event kinds.
/// </summary>
public static class EventKinds
{
    public const string RunCreated = "run_created";
    public const string TriggerMaterialized = "trigger_materialized";
    public const string Coalesced = "coalesced";
    public const string Recovered = "recovered";
    public const string LeaseAcquired = "lease_acquired";
    public const string HeartbeatLost = "heartbeat_lost";
    public const string WorkerShutdown = "worker_shutdown";
    public const string TriggerMissed = "trigger_missed";
}

/// <summary>Localizable message keys for the events above (kept separate from error codes).</summary>
public static class MessageKeys
{
    public const string RunCreated = "Run.Created";
    public const string Coalesced = "Run.Coalesced";
    public const string Recovered = "Run.Recovered";
    public const string LeaseAcquired = "Run.LeaseAcquired";
    public const string HeartbeatLost = "Run.HeartbeatLost";
    public const string WorkerShutdown = "Run.WorkerShutdown";
    public const string TriggerMissed = "Run.TriggerMissed";
}

/// <summary>Maps a trigger definition type to the run trigger kind stored on the run.</summary>
public static class TriggerKindMapper
{
    public static RunTriggerKind ToRunTriggerKind(TriggerType type) => type switch
    {
        TriggerType.Manual => RunTriggerKind.Manual,
        TriggerType.Once => RunTriggerKind.Once,
        TriggerType.Interval => RunTriggerKind.Interval,
        TriggerType.CalendarDaily => RunTriggerKind.CalendarDaily,
        TriggerType.CalendarWeekly => RunTriggerKind.CalendarWeekly,
        TriggerType.CalendarMonthly => RunTriggerKind.CalendarMonthly,
        TriggerType.DomainEvent => RunTriggerKind.DomainEvent,
        _ => RunTriggerKind.Manual
    };
}
