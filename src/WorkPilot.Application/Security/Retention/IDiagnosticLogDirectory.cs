using System.Collections.Generic;

namespace WorkPilot.Application.Security.Retention;

/// <summary>
/// Locates the diagnostic log files for a support package (doc 05 §10.2, SEC-108). Host-provided:
/// the real implementation points at <c>%LocalAppData%/WorkPilot/diagnostics</c> with the configured
/// base name. The bundle builder enumerates the most recent files and re-redacts them.
/// </summary>
public interface IDiagnosticLogDirectory
{
    /// <summary>Directory containing the rolling <c>&lt;baseName&gt;.log</c> / <c>&lt;baseName&gt;.&lt;n&gt;.log</c> files.</summary>
    string Directory { get; }

    /// <summary>Base file name (without extension) used by the diagnostic logger.</summary>
    string BaseName { get; }
}
