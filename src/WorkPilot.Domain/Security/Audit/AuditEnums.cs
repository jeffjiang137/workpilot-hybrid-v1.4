namespace WorkPilot.Domain.Security.Audit;

/// <summary>Category of a security audit-log entry (SEC-106).</summary>
public enum AuditCategory : int
{
    Governance = 0,
    Detector = 1,
    Incident = 2,
    Integrity = 3,
    System = 4
}
