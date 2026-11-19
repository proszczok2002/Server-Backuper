using System.Globalization;

namespace ServerBackuper.Core;

public static class ScheduleCalculator
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss";

    public static DateTime ParseFirstRun(ScheduleSettings schedule)
    {
        if (!DateTime.TryParseExact(schedule.FirstRunLocal, Format, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var first))
            throw new InvalidDataException("Data pierwszego uruchomienia musi mieć format RRRR-MM-DD GG:mm.");
        return DateTime.SpecifyKind(first, DateTimeKind.Unspecified);
    }

    public static DateTime? GetLatestDue(ScheduleSettings schedule, DateTime nowLocal)
    {
        if (!schedule.Enabled) return null;
        ValidateInterval(schedule.IntervalHours);
        var first = ParseFirstRun(schedule);
        if (nowLocal < first) return null;
        var count = (long)Math.Floor((nowLocal - first).TotalHours / schedule.IntervalHours);
        return first.AddHours(count * schedule.IntervalHours);
    }

    public static DateTime? GetNext(ScheduleSettings schedule, DateTime nowLocal)
    {
        if (!schedule.Enabled) return null;
        ValidateInterval(schedule.IntervalHours);
        var first = ParseFirstRun(schedule);
        if (nowLocal < first) return first;
        return GetLatestDue(schedule, nowLocal)!.Value.AddHours(schedule.IntervalHours);
    }

    public static void ValidateInterval(int intervalHours)
    {
        if (intervalHours is < 1 or > 8760)
            throw new InvalidDataException("Odstęp musi wynosić od 1 do 8760 godzin.");
    }
}
