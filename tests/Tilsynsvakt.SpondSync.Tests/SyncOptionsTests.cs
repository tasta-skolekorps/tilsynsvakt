using Tilsynsvakt.SpondSync;

namespace Tilsynsvakt.SpondSync.Tests;

public sealed class SyncOptionsTests
{
    private static SyncOptions Options(string? dates = null, string? dryRun = null, string origin = "https://example.invalid") =>
        SyncOptions.FromEnvironment(key => key switch
        {
            "SPOND_USERNAME" => "test-placeholder",
            "SPOND_PASSWORD" => "test-placeholder",
            "API_BASE_URL" => origin,
            "SPOND_SYNC_DATES" => dates,
            "DRY_RUN" => dryRun,
            _ => null
        });

    [Fact]
    public void Options_DefaultsToDryRunAndRequiredGroupContext()
    {
        var options = Options();
        Assert.True(options.DryRun);
        Assert.Equal("Tasta Skolekorps - Medlemmer", options.GroupName);
        Assert.Equal("Tilsynsvakt", options.SubgroupName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Options_UnsetDateLimitFallsBackToFullCurrentWindow(string? dates)
    {
        var options = Options(dates);
        var today = new DateOnly(2026, 10, 5);
        Assert.Null(options.Dates);
        var selected = SyncCalendar.SelectDates(today, options.Dates);
        Assert.Equal(55, selected.Count);
        Assert.Contains(today, selected);
        Assert.Contains(new DateOnly(2026, 11, 28), selected);
        Assert.DoesNotContain(today.AddDays(-1), selected);
        Assert.DoesNotContain(new DateOnly(2026, 11, 29), selected);
    }

    [Theory]
    [InlineData("open", 0)]
    [InlineData("taken", 1)]
    public void Options_PilotRestrictsPlannerToSingleDate(string status, int expectedCount)
    {
        var options = Options("2026-11-26");
        var today = new DateOnly(2026, 10, 5);
        var pilot = new DateOnly(2026, 11, 26);
        var dates = SyncCalendar.SelectDates(today, options.Dates);
        Assert.Equal(pilot, Assert.Single(dates));
        var guard = new Guard(1, "Familien Test", "+4790000001");
        var group = new TargetGroup("test-group", "test-subgroup",
            [new ChildMember("test-child", ["test-subgroup"], [new Guardian("test-parent", "+4790000001", "test-parent")])]);
        var actions = SyncPlanner.Plan(
            [new(pilot, status, status == "taken" ? guard : null), new(pilot.AddDays(-1), "taken", guard)],
            [new("test-outside", SyncPlanner.Marker(pilot.AddDays(-1)), null)], group, dates, today);

        Assert.Equal(expectedCount, actions.Count);
        Assert.All(actions, action => Assert.Equal(pilot, action.Date));
        Assert.All(actions, action => Assert.Equal(SyncActionKind.Create, action.Kind));
    }

    [Fact]
    public void Options_MultipleDatesCanWidenPilotWithoutDuplicates()
    {
        var dates = Options(" 2026-11-26,2026-10-20,2026-11-26 ").Dates!;
        Assert.Equal(2, dates.Count);
        Assert.Contains(new DateOnly(2026, 10, 20), dates);
        Assert.Contains(new DateOnly(2026, 11, 26), dates);
    }

    [Theory]
    [InlineData("TRUE", true)]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("", true)]
    public void Options_DryRunParsesExplicitBooleanOrEmptyDefault(string value, bool expected) =>
        Assert.Equal(expected, Options(dryRun: value).DryRun);

    [Theory]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData(" false ")]
    public void Options_InvalidDryRunFailsClosed(string value) =>
        Assert.Throws<SyncException>(() => Options(dryRun: value));

    [Theory]
    [InlineData("26.11.2026")]
    [InlineData("2026-02-30")]
    [InlineData("2026-11-26,")]
    public void Options_InvalidPilotDatesFailClosed(string dates) =>
        Assert.Throws<SyncException>(() => Options(dates));

    [Theory]
    [InlineData("https://example.invalid/path")]
    [InlineData("https://example.invalid/?query=test")]
    [InlineData("https://example.invalid/#test")]
    [InlineData("file:///test")]
    public void Options_InvalidBackendOriginFailsClosed(string origin) =>
        Assert.Throws<SyncException>(() => Options(origin: origin));

    [Theory]
    [InlineData("SPOND_USERNAME")]
    [InlineData("SPOND_PASSWORD")]
    [InlineData("API_BASE_URL")]
    public void Options_MissingRequiredSettingNamesOnlyTheKey(string missing)
    {
        var error = Assert.Throws<SyncException>(() => SyncOptions.FromEnvironment(key =>
            key == missing ? null : key == "API_BASE_URL" ? "https://example.invalid" : "test-placeholder"));
        Assert.Equal("Mangler obligatorisk konfigurasjon: " + missing, error.Message);
        Assert.DoesNotContain("test-placeholder", error.Message);
    }
}