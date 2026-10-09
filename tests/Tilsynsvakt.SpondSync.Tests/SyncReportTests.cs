using Tilsynsvakt.SpondSync;

namespace Tilsynsvakt.SpondSync.Tests;

public sealed class SyncReportTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);
    private static readonly TargetGroup Group = new("test-group", "test-subgroup", [
        new ChildMember("test-child", ["test-subgroup"], [
            new Guardian("test-guardian", "+4790000001", "test-profile", "test-placeholder@example.invalid")])]);
    private static readonly Guard Kari = new(1, "Kari Test", "+4790000001");
    private static readonly Guard Ola = new(2, "Ola Test", "+4790000009");
    private static DateOnly D(int day) => new(2026, 11, day);
    private static EventSpec Spec(int day) => SyncPlanner.DesiredEvent(D(day), Group, ["test-profile"]);

    private static (IReadOnlyList<SyncAction> Actions, IReadOnlyList<DateOutcome> Outcomes) Scenario()
    {
        RosterShift[] roster = [
            new(D(17), "taken", Kari), new(D(18), "taken", Kari), new(D(19), "open", null),
            new(D(20), "open", null), new(D(21), "taken", Kari), new(D(22), "taken", Kari), new(D(23), "taken", Ola)];
        ExistingEvent[] existing = [
            new("test-e18", Spec(18).Description, Spec(18)),
            new("test-e20", Spec(20).Description, null),
            new("test-e21", Spec(21).Description, Spec(21) with { Heading = "Old" }),
            new("test-e22", Spec(22).Description, Spec(22) with { GuardianIds = ["test-old-a", "test-old-b"] })];
        var dates = Enumerable.Range(17, 7).Select(D).ToHashSet();
        var actions = SyncPlanner.Plan(roster, existing, Group, dates, Today);
        return (actions, SyncReport.Outcomes(actions, roster, dates, Today));
    }

    private static List<string> Render(bool dryRun)
    {
        var (actions, outcomes) = Scenario();
        var lines = new List<string>();
        var log = new SyncLog(outcomes, dryRun, lines.Add);
        log.Start();
        foreach (var action in actions) log.Reported(action);
        log.Finish();
        return lines;
    }

    [Fact]
    public void DryRun_OneLinePerDateAndSummary() => Assert.Equal(new[]
    {
        "2026-11-17 vil opprette – Kari Test",
        "2026-11-18 uendret – Kari Test",
        "2026-11-19 ingen vakt",
        "2026-11-20 vil slette",
        "2026-11-21 vil oppdatere (tittel) – Kari Test",
        "2026-11-22 vil erstatte (ny vakt) – Kari Test",
        "::warning::2026-11-23 telefonnummeret finnes ikke hos noen foresatt i undergruppen; vakt hoppet over – Ola Test",
        "Oppsummering: vil opprette 1, vil oppdatere 2, vil slette 1, 1 uendret, 1 hoppet over"
    }, Render(true));

    [Fact]
    public void RealRun_UsesPerformedWording() => Assert.Equal(new[]
    {
        "2026-11-17 opprettet – Kari Test",
        "2026-11-18 uendret – Kari Test",
        "2026-11-19 ingen vakt",
        "2026-11-20 slettet",
        "2026-11-21 oppdatert (tittel) – Kari Test",
        "2026-11-22 erstattet – Kari Test",
        "::warning::2026-11-23 telefonnummeret finnes ikke hos noen foresatt i undergruppen; vakt hoppet over – Ola Test",
        "Oppsummering: 1 opprettet, 2 oppdatert, 1 slettet, 1 uendret, 1 hoppet over"
    }, Render(false));

    [Fact]
    public void Replace_IsOneLineEmittedOnlyAfterBothWrites()
    {
        var (actions, outcomes) = Scenario();
        Assert.Equal(DateOutcomeKind.Replace, outcomes.Single(outcome => outcome.Date == D(22)).Kind);
        var lines = new List<string>();
        var log = new SyncLog(outcomes, false, lines.Add);
        log.Start();
        foreach (var action in actions.TakeWhile(action => action.Date < D(22))) log.Reported(action);
        log.Reported(actions.Single(action => action.Date == D(22) && action.Kind == SyncActionKind.Delete));
        Assert.DoesNotContain(lines, line => line.StartsWith("2026-11-22", StringComparison.Ordinal));
        log.Reported(actions.Single(action => action.Date == D(22) && action.Kind == SyncActionKind.Create));
        Assert.Single(lines, line => line.StartsWith("2026-11-22", StringComparison.Ordinal));
        Assert.Contains("2026-11-22 erstattet – Kari Test", lines);
    }

    [Fact]
    public void RealRun_FailedWriteStopsBeforePendingDate()
    {
        var (actions, outcomes) = Scenario();
        var lines = new List<string>();
        var log = new SyncLog(outcomes, false, lines.Add);
        log.Start();
        Assert.Empty(lines);
        log.Reported(actions[0]);
        Assert.Equal(new[] { "2026-11-17 opprettet – Kari Test", "2026-11-18 uendret – Kari Test", "2026-11-19 ingen vakt" }, lines);
    }

    [Fact]
    public void ChangedFields_ListsNamesAndGuardianCountsOnly()
    {
        var desired = Spec(21);
        var prior = desired with
        {
            Heading = "Old", Description = "Old", Start = desired.Start.AddMinutes(15),
            GuardianIds = ["test-a", "test-b"]
        };
        var fields = SyncReport.ChangedFields(prior, desired);
        Assert.Equal(new[] { "tittel", "beskrivelse", "tidspunkt", "foresatte (2→1)" }, fields);
        var line = Assert.Single(SyncReport.Lines(new(D(21), DateOutcomeKind.Update, "Kari Test", null, fields), true));
        Assert.Equal("2026-11-21 vil oppdatere (tittel, beskrivelse, tidspunkt, foresatte (2→1)) – Kari Test", line);
        Assert.DoesNotContain("test-", line);
    }

    [Theory]
    [InlineData(DateOutcomeKind.Create, true, "vil opprette")]
    [InlineData(DateOutcomeKind.Create, false, "opprettet")]
    [InlineData(DateOutcomeKind.Update, true, "vil oppdatere")]
    [InlineData(DateOutcomeKind.Update, false, "oppdatert")]
    [InlineData(DateOutcomeKind.Delete, true, "vil slette")]
    [InlineData(DateOutcomeKind.Delete, false, "slettet")]
    [InlineData(DateOutcomeKind.Replace, true, "vil erstatte (ny vakt)")]
    [InlineData(DateOutcomeKind.Replace, false, "erstattet")]
    [InlineData(DateOutcomeKind.Unchanged, true, "uendret")]
    [InlineData(DateOutcomeKind.Unchanged, false, "uendret")]
    [InlineData(DateOutcomeKind.NoShift, true, "ingen vakt")]
    [InlineData(DateOutcomeKind.NoShift, false, "ingen vakt")]
    public void Lines_UseDryOrRealWording(DateOutcomeKind kind, bool dryRun, string label) =>
        Assert.Equal($"2026-11-26 {label}", Assert.Single(SyncReport.Lines(new(D(26), kind, null), dryRun)));

    [Fact]
    public void PartialProfiles_WarnsThenReportsDateLine()
    {
        var lines = SyncReport.Lines(new(D(26), DateOutcomeKind.Unchanged, "Kari Test",
            SyncActionKind.WarnMissingProfiles), false).ToArray();
        Assert.Equal(new[]
        {
            "::warning::2026-11-26 én eller flere foresatte mangler Spond-profil og ble ikke invitert – Kari Test",
            "2026-11-26 uendret – Kari Test"
        }, lines);
    }

    [Fact]
    public void Header_ShowsModeAndWindow()
    {
        var dates = new HashSet<DateOnly> { D(26), D(17) };
        Assert.Equal("Tørrkjøring – ingen endringer sendes til Spond (2026-11-17 – 2026-11-26)", SyncReport.Header(true, dates));
        Assert.Equal("Skriver til Spond (2026-11-17 – 2026-11-26)", SyncReport.Header(false, dates));
    }

    [Fact]
    public void Lines_NeverContainSpondIdsOrContactData()
    {
        foreach (var line in Render(true).Concat(Render(false)))
        {
            Assert.DoesNotContain("test-", line);
            Assert.DoesNotContain("+47", line);
            Assert.DoesNotContain("@", line);
        }
    }
}
