namespace WorkPilot.Application.Security.Retention;

/// <summary>Probe for free disk space on a volume (host-provided; real impl uses DriveInfo on Windows).</summary>
public interface IDiskSpaceProbe
{
    /// <summary>Free bytes available at <paramref name="path"/> (or its backing volume).</summary>
    long GetFreeBytes(string path);
}
