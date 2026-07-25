namespace WorkPilot.Contracts.Primitives;

/// <summary>
/// Centralized contract/algorithm versions (spec §15). Whenever an algorithm's behavior changes,
/// a new version is published; historical data is never silently reinterpreted.
/// </summary>
public static class ContractVersions
{
    public const string ContractsVersion = "1.5.0";

    public const int AutomationDefinitionSchema = 1;
    public const int PolicySchema = 1;
    public const int RunEventSchema = 1;

    public const string SchedulerAlgorithm = "calendar-v1";
    public const string PermissionAlgorithm = "policy-v1";
    public const string RedactionAlgorithm = "redaction-v1";
    public const string AuditIntegrityAlgorithm = "hmac-chain-v1";
}
