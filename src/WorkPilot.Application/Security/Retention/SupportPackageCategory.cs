namespace WorkPilot.Application.Security.Retention;

/// <summary>Selectable categories of a support package (doc 06 §9, SEC-108).</summary>
public enum SupportPackageCategory
{
    Incidents = 0,
    AuditLog = 1,
    SourceHealth = 2,
    Policy = 3,
    Configuration = 4,
    RunReports = 5
}
