using Microsoft.Extensions.Configuration;
using Tilsynsvakt.Api;

namespace Tilsynsvakt.Api.Tests;

public sealed class ShiftCalendarTests
{
    [Fact]
    public void Closed_range_removes_shift_and_duty_days()
    {
        var periods = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["0:Start"] = "09-01",
            ["0:End"] = "11-28",
        }).Build();
        var closed = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["0:From"] = "2026-10-05",
            ["0:To"] = "2026-10-11",
        }).Build();

        var calendar = ShiftCalendar.FromConfiguration(periods, closed);

        Assert.False(calendar.IsShiftDay(new DateOnly(2026, 10, 6)));
        Assert.False(calendar.IsAdminDutyDay(new DateOnly(2026, 10, 5)));
        Assert.True(calendar.IsShiftDay(new DateOnly(2026, 10, 13)));
        Assert.True(calendar.IsShiftDay(new DateOnly(2026, 9, 29)));
    }
}
