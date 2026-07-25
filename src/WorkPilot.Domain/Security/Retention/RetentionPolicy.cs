using System;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Domain.Security.Retention;

/// <summary>
/// Retention window for durable data (doc 05 §9, SEC-106). All bounds are enforced in
/// <see cref="Clamp"/> and mirrored by the <c>retention_settings</c> schema CHECK constraints.
/// <list type="bullet">
///   <item><description>Run metadata / steps: default 90d (30–365).</description></item>
///   <item><description>Run events: default 30d (7–90).</description></item>
///   <item><description>Security audit / incidents: default 180d (90–730).</description></item>
/// </list>
/// </summary>
public sealed record RetentionPolicy(
    int RunDays,
    int EventDays,
    int AuditDays)
{
    /// <summary>Product defaults (doc 05 §9).</summary>
    public static readonly RetentionPolicy Default = new(
        Limits.V1_5.RetentionDefaultRunDays,
        Limits.V1_5.RetentionDefaultEventDays,
        Limits.V1_5.RetentionDefaultAuditDays);

    /// <summary>Clamps every window into its allowed range (never throws — fail-safe bounds).</summary>
    public RetentionPolicy Clamp() => new(
        Clamp(RunDays, Limits.V1_5.RetentionMinRunDays, Limits.V1_5.RetentionMaxRunDays),
        Clamp(EventDays, Limits.V1_5.RetentionMinEventDays, Limits.V1_5.RetentionMaxEventDays),
        Clamp(AuditDays, Limits.V1_5.RetentionMinAuditDays, Limits.V1_5.RetentionMaxAuditDays));

    private static int Clamp(int value, int lo, int hi) => value < lo ? lo : value > hi ? hi : value;

    /// <summary>Absolute cutoff (exclusive) before which data of each kind may be deleted.</summary>
    public (DateTimeOffset RunCutoff, DateTimeOffset EventCutoff, DateTimeOffset AuditCutoff)
        ComputeCutoffs(DateTimeOffset now)
    {
        return (
            RunCutoff: now.AddDays(-RunDays),
            EventCutoff: now.AddDays(-EventDays),
            AuditCutoff: now.AddDays(-AuditDays));
    }
}
