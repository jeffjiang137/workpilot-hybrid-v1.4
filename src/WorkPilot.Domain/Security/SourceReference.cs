namespace WorkPilot.Domain.Security;

/// <summary>
/// Safe reference to the originating connector / MCP / source of a security event. Stores only the
/// kind + stable id (e.g. <c>connector:github</c> + <c>src_abc</c>) — never a display name, path, URL
/// or secret (doc 06 §2). Used to build the tamper-resistant fingerprint.
/// </summary>
public sealed record SourceReference(string Kind, string Id)
{
    /// <summary>Stable composite key used in fingerprints and store lookups.</summary>
    public string CompositeKey => $"{Kind}:{Id}";
}
