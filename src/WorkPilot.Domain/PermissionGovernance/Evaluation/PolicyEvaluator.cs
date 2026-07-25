using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using WorkPilot.Contracts.Primitives.Ids;

namespace WorkPilot.Domain.PermissionGovernance.Evaluation;

/// <summary>
/// The pure, deterministic Policy Core evaluator (doc 07 §6, PER-001–007). Given an immutable
/// <see cref="PolicySnapshot"/>, a runtime <see cref="EvaluationContext"/>, a
/// <see cref="CapabilityDescriptor"/> and validated <see cref="EvaluationArguments"/>, it returns a
/// complete <see cref="PermissionDecision"/> with an ordered, byte-stable <see cref="DecisionTraceItem"/>
/// list. The algorithm is fail-closed: any missing / unknown / malformed input resolves to
/// <see cref="PermissionDecisionKind.Deny"/>, and a single applicable Deny at any layer always wins
/// (Deny-priority). No I/O, no clock dependency beyond the <c>NowUtc</c> supplied in the context —
/// so the same simulator and the real gate share this exact function (doc 07 §14).
/// </summary>
public static class PolicyEvaluator
{
    public static PermissionDecision Evaluate(
        PolicySnapshot snapshot,
        EvaluationContext context,
        CapabilityDescriptor capability,
        EvaluationArguments arguments,
        IReadOnlyList<PolicyLayer>? presentLayers = null)
    {
        var trace = new List<DecisionTraceItem>();
        RiskLevel effRisk = RiskLevel.Low;
        ResourceScope? effScope = arguments.InvocationScope;

        PermissionDecision Deny(string code) =>
            new(PermissionDecisionKind.Deny, code, effRisk, effScope, trace, null, snapshot.PolicyHash);
        PermissionDecision Ask(string code) =>
            new(PermissionDecisionKind.Ask, code, effRisk, effScope, trace, null, snapshot.PolicyHash);
        PermissionDecision Allow() =>
            new(PermissionDecisionKind.Allow, PolicyReasonCodes.AllowedByPolicy, effRisk, effScope, trace, null, snapshot.PolicyHash);
        PermissionDecision Defer(string code, DateTimeOffset until) =>
            new(PermissionDecisionKind.Defer, code, effRisk, effScope, trace, until, snapshot.PolicyHash);

        // ---- Step 2: emergency / source / space (context invalid → Deny) ----
        if (context.EmergencyStopActive)
        {
            trace.Add(new(2, null, null, PolicyReasonCodes.EmergencyStopActive, "emergency stop active"));
            return Deny(PolicyReasonCodes.EmergencyStopActive);
        }
        if (!context.SourceEnabled)
        {
            trace.Add(new(2, null, null, PolicyReasonCodes.SourceDisabled, "source disabled"));
            return Deny(PolicyReasonCodes.SourceDisabled);
        }
        if (context.SourceQuarantined)
        {
            trace.Add(new(2, null, null, PolicyReasonCodes.SourceQuarantined, "source quarantined"));
            return Deny(PolicyReasonCodes.SourceQuarantined);
        }
        if (!context.SpaceLinked)
        {
            trace.Add(new(2, null, null, PolicyReasonCodes.SpaceSourceNotEnabled, "space not linked"));
            return Deny(PolicyReasonCodes.SpaceSourceNotEnabled);
        }

        // ---- Step 4: capability schema current ----
        if (!string.IsNullOrEmpty(context.SourceSchemaSha256) && !string.IsNullOrEmpty(capability.SchemaSha256)
            && !string.Equals(context.SourceSchemaSha256, capability.SchemaSha256, StringComparison.Ordinal))
        {
            trace.Add(new(4, null, null, PolicyReasonCodes.SchemaChanged, "capability schema mismatch"));
            return Deny(PolicyReasonCodes.SchemaChanged);
        }

        // ---- Step 5: base risk (local + argument; unknown schema escalates to High) ----
        var unknownSchema = string.IsNullOrEmpty(capability.SchemaSha256);
        var baseRisk = RiskAssessor.Max(capability.LocalRisk, arguments.ArgumentRisk);
        if (unknownSchema) baseRisk = RiskAssessor.Max(baseRisk, RiskLevel.High);
        effRisk = baseRisk;

        // ---- Step 6/7: load enabled statements and filter applicable ----
        var applicable = new List<(LayeredStatement Ls, bool TemporalUnmet)>();
        foreach (var ls in snapshot.Statements)
        {
            var st = ls.Statement;
            if (!st.Enabled) continue;
            if (st.HasWildcardAllow()) continue; // defensive: wildcard Allow must never grant
            if (!st.Subjects.Contains(context.Subject)) continue;

            var srcSel = Selector.Parse(st.SourceSelectorJson);
            if (!srcSel.Matches(context.SourceStableId, context.SourceSchemaSha256)) continue;

            var capSel = Selector.Parse(st.CapabilitySelectorJson);
            if (!capSel.Matches(capability.StableId, capability.SchemaSha256)) continue;

            if (baseRisk < st.RiskMin || baseRisk > st.RiskMax) continue;

            var cm = ConditionEvaluator.EvaluateAll(st.Conditions, context, out var temporalUnmet);
            if (cm == ConditionEvaluator.ConditionMatch.ParseError)
            {
                trace.Add(new(7, ls.Layer, st.Id, PolicyReasonCodes.ArgumentsInvalid, "condition parse error"));
                return Deny(PolicyReasonCodes.ArgumentsInvalid);
            }
            if (cm == ConditionEvaluator.ConditionMatch.NotMatched && !temporalUnmet)
                continue; // genuinely not applicable now

            applicable.Add((ls, temporalUnmet));
        }

        // ---- Step 8: Deny priority ----
        var denyHit = applicable.FirstOrDefault(x => x.Ls.Statement.Effect == PolicyEffect.Deny);
        if (denyHit.Ls is not null)
        {
            trace.Add(new(8, denyHit.Ls.Layer, denyHit.Ls.Statement.Id, PolicyReasonCodes.ExplicitDeny, "explicit deny wins"));
            return Deny(PolicyReasonCodes.ExplicitDeny);
        }

        // ---- Step 9: required-layer coverage ----
        var covering = applicable
            .Where(x => x.Ls.Statement.Effect == PolicyEffect.Allow || x.Ls.Statement.Effect == PolicyEffect.Ask)
            .ToList();

        var present = presentLayers ?? snapshot.Statements.Select(s => s.Layer).Distinct().ToList();
        var required = new List<(PolicyLayer Layer, string AutoCode, string InteractiveCode)>();
        if (present.Contains(PolicyLayer.GlobalPolicy))
            required.Add((PolicyLayer.GlobalPolicy, PolicyReasonCodes.CapabilityNotAllowlisted, PolicyReasonCodes.CapabilityNotAllowlisted));
        if (present.Contains(PolicyLayer.SpacePolicy))
            required.Add((PolicyLayer.SpacePolicy, PolicyReasonCodes.CapabilityNotAllowlisted, PolicyReasonCodes.CapabilityNotAllowlisted));
        required.Add((PolicyLayer.ExpertPolicy, PolicyReasonCodes.ExpertSourceNotGranted, PolicyReasonCodes.ExpertSourceNotGranted));
        required.Add((PolicyLayer.AutomationPolicy, PolicyReasonCodes.MissingAutomationGrant, PolicyReasonCodes.MissingAutomationGrant));

        foreach (var (layer, autoCode, interactiveCode) in required)
        {
            var covered = covering.Any(c => c.Ls.Layer == layer);
            if (covered) continue;
            if (context.Subject == PolicySubject.AutomationPrincipal)
            {
                trace.Add(new(9, layer, null, autoCode, "missing required-layer coverage"));
                return Deny(autoCode);
            }
            trace.Add(new(9, layer, null, interactiveCode, "missing required-layer coverage -> ask"));
            return Ask(interactiveCode);
        }

        // ---- Step 10: effective scope = intersection of covering scopes and invocation scope ----
        foreach (var c in covering)
        {
            if (c.Ls.Statement.Scope is null) continue;
            var r = ScopeIntersector.Intersect(effScope, c.Ls.Statement.Scope);
            if (r.Outcome == ScopeIntersector.Kind.Disjoint)
            {
                trace.Add(new(10, c.Ls.Layer, c.Ls.Statement.Id, PolicyReasonCodes.ResourceOutOfScope, "scope disjoint"));
                return Deny(PolicyReasonCodes.ResourceOutOfScope);
            }
            effScope = r.Scope; // Bounded or Unbounded(null)
        }

        // ---- Step 12: effective risk = max(base, statement floors) ----
        if (covering.Count > 0)
            effRisk = RiskAssessor.Max(baseRisk, covering.Max(c => c.Ls.Statement.RiskMin));
        else
            effRisk = baseRisk;

        // ---- Step 13: Critical blocked ----
        if (effRisk == RiskLevel.Critical)
        {
            trace.Add(new(13, null, null, PolicyReasonCodes.CriticalBlocked, "effective risk critical"));
            return Deny(PolicyReasonCodes.CriticalBlocked);
        }

        // ---- Step 15: temporal Defer (only when every coverage is a temporally-gated Allow) ----
        var anyFullyMet = covering.Any(c => !c.TemporalUnmet);
        var temporallyGatedAllows = covering
            .Where(c => c.TemporalUnmet && c.Ls.Statement.Effect == PolicyEffect.Allow)
            .ToList();
        if (temporallyGatedAllows.Count > 0 && !anyFullyMet)
        {
            var until = DeferWindowCalculator.ComputeNext(context, temporallyGatedAllows.Select(c => c.Ls.Statement));
            trace.Add(new(15, null, null, PolicyReasonCodes.TimeWindowDeferred, "allow gated by time window"));
            return Defer(PolicyReasonCodes.TimeWindowDeferred, until);
        }

        // ---- Step 14: execution-mode matrix ----
        var hasAllow = covering.Any(c => c.Ls.Statement.Effect == PolicyEffect.Allow);
        if (!hasAllow)
        {
            trace.Add(new(14, null, null, PolicyReasonCodes.AskRequired, "ask coverage only"));
            return Ask(PolicyReasonCodes.AskRequired);
        }

        if (context.Subject == PolicySubject.AutomationPrincipal)
        {
            // Automation: every required layer must have an Allow (not merely Ask) for a final Allow.
            var allRequiredAllow = required.All(rl =>
                covering.Any(c => c.Ls.Layer == rl.Layer && c.Ls.Statement.Effect == PolicyEffect.Allow));
            if (!allRequiredAllow)
            {
                trace.Add(new(14, null, null, PolicyReasonCodes.AskRequired, "automation partial allow coverage"));
                return Ask(PolicyReasonCodes.AskRequired);
            }
            if (effRisk == RiskLevel.Medium)
            {
                if (context.AutomationGrantPresent)
                    return Allow();
                trace.Add(new(14, null, null, PolicyReasonCodes.MissingAutomationGrant, "medium automation without grant"));
                return Ask(PolicyReasonCodes.MissingAutomationGrant);
            }
            if (effRisk == RiskLevel.High)
            {
                trace.Add(new(14, null, null, PolicyReasonCodes.HighRequiresApproval, "high automation requires per-run approval"));
                return Ask(PolicyReasonCodes.HighRequiresApproval);
            }
            return Allow();
        }

        // InteractiveUser / SystemMaintenance
        switch (effRisk)
        {
            case RiskLevel.Low:
                return Allow();
            case RiskLevel.Medium:
                trace.Add(new(14, null, null, PolicyReasonCodes.AskRequired, "medium interactive requires confirmation"));
                return Ask(PolicyReasonCodes.AskRequired);
            case RiskLevel.High:
                trace.Add(new(14, null, null, PolicyReasonCodes.HighRequiresApproval, "high requires per-parameter approval"));
                return Ask(PolicyReasonCodes.HighRequiresApproval);
            default:
                return Deny(PolicyReasonCodes.CriticalBlocked);
        }
    }
}

