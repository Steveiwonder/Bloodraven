using System.Globalization;
using System.Text.RegularExpressions;

namespace Bloodraven;

public static class Scheduling
{
    public static DateTimeOffset Next(string timing, DateTimeOffset after)
    {
        var every = Regex.Match(timing, @"^every ([1-9][0-9]{0,5})([mhd])$");
        if (every.Success)
        {
            var minutes = int.Parse(every.Groups[1].Value, CultureInfo.InvariantCulture) *
                (every.Groups[2].Value == "d" ? 1440L : every.Groups[2].Value == "h" ? 60L : 1L);
            if (minutes > 525600) throw new ArgumentException("Maximum interval is one year.");
            return after.AddMinutes(minutes);
        }
        var parts = timing.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var weekly = parts.Length == 4 && parts[0] == "weekly";
        if (!weekly && !(parts.Length == 3 && parts[0] == "daily"))
            throw new ArgumentException("Use every 30m, daily 08:00 Europe/London, or weekly sat 08:00 Europe/London.");
        int? day = weekly ? Array.IndexOf(new[] { "sun", "mon", "tue", "wed", "thu", "fri", "sat" }, parts[1]) : null;
        if (day == -1 || !TimeOnly.TryParseExact(parts[weekly ? 2 : 1], "HH:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var time)) throw new ArgumentException("Invalid day or time; use HH:mm.");
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(parts[^1]); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new ArgumentException("Unknown timezone. Example: Europe/London or UTC."); }
        var date = TimeZoneInfo.ConvertTime(after, zone).Date;
        for (var i = 0; i < 15; i++)
        {
            var local = DateTime.SpecifyKind(date.AddDays(i).Add(time.ToTimeSpan()), DateTimeKind.Unspecified);
            if (day.HasValue && (int)local.DayOfWeek != day.Value || zone.IsInvalidTime(local)) continue;
            // Once per local date, including the repeated hour when clocks go back.
            var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
            var next = new DateTimeOffset(local, offset).ToUniversalTime();
            if (next > after) return next;
        }
        throw new ArgumentException("No upcoming occurrence found.");
    }

    public static void EnqueueDue(JournalData data, DateTimeOffset now, bool defaultApproval)
    {
        for (var i = 0; i < data.Schedules.Count && data.Jobs.Count < 20; i++)
        {
            var schedule = data.Schedules[i];
            if (schedule.Paused || schedule.NextRun > now) continue;
            if (!data.Jobs.Any(j => j.ScheduleId == schedule.Id))
                data.Jobs.Add(new Job(data.NextScheduledId--, schedule.ChatId, schedule.Prompt,
                    Conversation: schedule.Conversation, ScheduleId: schedule.Id,
                    ApprovalRequired: data.Approvals ?? defaultApproval));
            data.Schedules[i] = schedule with { NextRun = Next(schedule.Timing, now) };
        }
    }
}
