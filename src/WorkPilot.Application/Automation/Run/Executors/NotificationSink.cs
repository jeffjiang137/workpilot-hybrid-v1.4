using System.Threading;
using System.Threading.Tasks;

namespace WorkPilot.Application.Automation.Run.Executors;

/// <summary>
/// Rendered, safe notification content. Contains only the final Title/Body strings — never the variable
/// store — so even a faulty sink implementation cannot leak run variables (RUN-008).
/// </summary>
public sealed record NotificationContent(string Title, string Body);

/// <summary>Result of a single notification delivery attempt.</summary>
public sealed record NotificationDeliveryResult(bool Delivered, string? ErrorCode = null);

/// <summary>
/// Port to the local notification surface (doc 03 §3.6). The Windows toast implementation lives in the
/// Host layer (WinUI/Win32, source delivery only in this sandbox); tests use an in-memory fake. The
/// sink receives only <see cref="NotificationContent"/>, never the run's variables.
/// </summary>
public interface INotificationSink
{
    Task<NotificationDeliveryResult> ShowAsync(NotificationContent content, CancellationToken ct);
}
