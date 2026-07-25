using System;
using System.IO;
using System.Threading;
using WorkPilot.Contracts.Primitives;

namespace WorkPilot.Application.Diagnostics;

/// <summary>
/// Rotating JSONL file sink for one process (doc 05 §5, LOG-A08). Active file is <c>baseName.log</c>;
/// on exceeding <paramref name="maxBytes"/> it is atomically renamed to <c>baseName.1.log</c> ... up to
/// <paramref name="maxFiles"/>, dropping the oldest. Each process (app/host) owns a distinct base name so
/// they never share an active file. Rotation uses an atomic Move; throttle avoids hammering the FS.
/// </summary>
public sealed class FileLogSink : ILogSink
{
    private readonly string _activePath;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly object _gate = new();
    private long _lastRotationCheck;
    private bool _disposed;

    public FileLogSink(string directory, string baseName, long maxBytes = Limits.V1_5.DiagnosticMaxLogFileBytes, int maxFiles = Limits.V1_5.DiagnosticMaxLogFiles)
    {
        Directory.CreateDirectory(directory);
        _activePath = Path.Combine(directory, baseName + ".log");
        _maxBytes = maxBytes;
        _maxFiles = maxFiles;
    }

    public long CurrentSizeBytes
    {
        get
        {
            try { var fi = new FileInfo(_activePath); return fi.Exists ? fi.Length : 0; }
            catch { return 0; }
        }
    }

    public void AppendLine(string line)
    {
        lock (_gate)
        {
            File.AppendAllText(_activePath, line + Environment.NewLine);
        }
    }

    public void RotateIfNeeded(long maxBytes)
    {
        var limit = Math.Min(maxBytes, _maxBytes);
        if (CurrentSizeBytes < limit) return;
        lock (_gate)
        {
            if (CurrentSizeBytes < limit) return;
            try
            {
                File.Delete(Path.Combine(Path.GetDirectoryName(_activePath)!, Path.GetFileNameWithoutExtension(_activePath) + "." + (_maxFiles - 1) + ".log"));
                for (var i = _maxFiles - 2; i >= 1; i--)
                {
                    var src = Path.Combine(Path.GetDirectoryName(_activePath)!, Path.GetFileNameWithoutExtension(_activePath) + "." + i + ".log");
                    if (File.Exists(src))
                        File.Move(src, Path.Combine(Path.GetDirectoryName(_activePath)!, Path.GetFileNameWithoutExtension(_activePath) + "." + (i + 1) + ".log"));
                }
                File.Move(_activePath, Path.Combine(Path.GetDirectoryName(_activePath)!, Path.GetFileNameWithoutExtension(_activePath) + ".1.log"));
            }
            catch (IOException)
            {
                // best-effort rotation; never block the caller
            }
        }
        Interlocked.Exchange(ref _lastRotationCheck, DateTimeOffset.UtcNow.Ticks);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
