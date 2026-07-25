using System;
using System.Collections.Generic;

namespace WorkPilot.Application.Security.Retention;

/// <summary>Outcome of building a support package (LOG-006, SEC-108).</summary>
public sealed record SupportBundleResult(
    string OutputPath,
    string ManifestHash,
    long TotalBytes,
    int FileCount,
    DateTimeOffset GeneratedAtUtc,
    IReadOnlyList<string> IncludedCategories);
