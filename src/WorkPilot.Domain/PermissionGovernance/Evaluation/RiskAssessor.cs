using System.Collections.Generic;
using System.Linq;

namespace WorkPilot.Domain.PermissionGovernance.Evaluation;

/// <summary>
/// Effective-risk helpers (doc 07 §5). Risk is ordinal Low=0 &lt; Medium=1 &lt; High=2 &lt; Critical=3;
/// the effective risk of a request is the maximum across the local capability manifest risk, the
/// argument-derived risk, and any explicit statement floor. Unknown schema / target always escalates.
/// </summary>
public static class RiskAssessor
{
    public static RiskLevel Max(RiskLevel a, RiskLevel b)
        => (RiskLevel)System.Math.Max((int)a, (int)b);

    public static RiskLevel Max(params RiskLevel[] levels)
        => levels.Aggregate(RiskLevel.Low, Max);

    public static RiskLevel Max(IEnumerable<RiskLevel> levels)
        => levels.Aggregate(RiskLevel.Low, Max);
}
