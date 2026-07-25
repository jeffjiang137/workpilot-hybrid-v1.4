namespace WorkPilot.Domain.Automation.Scheduling;

/// <summary>Stable codes for scheduling/next-time computation. Structural, not runtime <see cref="AppError"/>s.</summary>
public static class SchedulingCodes
{
    public const string TimezoneNotFound = "SCH_TIMEZONE_NOT_FOUND";
    public const string IntervalInvalid = "SCH_INTERVAL_INVALID";
    public const string CalendarTimeInvalid = "SCH_CALENDAR_TIME_INVALID";
    public const string HorizonExceeded = "SCH_HORIZON_EXCEEDED";
}
