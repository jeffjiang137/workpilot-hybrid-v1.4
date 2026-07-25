namespace WorkPilot.Domain.Security;

/// <summary>
/// Computes the effective severity of a security event (doc 06 §5). The fixed detection rule supplies
/// a <paramref name="baseSeverity"/>; the modifiers below may ONLY raise it (never lower), and
/// incomplete / heuristic evidence may never be raised to <see cref="SecuritySeverity.Critical"/>
/// unless the rule itself set Critical.
/// </summary>
public static class SeverityCalculator
{
    public static SecuritySeverity Compute(
        SecuritySeverity baseSeverity,
        bool involvesCredential,
        bool involvesExecutable,
        bool involvesAudit,
        bool involvesRedaction,
        int affectedAutomationCount,
        bool externalSideEffectUnknownResult,
        bool evidenceIncomplete)
    {
        var sev = baseSeverity;

        if (affectedAutomationCount >= 3)
            sev = RaiseOne(sev);

        if (involvesCredential || involvesExecutable || involvesAudit || involvesRedaction)
            sev = AtLeast(sev, SecuritySeverity.High);

        if (externalSideEffectUnknownResult)
            sev = AtLeast(sev, SecuritySeverity.High);

        // Incomplete / heuristic evidence must not be promoted to Critical by modifiers.
        if (evidenceIncomplete && (int)baseSeverity < (int)SecuritySeverity.Critical)
            sev = AtMost(sev, SecuritySeverity.High);

        return Clamp(sev);
    }

    public static SecuritySeverity AtLeast(SecuritySeverity value, SecuritySeverity floor) =>
        (int)value < (int)floor ? floor : value;

    public static SecuritySeverity AtMost(SecuritySeverity value, SecuritySeverity ceiling) =>
        (int)value > (int)ceiling ? ceiling : value;

    public static SecuritySeverity Max(SecuritySeverity a, SecuritySeverity b) =>
        (int)a >= (int)b ? a : b;

    public static SecuritySeverity Clamp(SecuritySeverity value) =>
        AtMost(AtLeast(value, SecuritySeverity.Info), SecuritySeverity.Critical);

    private static SecuritySeverity RaiseOne(SecuritySeverity value)
    {
        var next = (int)value + 1;
        return next > (int)SecuritySeverity.Critical
            ? SecuritySeverity.Critical
            : (SecuritySeverity)next;
    }
}
