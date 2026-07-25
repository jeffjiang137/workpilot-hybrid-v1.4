using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WorkPilot.Contracts.Primitives;
using WorkPilot.Domain.Automation.Run.Redaction;

namespace WorkPilot.Application.Diagnostics;

/// <summary>
/// JSONL diagnostic logger (doc 05 §5, LOG-A06/A08/A09). Low-severity events flow through a bounded channel
/// and are dropped under backpressure (with a dropped-count summary available to the UI); Warning/Error are
/// written directly so they are never lost. A single background reader serializes lines to the sink. Any sink
/// I/O failure flips <see cref="IsDegraded"/> and is swallowed — it must never block a capability send.
/// </summary>
public sealed class JsonlDiagnosticLogger : IDiagnosticLogger, IDisposable
{
    private readonly ILogSink _sink;
    private readonly ISecretMatcher? _matcher;
    private readonly ISet<string>? _canaryTokens;
    private readonly bool _releaseMode;
    private readonly long _maxBytes;
    private readonly Channel<string> _lines;
    private readonly Task _reader;
    private readonly CancellationTokenSource _cts = new();
    private int _droppedLow;
    private volatile bool _degraded;

    public JsonlDiagnosticLogger(
        ILogSink sink,
        ISecretMatcher? matcher = null,
        ISet<string>? canaryTokens = null,
        bool releaseMode = false,
        int channelCapacity = Limits.V1_5.DiagnosticChannelCapacity,
        long maxBytes = Limits.V1_5.DiagnosticMaxLogFileBytes,
        bool autoStartReader = true)
    {
        _sink = sink;
        _matcher = matcher;
        _canaryTokens = canaryTokens;
        _releaseMode = releaseMode;
        _maxBytes = maxBytes;
        // NOTE: Channel.CreateBounded(BoundedChannelOptions) ignores Capacity in this runtime; the int
        // overload correctly bounds (default FullMode is DropWrite, which is what we want).
        _lines = Channel.CreateBounded<string>(channelCapacity);
        _reader = autoStartReader ? Task.Run(() => PumpAsync(_cts.Token)) : Task.CompletedTask;
    }

    public bool IsDegraded => _degraded;

    /// <summary>Dropped low-severity event count under backpressure (LOG-A06 summary).</summary>
    public int DroppedLowCount => Volatile.Read(ref _droppedLow);

    public void Emit(DiagnosticEvent evt)
    {
        var line = BuildLine(evt);
        if (evt.Level is DiagnosticLogLevel.Warning or DiagnosticLogLevel.Error)
        {
            WriteLine(line); // high severity is never dropped
        }
        else
        {
            if (!_lines.Writer.TryWrite(line))
                Interlocked.Increment(ref _droppedLow);
        }
    }

    /// <summary>Wait until the bounded channel has drained (tests / shutdown).</summary>
    public async Task FlushAsync(TimeSpan timeout)
    {
        var end = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < end && !_lines.Reader.Completion.IsCompleted && _lines.Reader.Count > 0)
            await Task.Delay(1);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var line in _lines.Reader.ReadAllAsync(ct))
                WriteLine(line);
        }
        catch (OperationCanceledException) { }
    }

    private void WriteLine(string line)
    {
        try
        {
            _sink.AppendLine(line);
            _sink.RotateIfNeeded(_maxBytes);
            _degraded = false;
        }
        catch (Exception)
        {
            _degraded = true; // LOG-A09: degrade, never throw into the caller
        }
    }

    private string BuildLine(DiagnosticEvent evt)
    {
        var rawSafe = DiagnosticJson.BuildSafeObject(evt.Safe);
        var redactedSafe = RedactionPipeline.RedactSerialized(rawSafe, _matcher, _canaryTokens, _releaseMode).Value;
        var level = evt.Level switch
        {
            DiagnosticLogLevel.Trace => "trace",
            DiagnosticLogLevel.Warning => "warning",
            DiagnosticLogLevel.Error => "error",
            _ => "information"
        };
        var cid = evt.CorrelationId ?? "-";
        var ev = evt.Event ?? "-";
        // schema/ts/level/event/correlation are themselves safe; only the safe bag was redacted.
        return "{\"schema\":" + Limits.V1_5.DiagnosticSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"ts\":\"" + DateTimeOffset.UtcNow.ToString("O") + "\""
            + ",\"level\":\"" + level + "\""
            + ",\"event\":\"" + Escape(ev) + "\""
            + ",\"correlation_id\":\"" + Escape(cid) + "\""
            + ",\"safe\":" + redactedSafe + "}";
    }

    private static string Escape(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 2);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        try { _lines.Writer.TryComplete(); } catch { }
        try { _cts.Cancel(); } catch { }
        try { _reader.Wait(2000); } catch { }
        _sink.Dispose();
    }
}
