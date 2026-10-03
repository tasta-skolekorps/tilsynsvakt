using System.Globalization;

namespace Tilsynsvakt.Api;

public sealed class ShiftCalendar
{
    private readonly (int Start, int End)[] _periods;

    private ShiftCalendar((int Start, int End)[] periods) => _periods = periods;

    public static ShiftCalendar FromConfiguration(IConfiguration periods)
    {
        var parsed = periods.GetChildren()
            .Select(child => (Start: ParseMonthDay(child["Start"]), End: ParseMonthDay(child["End"])))
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

    private static int ParseMonthDay(string? value)
    {
        // 2000 is a leap year, so 02-29 stays valid.
        if (!DateOnly.TryParseExact($"2000-{value}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new InvalidOperationException($"Calendar period value '{value}' must be formatted MM-dd.");
        }

        return date.Month * 100 + date.Day;
    }
}

public static class Clock
{
    private static readonly TimeZoneInfo Oslo = TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo");

    public static DateOnly Today(TimeProvider time) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), Oslo).DateTime);
}
