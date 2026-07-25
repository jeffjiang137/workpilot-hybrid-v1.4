using System.Collections.Generic;
using System.Threading;

namespace WorkPilot.Application.Diagnostics;

/// <summary>In-memory <see cref="ILogSink"/> for tests: records lines, simulates size + rotation + I/O failure.</summary>
public sealed class MemoryLogSink : ILogSink
{
    private readonly List<string> _lines = new();
    private readonly object _gate = new();
    public bool FailNextWrite { get; set; }
    public int RotateCallCount { get; private set; }
    public long ForcedMaxBytes { get; set; } = long.MaxValue;
    /// <summary>When set, <see cref="AppendLine"/> blocks until the event is signaled (deterministic overflow tests).</summary>
    public ManualResetEventSlim? Block { get; set; }

    public IReadOnlyList<string> Lines
    {
        get { lock (_gate) return new List<string>(_lines); }
    }

    public long CurrentSizeBytes
    {
        get { lock (_gate) { var n = 0L; foreach (var l in _lines) n += l.Length + 1; return n; } }
    }

    public void AppendLine(string line)
    {
        Block?.Wait();
        if (FailNextWrite)
        {
            FailNextWrite = false;
            throw new System.IO.IOException("simulated diagnostic I/O failure");
        }
        lock (_gate) _lines.Add(line);
    }

    public void RotateIfNeeded(long maxBytes)
    {
        RotateCallCount++;
        var limit = System.Math.Min(maxBytes, ForcedMaxBytes);
        lock (_gate)
        {
            while (_lines.Count > 0 && CurrentSizeBytes > limit && _lines.Count > 1)
                _lines.RemoveAt(0); // drop oldest on rotation
        }
    }

    public void Dispose() { }
}
