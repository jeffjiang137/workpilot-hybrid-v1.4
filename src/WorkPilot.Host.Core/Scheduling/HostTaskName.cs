namespace WorkPilot.Host.Core.Scheduling;

/// <summary>
/// Deterministic, stable derivation of the background Host scheduled-task name from the application
/// identity. The name is the primary key used by the OS task scheduler, so it must be (a) stable
/// across launches and (b) free of characters the Task Scheduler forbids (backslash, slash, colon).
/// Pure and testable.
/// </summary>
public static class HostTaskName
{
    /// <summary>Well-known, human-readable prefix. The full name is <c>prefix.appId</c>.</summary>
    public const string Prefix = "WorkPilot.BackgroundHost";

    private static readonly char[] InvalidChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

    /// <summary>Derive the task name for the given application id. <paramref name="appId"/> is sanitized.</summary>
    public static string ForApp(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
            throw new System.ArgumentException("appId must not be empty", nameof(appId));

        var safe = new string(appId.Trim().Select(c => System.Array.IndexOf(InvalidChars, c) >= 0 ? '_' : c).ToArray());
        return Prefix + "." + safe;
    }

    /// <summary>True if <paramref name="name"/> was produced by <see cref="ForApp"/> for this product.</summary>
    public static bool IsOurs(string name)
        => !string.IsNullOrEmpty(name) && name.StartsWith(Prefix + ".", System.StringComparison.Ordinal);
}
