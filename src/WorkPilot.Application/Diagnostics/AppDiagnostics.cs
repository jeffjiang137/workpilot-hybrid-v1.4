using System.Collections.Generic;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Application.Diagnostics;

/// <summary>
/// Compatible static facade for the V1.4 <c>AppLogger</c> (doc 05 §5). The composition root installs the
/// active <see cref="IDiagnosticLogger"/> implementation; until then calls are no-ops. Migration of the
/// existing V1.4 <c>AppLogger.Info/Error</c> call sites to this facade is the compatibility path required
/// by the T14 DoD (the WinUI/Host rewrite of those call sites is deferred to the Windows build).
/// </summary>
public static class AppDiagnostics
{
    private static IDiagnosticLogger? _logger;

    public static void SetLogger(IDiagnosticLogger logger) => _logger = logger;
    public static IDiagnosticLogger? Current => _logger;
    public static bool IsDegraded => _logger?.IsDegraded ?? false;

    public static void Info(string eventName, string? correlationId = null, IReadOnlyDictionary<string, object?>? safe = null)
        => _logger?.Emit(new DiagnosticEvent(eventName, DiagnosticLogLevel.Information, correlationId ?? "-", safe));

    public static void Warning(string eventName, string? correlationId = null, IReadOnlyDictionary<string, object?>? safe = null)
        => _logger?.Emit(new DiagnosticEvent(eventName, DiagnosticLogLevel.Warning, correlationId ?? "-", safe));

    public static void Error(string eventName, string? correlationId = null, IReadOnlyDictionary<string, object?>? safe = null)
        => _logger?.Emit(new DiagnosticEvent(eventName, DiagnosticLogLevel.Error, correlationId ?? "-", safe));
}
