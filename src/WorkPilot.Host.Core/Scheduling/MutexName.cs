namespace WorkPilot.Host.Core.Scheduling;

/// <summary>
/// Deterministic name of the named mutex the Host process uses for single-instance guarding
/// (T08 "mutex"). Global namespace so only one Host runs per machine per application identity,
/// regardless of session. Pure and testable.
/// </summary>
public static class MutexName
{
    public const string Prefix = "WorkPilot.Host.SingleInstance";

    private static readonly char[] InvalidChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    /// <summary>Derive the global mutex name for the given application id (sanitized).</summary>
    public static string ForApp(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
            throw new System.ArgumentException("appId must not be empty", nameof(appId));

        var safe = new string(appId.Trim().Select(c => System.Array.IndexOf(InvalidChars, c) >= 0 ? '_' : c).ToArray());
        return @"Global\" + Prefix + "." + safe;
    }
}
