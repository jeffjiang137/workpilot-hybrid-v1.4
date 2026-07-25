namespace WorkPilot.Application.Security.Retention;

/// <summary>Static build/runtime identity written into a support package manifest (doc 05 §10.2).</summary>
public interface IAppInfo
{
    string AppVersion { get; }
    string OsVersion { get; }
    string Architecture { get; }
    int DatabaseSchemaVersion { get; }
}
