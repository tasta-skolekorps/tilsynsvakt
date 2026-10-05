using System.Globalization;
using Tilsynsvakt.SpondSync;

namespace Tilsynsvakt.SpondSync.Tests;

public sealed class SyncCalendarTests
{
    [Theory]
    [InlineData("2026-10-20", "2026-10-20T14:40:00Z", "2026-10-20T14:45:00Z", "2026-10-20T20:00:00Z", "2026-10-13T14:45:00Z")]
    [InlineData("2026-11-26", "2026-11-26T15:40:00Z", "2026-11-26T15:45:00Z", "2026-11-26T21:00:00Z", "2026-11-19T15:45:00Z")]
    [InlineData("2026-10-27", "2026-10-27T15:40:00Z", "2026-10-27T15:45:00Z", "2026-10-27T21:00:00Z", "2026-10-20T14:45:00Z")]
    public void DesiredEvent_UsesOsloWallClockForMeetStartEndAndInvitation(
        string date, string meet, string start, string end, string invite)
    {
        var day = DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var desired = SyncPlanner.DesiredEvent(day, new("test-group", "test-subgroup", []), ["test-parent"]);
        Assert.Equal(Parse(meet), desired.Meet);
        Assert.Equal(Parse(start), desired.Start);
        Assert.Equal(Parse(end), desired.End);
        Assert.Equal(Parse(invite), desired.InviteAt);
        Assert.Equal(TimeSpan.Zero, desired.Start.Offset);
        var invitation = TimeZoneInfo.ConvertTime(desired.InviteAt, SyncCalendar.Oslo);
        Assert.Equal(day.AddDays(-7), DateOnly.FromDateTime(invitation.DateTime));
        Assert.Equal(new TimeOnly(16, 45), TimeOnly.FromDateTime(invitation.DateTime));
    }

    [Theory]
    [InlineData("2026-10-04T22:30:00Z", "2026-10-05")]
    [InlineData("2026-11-25T23:30:00Z", "2026-11-26")]
    public void Today_UsesOsloDateNotUtcDate(string instant, string date) =>
        Assert.Equal(DateOnly.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture), SyncCalendar.Today(Parse(instant)));

    [Theory]
    [InlineData("2026-01-05", "2026-05-29")]
    [InlineData("2026-05-29", "2026-05-29")]
    [InlineData("2026-09-01", "2026-11-28")]
    [InlineData("2026-11-28", "2026-11-28")]
    [InlineData("2026-01-04", null)]
    [InlineData("2026-05-30", null)]
    [InlineData("2026-08-31", null)]
    [InlineData("2026-11-29", null)]
    public void PeriodEnd_RespectsInclusiveSeasonBoundaries(string today, string? expected)
    {
        var date = DateOnly.ParseExact(today, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Equal(expected is null ? (DateOnly?)null : DateOnly.ParseExact(expected, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            SyncCalendar.PeriodEnd(date));
        if (expected is null) Assert.Empty(SyncCalendar.SelectDates(date, null));
    }

    [Fact]
    public void SelectDates_IntersectsLimitWithCurrentWindow()
    {
        var today = new DateOnly(2026, 10, 5);
        var pilot = new DateOnly(2026, 11, 26);
        var selected = SyncCalendar.SelectDates(today,
            new HashSet<DateOnly> { today.AddDays(-1), pilot, new(2026, 11, 29) });
        Assert.Equal(pilot, Assert.Single(selected));
        Assert.Empty(SyncCalendar.SelectDates(pilot.AddDays(1), new HashSet<DateOnly> { pilot }));
    }

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}