/// <summary>
/// Computes the next window start for a temporally-gated Allow so the caller can schedule a retry
/// (doc 07 §15). Deterministic and timezone-aware; falls back to +1 day when no window is decodable.
/// </summary>
internal static class DeferWindowCalculator
{
    public static DateTimeOffset ComputeNext(EvaluationContext ctx, IEnumerable<PolicyStatement> statements)
    {
        var candidates = new List<DateTimeOffset>();
        foreach (var st in statements)
            foreach (var c in st.Conditions)
                if (ConditionEvaluator.IsTemporal(c.Kind))
                    candidates.Add(NextForCondition(c, ctx));

        if (candidates.Count == 0)
            return ctx.NowUtc.AddDays(1);
        return candidates.Min();
    }

    private static DateTimeOffset NextForCondition(PolicyCondition c, EvaluationContext ctx)
    {
        if (c.Kind == PolicyConditionKind.DaysOfWeek)
        {
            if (!TryParseDays(c.DetailJson, out var days)) return ctx.NowUtc.AddDays(1);
            for (int i = 0; i < 8; i++)
            {
                var d = ctx.NowUtc.AddDays(i);
                var iso = ((int)d.DayOfWeek + 6) % 7 + 1;
                if (days.Contains(iso)) return d.Date;
            }
            return ctx.NowUtc.AddDays(1);
        }
        // time_window: next occurrence of "from" in the resolved tz
        try
        {
            using var doc = JsonDocument.Parse(c.DetailJson);
            var detail = doc.RootElement;
            var tz = ResolveZone(detail);
            var fromStr = detail.TryGetProperty("from", out var f) ? f.GetString() : null;
            if (fromStr is null || !TimeSpan.TryParse(fromStr, CultureInfo.InvariantCulture, out var from))
                return ctx.NowUtc.AddDays(1);
            for (int i = 0; i < 3; i++)
            {
                var local = TimeZoneInfo.ConvertTime(ctx.NowUtc.AddDays(i), tz);
                var fromLocal = new DateTime(local.Year, local.Month, local.Day, from.Hours, from.Minutes, 0);
                var fromUtc = TimeZoneInfo.ConvertTimeToUtc(fromLocal, tz);
                if (fromUtc > ctx.NowUtc) return fromUtc;
            }
            return ctx.NowUtc.AddDays(1);
        }
        catch (JsonException)
        {
            return ctx.NowUtc.AddDays(1);
        }
    }

    private static bool TryParseDays(string json, out List<int> days)
    {
        days = new List<int>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("days", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var e in arr.EnumerateArray())
                if (e.TryGetInt32(out var d)) days.Add(d);
            return days.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static TimeZoneInfo ResolveZone(JsonElement detail)
    {
        if (detail.TryGetProperty("tz", out var tzEl) && tzEl.ValueKind == JsonValueKind.String)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(tzEl.GetString()!); }
            catch { /* fall through */ }
        }
        return TimeZoneInfo.Utc;
    }
}
