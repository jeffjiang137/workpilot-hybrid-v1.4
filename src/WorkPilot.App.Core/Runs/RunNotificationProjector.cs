using WorkPilot.Contracts.Primitives.Ids;
using WorkPilot.Domain.Automation.Run;

namespace WorkPilot.App.Core.Runs;

/// <summary>
/// Projects a terminal/awaiting run into a user notification envelope (RUN-008). The envelope carries
/// only a localization title <em>key</em> and a safe reason <em>code</em> — never business body text
/// and never a secret. Windows toast text is built from the key; the code is for diagnostics only.
/// </summary>
public static class RunNotificationProjector
{
    public static RunNotification Project(AutomationRun run)
    {
        var (titleKey, reason) = run.Status switch
        {
            RunStatus.Completed => ("RunNotification.Completed", run.FinalErrorCode),
            RunStatus.Failed => ("RunNotification.Failed", run.FinalErrorCode),
            RunStatus.WaitingApproval => ("RunNotification.WaitingApproval", null),
            RunStatus.BlockedPolicy => ("RunNotification.BlockedPolicy", run.FinalErrorCode),
            RunStatus.NeedsReview => ("RunNotification.NeedsReview", run.FinalErrorCode),
            RunStatus.Cancelled => ("RunNotification.Cancelled", null),
            _ => ("RunNotification.StatusChanged", run.FinalErrorCode)
        };

        var isSecurityBlocked = run.Status is RunStatus.BlockedPolicy or RunStatus.NeedsReview;
        return new RunNotification(run.Id, run.Status, titleKey, reason, isSecurityBlocked);
    }
}
