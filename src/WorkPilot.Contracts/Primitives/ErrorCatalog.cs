using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace WorkPilot.Contracts.Primitives;

/// <summary>
/// A single error definition within a feature's versioned catalog. Codes are immutable once
/// published (AI dev rule §13 / §224). <see cref="AppError"/> is materialized from this on demand.
/// </summary>
public sealed record ErrorDefinition(
    string Code,
    ErrorCategory Category,
    string MessageKey,
    bool IsRetryable)
{
    public AppError ToError(IReadOnlyDictionary<string, string>? details = null, string? correlationId = null) =>
        new(Code, Category, MessageKey, IsRetryable, details, correlationId);
}

/// <summary>
/// A feature-scoped, versioned set of error definitions. A feature owns its codes; once a code
/// ships its meaning must not change. Concrete catalogs are registered with <see cref="ErrorCatalog"/>.
/// </summary>
public abstract class FeatureErrorCatalog
{
    public abstract string Feature { get; }

    /// <summary>API version this catalog ships with. Bump only with an ADR.</summary>
    public virtual string ApiVersion => "1.5";

    public abstract IReadOnlyList<ErrorDefinition> Definitions { get; }

    public AppError Error(string code, IReadOnlyDictionary<string, string>? details = null, string? correlationId = null)
    {
        var def = Definitions.FirstOrDefault(d => d.Code == code)
                  ?? throw new KeyNotFoundException($"Error code '{code}' is not defined by feature '{Feature}'.");
        return def.ToError(details, correlationId);
    }
}

/// <summary>
/// Global, process-wide registry of feature error catalogs. Enforces that no error code is
/// reused across features (AI dev rule §13: "错误码一经发布不改义"). Thread-safe.
/// </summary>
public static class ErrorCatalog
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, FeatureErrorCatalog> Features = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> CodeToFeature = new(StringComparer.Ordinal);

    public static void Register(FeatureErrorCatalog catalog)
    {
        if (catalog is null)
            throw new ArgumentNullException(nameof(catalog));

        lock (Lock)
        {
            if (Features.ContainsKey(catalog.Feature))
                throw new InvalidOperationException($"Error catalog for feature '{catalog.Feature}' is already registered.");

            foreach (var def in catalog.Definitions)
            {
                if (CodeToFeature.TryGetValue(def.Code, out var existing))
                    throw new InvalidOperationException(
                        $"Error code '{def.Code}' is already owned by feature '{existing}' and cannot be reused by '{catalog.Feature}'.");
                CodeToFeature[def.Code] = catalog.Feature;
            }

            Features[catalog.Feature] = catalog;
        }
    }

    public static IReadOnlyCollection<FeatureErrorCatalog> All() => Features.Values.ToImmutableArray();

    public static IReadOnlyCollection<string> AllCodes() => CodeToFeature.Keys.ToImmutableArray();

    public static bool IsCodeUnique(string code) => !CodeToFeature.ContainsKey(code);
}
