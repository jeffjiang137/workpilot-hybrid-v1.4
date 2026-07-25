using System;

namespace WorkPilot.Application.Diagnostics;

/// <summary>
/// Physical sink for one diagnostic log file (doc 05 §5, LOG-A08). Implementations rotate atomically and
/// must survive concurrent append. A separate sink instance per process (app vs host) guarantees they never
/// write the same active file.
/// </summary>
public interface ILogSink : IDisposable
{
    /// <summary>Append a single JSONL line. May throw on I/O failure (caller degrades).</summary>
    void AppendLine(string line);

    /// <summary>Current active-file size in bytes.</summary>
    long CurrentSizeBytes { get; }

    /// <summary>If the active file exceeds <paramref name="maxBytes"/>, rotate atomically (rename + cap count).</summary>
    void RotateIfNeeded(long maxBytes);
}
