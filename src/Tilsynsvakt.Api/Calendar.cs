using System.Globalization;

namespace Tilsynsvakt.Api;

public sealed class ShiftCalendar
{
    private sealed record Period(int Start, int End, int StartMonth, int StartDay, int EndMonth, int EndDay, string SeasonKey);

    private readonly Period[] _periods;

    private ShiftCalendar(Period[] periods) => _periods = periods;

    public static ShiftCalendar FromConfiguration(IConfiguration periods)
    {
        var parsed = periods.GetChildren()
            .Select(child =>
            {
                var start = ParseMonthDay(child["Start"]);
                var end = ParseMonthDay(child["End"]);
                return new Period(
                    start.Month * 100 + start.Day,
                    end.Month * 100 + end.Day,
                    start.Month,
                    start.Day,
                    end.Month,
                    end.Day,
                    start.Month >= 8 ? "autumn" : "spring");
            })
            .ToArray();

        if (parsed.Length == 0 || parsed.Any(period => period.Start > period.End))
        {
            throw new InvalidOperationException("Calendar:Periods must contain at least one period with Start <= End.");
        }

        return new ShiftCalendar(parsed);
    }

    // Tuesday to Thursday inside a configured period; Monday is covered by the board and Friday has no activity.
    public bool IsShiftDay(DateOnly date)
    {
        if (date.DayOfWeek is not (DayOfWeek.Tuesday or DayOfWeek.Wednesday or DayOfWeek.Thursday))
        {
            return false;
        }

        var key = date.Month * 100 + date.Day;
        return _periods.Any(period => key >= period.Start && key <= period.End);
    }

    public bool IsAdminDutyDay(DateOnly date)
    {
        if (date.DayOfWeek is not (DayOfWeek.Monday or DayOfWeek.Tuesday or DayOfWeek.Wednesday or DayOfWeek.Thursday))
        {
            return false;
        }

        var key = date.Month * 100 + date.Day;
        return _periods.Any(period => key >= period.Start && key <= period.End);
    }

    public bool TryGetSeasonRange(string? season, int year, out DateOnly from, out DateOnly to)
    {
        var seasonKey = season?.Trim().ToLowerInvariant();
        var period = _periods.FirstOrDefault(candidate => string.Equals(candidate.SeasonKey, seasonKey, StringComparison.Ordinal));
        if (period is null)
        {
            from = default;
            to = default;
            return false;
        }

        from = new DateOnly(year, period.StartMonth, period.StartDay);
        to = new DateOnly(year, period.EndMonth, period.EndDay);
        return true;
    }

    private static DateOnly ParseMonthDay(string? value)
    {
        // 2000 is a leap year, so 02-29 stays valid.
        if (!DateOnly.TryParseExact($"2000-{value}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new InvalidOperationException($"Calendar period value '{value}' must be formatted MM-dd.");
        }

        return date;
    }
}

public static class Clock
{
    private static readonly TimeZoneInfo Oslo = TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo");

    public static DateOnly Today(TimeProvider time) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), Oslo).DateTime);

    public static DateTimeOffset ToUtc(DateOnly date, TimeOnly time)
    {
        var localDateTime = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(localDateTime, Oslo.GetUtcOffset(localDateTime)).ToUniversalTime();
    }

    public static TimeOnly ToLocalTime(DateTimeOffset utc) =>
        TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, Oslo).DateTime);
}
