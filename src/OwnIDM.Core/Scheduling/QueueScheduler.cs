namespace OwnIDM.Core.Scheduling;

public enum CompletionAction
{
    None,
    ExitApp,
    Sleep,
    Shutdown,
}

public enum ScheduledAction
{
    None,
    StartQueue,
    StopQueue,
}

/// <summary>When the main queue starts/stops by itself and what happens when it finishes.</summary>
public sealed class QueueSchedule
{
    public bool StartEnabled { get; set; }
    public TimeOnly StartTime { get; set; } = new(2, 0);

    /// <summary>True: every selected weekday. False: once, on <see cref="OnceDate"/>.</summary>
    public bool Daily { get; set; } = true;

    public List<DayOfWeek> Days { get; set; } = Enum.GetValues<DayOfWeek>().ToList();
    public DateOnly OnceDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

    public bool StopEnabled { get; set; }
    public TimeOnly StopTime { get; set; } = new(7, 0);

    public CompletionAction OnComplete { get; set; } = CompletionAction.None;

    /// <summary>Occurrences already handled, so a time fires once even across restarts.</summary>
    public DateTime? LastStartFired { get; set; }
    public DateTime? LastStopFired { get; set; }
}

public static class QueueScheduler
{
    /// <summary>A scheduled time still fires if the check runs a little late (timer hiccups, sleep).</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Returns the action due at <paramref name="now"/> and records it as fired.
    /// Call it periodically (every few seconds).
    /// </summary>
    public static ScheduledAction Evaluate(QueueSchedule s, DateTime now)
    {
        // Yesterday is checked too, so 23:59 still fires if the check runs just after midnight.
        var today = DateOnly.FromDateTime(now);
        foreach (var day in new[] { today, today.AddDays(-1) })
        {
            if (!s.StopEnabled)
                break;
            // The stop time applies every day: a night run may cross midnight.
            var stop = Occurrence(day, s.StopTime);
            if (IsDue(stop, now, s.LastStopFired))
            {
                s.LastStopFired = stop;
                return ScheduledAction.StopQueue;
            }
        }
        foreach (var day in new[] { today, today.AddDays(-1) })
        {
            if (s.StartEnabled && StartOn(s, day) is { } start && IsDue(start, now, s.LastStartFired))
            {
                s.LastStartFired = start;
                return ScheduledAction.StartQueue;
            }
        }
        return ScheduledAction.None;
    }

    /// <summary>Next start time strictly after <paramref name="now"/>, or null if none is scheduled.</summary>
    public static DateTime? NextStart(QueueSchedule s, DateTime now)
    {
        if (!s.StartEnabled)
            return null;
        var today = DateOnly.FromDateTime(now);
        for (int d = 0; d <= 7; d++)
            if (StartOn(s, today.AddDays(d)) is { } start && start > now)
                return start;
        return null;
    }

    private static DateTime? StartOn(QueueSchedule s, DateOnly day)
    {
        bool runs = s.Daily ? s.Days.Contains(day.DayOfWeek) : day == s.OnceDate;
        return runs ? Occurrence(day, s.StartTime) : null;
    }

    /// <summary>
    /// Local time of <paramref name="time"/> on <paramref name="day"/>. A time skipped by the
    /// daylight-saving jump (02:30 when clocks go from 02:00 to 03:00) moves to just after it.
    /// </summary>
    private static DateTime Occurrence(DateOnly day, TimeOnly time)
    {
        var at = day.ToDateTime(time);
        while (TimeZoneInfo.Local.IsInvalidTime(at))
            at = at.AddMinutes(30);
        return at;
    }

    private static bool IsDue(DateTime occurrence, DateTime now, DateTime? lastFired) =>
        now >= occurrence && now - occurrence < Tolerance && lastFired != occurrence;
}
