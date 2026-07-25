using System.Collections.Generic;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Host.Core.Scheduling;

/// <summary>
/// Versioned error catalog for the background Host scheduler registration feature (SCHREG-* codes).
/// Registered globally so the catalog enforces cross-feature code uniqueness (AI dev rule §13).
/// </summary>
public sealed class SchedulerErrors : FeatureErrorCatalog
{
    public override string Feature => "Scheduler";

    public static readonly ErrorDefinition ExecutablePathInvalid =
        new("SCHREG_PATH", ErrorCategory.Validation, "Scheduler.ExecutablePathInvalid", false);
    public static readonly ErrorDefinition SidResolutionFailed =
        new("SCHREG_SID", ErrorCategory.Internal, "Scheduler.SidResolutionFailed", false);
    public static readonly ErrorDefinition RegistrationFailed =
        new("SCHREG_REGISTER", ErrorCategory.Internal, "Scheduler.RegistrationFailed", false);
    public static readonly ErrorDefinition QueryFailed =
        new("SCHREG_QUERY", ErrorCategory.Internal, "Scheduler.QueryFailed", false);
    public static readonly ErrorDefinition RemoveFailed =
        new("SCHREG_REMOVE", ErrorCategory.Internal, "Scheduler.RemoveFailed", false);
    public static readonly ErrorDefinition HealthFailed =
        new("SCHREG_HEALTH", ErrorCategory.Internal, "Scheduler.HealthFailed", false);

    public override IReadOnlyList<ErrorDefinition> Definitions { get; } = new[]
    {
        ExecutablePathInvalid, SidResolutionFailed, RegistrationFailed,
        QueryFailed, RemoveFailed, HealthFailed
    };

    public static readonly SchedulerErrors Instance = new();

    static SchedulerErrors() => ErrorCatalog.Register(Instance);

    public static AppError ExecutablePathInvalidError(string detail)
        => Instance.Error("SCHREG_PATH", new Dictionary<string, string> { ["detail"] = detail });

    public static AppError SidResolutionError() => Instance.Error("SCHREG_SID");
    public static AppError RegistrationError() => Instance.Error("SCHREG_REGISTER");
    public static AppError QueryError() => Instance.Error("SCHREG_QUERY");
    public static AppError RemoveError() => Instance.Error("SCHREG_REMOVE");
    public static AppError HealthError() => Instance.Error("SCHREG_HEALTH");
}
