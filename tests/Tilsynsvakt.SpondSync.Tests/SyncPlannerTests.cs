using Tilsynsvakt.SpondSync;

namespace Tilsynsvakt.SpondSync.Tests;

public sealed class SyncPlannerTests
{
    private static readonly DateOnly Today = new(2026, 10, 5);
    private static readonly DateOnly PilotDate = new(2026, 11, 26);
    private static readonly Guard TestGuard = new(1, "Familien Test", "+4790000001");
    private static readonly TargetGroup Group = new("test-group", "test-subgroup",
        [new ChildMember("test-child", ["test-subgroup"],
            [new Guardian("test-parent-a", "+4790000001", "test-parent-a"), new Guardian("test-parent-b", null, "test-parent-b")])]);

    [Fact]
    public void OwnedDate_RoundTripsMarkerOnItsOwnDescriptionLine()
    {
        Assert.Equal(PilotDate, SyncPlanner.OwnedDate("Informasjon\r\n" + SyncPlanner.Marker(PilotDate)));
    }

    [Fact]
    public void Plan_ManualEventOnTakenDate_SkipsWithoutCreate()
    {
        var action = Assert.Single(SyncPlanner.Plan([new(PilotDate, "taken", TestGuard)], [], Group,
            new HashSet<DateOnly> { PilotDate }, Today, new HashSet<DateOnly> { PilotDate }));
        Assert.Equal(new SyncAction(PilotDate, SyncActionKind.SkipManualEvent, TestGuard.Name, null, null), action);
    }

    [Theory]
    [InlineData("taken")]
    [InlineData("open")]
    public void Plan_ManualAndOwnedOnSameDate_SkipsWithoutDelete(string status)
    {
        var desired = SyncPlanner.DesiredEvent(PilotDate, Group, ["test-other-profile"]);
        var action = Assert.Single(SyncPlanner.Plan([new(PilotDate, status, status == "taken" ? TestGuard : null)],
            [new("test-event", desired.Description, desired)], Group, new HashSet<DateOnly> { PilotDate }, Today,
            new HashSet<DateOnly> { PilotDate }));
        Assert.Equal(SyncActionKind.SkipManualEvent, action.Kind);
        Assert.Null(action.EventId);
    }

    [Fact]
    public void Plan_ManualEventWithoutShift_SkipsWithoutName()
    {
        var action = Assert.Single(SyncPlanner.Plan([], [], Group, new HashSet<DateOnly> { PilotDate }, Today,
            new HashSet<DateOnly> { PilotDate }));
        Assert.Equal(SyncActionKind.SkipManualEvent, action.Kind);
        Assert.Null(action.GuardName);
    }

    [Fact]
    public void Plan_UnchangedVerifiedEvent_IsIdempotent()
    {
        var desired = SyncPlanner.DesiredEvent(PilotDate, Group, ["test-parent-a", "test-parent-b"]);
        var existing = new ExistingEvent("test-event", desired.Description, desired);

        var actions = SyncPlanner.Plan([new(PilotDate, "taken", TestGuard)], [existing],
            Group, new HashSet<DateOnly> { PilotDate }, Today);

        Assert.Empty(actions);
    }

    [Fact]
    public void Plan_OnlyTakenShiftsCreateGuardianOnlyEvents()
    {
        var openDate = PilotDate.AddDays(-1);
        var action = Assert.Single(SyncPlanner.Plan(
            [new(PilotDate, "taken", TestGuard), new(openDate, "open", null)], [], Group,
            new HashSet<DateOnly> { PilotDate, openDate }, Today));

        Assert.Equal(SyncActionKind.Create, action.Kind);
        Assert.Equal(PilotDate, action.Date);
        Assert.Equal("Familien Test", action.GuardName);
        Assert.Null(action.EventId);
        var desired = Assert.IsType<EventSpec>(action.Desired);
        Assert.Equal(new[] { "test-parent-a", "test-parent-b" }, desired.GuardianIds);
        Assert.DoesNotContain("test-child", desired.GuardianIds);
        Assert.DoesNotContain(Group.SubgroupId, desired.GuardianIds);
        Assert.Equal(Group.Id, desired.GroupId);
        Assert.Equal(Group.SubgroupId, desired.SubgroupId);
        Assert.False(desired.AutoAccept);
        Assert.Equal("Tilsynsvakt Tasta Skole", desired.Heading);
        Assert.Equal("Tasta skole, Randabergveien, Stavanger", desired.Location);
        Assert.Equal("Se nettside https://tasta-skolekorps.github.io/tilsynsvakt/ for informasjon, evt. ta kontakt på 924 23 946 hvis sp\u00f8rsm\u00e5l. God vakt!\n[tilsynsvakt-sync:2026-11-26]", desired.Description);
    }

    [Fact]
    public void Plan_OpenShiftDeletesOnlyOwnedEvent()
    {
        var action = Assert.Single(SyncPlanner.Plan([new(PilotDate, "open", null)],
            [new("test-owned", SyncPlanner.Marker(PilotDate), null), new("test-unowned", "Informasjon", null)],
            Group, new HashSet<DateOnly> { PilotDate }, Today));

        Assert.Equal(SyncActionKind.Delete, action.Kind);
        Assert.Equal("test-owned", action.EventId);
        Assert.Null(action.Desired);
        Assert.Null(action.GuardName);
    }

    [Fact]
    public void Plan_ChangedGuardiansDeletesThenCreates()
    {
        var oldState = SyncPlanner.DesiredEvent(PilotDate, Group, ["test-previous-parent"]);
        var actions = SyncPlanner.Plan([new(PilotDate, "taken", TestGuard)],
            [new("test-event", oldState.Description, oldState)], Group,
            new HashSet<DateOnly> { PilotDate }, Today);

        Assert.Equal(2, actions.Count);
        Assert.Equal(SyncActionKind.Delete, actions[0].Kind);
        Assert.Equal("test-event", actions[0].EventId);
        var action = actions[1];
        Assert.Equal(SyncActionKind.Create, action.Kind);
        Assert.Null(action.EventId);
        Assert.Equal(new[] { "test-parent-a", "test-parent-b" }, action.Desired!.GuardianIds);
        Assert.DoesNotContain("test-previous-parent", action.Desired.GuardianIds);
    }

    [Fact]
    public void Plan_ReorderedGuardianIdsAreUnchanged()
    {
        var desired = SyncPlanner.DesiredEvent(PilotDate, Group, ["test-parent-a", "test-parent-b"]);
        var state = desired with { GuardianIds = new[] { "test-parent-b", "test-parent-a" } };

        Assert.Empty(SyncPlanner.Plan([new(PilotDate, "taken", TestGuard)],
            [new("test-event", state.Description, state)], Group,
            new HashSet<DateOnly> { PilotDate }, Today));
    }

    [Fact]
    public void Plan_UnverifiedExistingStateFailsClosed()
    {
        Assert.Throws<SyncException>(() => SyncPlanner.Plan([new(PilotDate, "taken", TestGuard)],
            [new("test-event", SyncPlanner.Marker(PilotDate), null)], Group,
            new HashSet<DateOnly> { PilotDate }, Today));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("taken")]
    public void Plan_UnmarkedEventsAreNeverUpdateOrDeleteTargets(string status)
    {
        var actions = SyncPlanner.Plan([new(PilotDate, status, status == "taken" ? TestGuard : null)],
            [new("test-unowned", "Informasjon", null)], Group, new HashSet<DateOnly> { PilotDate }, Today);

        Assert.All(actions, action => Assert.Equal(SyncActionKind.Create, action.Kind));
        Assert.All(actions, action => Assert.Null(action.EventId));
        Assert.Equal(status == "taken" ? 1 : 0, actions.Count);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("taken")]
    public void Plan_PastDatesAreUntouched(string status)
    {
        var past = Today.AddDays(-1);
        Assert.Empty(SyncPlanner.Plan([new(past, status, status == "taken" ? TestGuard : null)],
            [new("test-past", SyncPlanner.Marker(past), null)], Group,
            new HashSet<DateOnly> { past }, Today));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Plan_UnmatchedPhoneSkipsAndPreservesExistingEvent(bool hasExisting)
    {
        var unmatchedGroup = Group with { Members = System.Array.Empty<ChildMember>() };
        ExistingEvent[] existing = hasExisting ? [new("test-event", SyncPlanner.Marker(PilotDate), null)] : [];

        var action = Assert.Single(SyncPlanner.Plan([new(PilotDate, "taken", TestGuard)],
            existing, unmatchedGroup, new HashSet<DateOnly> { PilotDate }, Today));

        Assert.Equal(SyncActionKind.SkipPhoneNotFound, action.Kind);
        Assert.Equal("Familien Test", action.GuardName);
        Assert.Null(action.EventId);
        Assert.Null(action.Desired);
    }

    [Theory]
    [InlineData("+47 900 00 001")]
    [InlineData("90000001")]
    [InlineData("004790000001")]
    public void MatchGuardians_MatchesSupportedPhoneFormats(string phone)
    {
        Assert.Equal("+4790000001", SyncPlanner.NormalizePhone(phone));
        Assert.Equal(new[] { "test-parent-a", "test-parent-b" },
            SyncPlanner.MatchGuardians(TestGuard with { Phone = phone }, Group).ProfileIds);
    }

    [Fact]
    public void Plan_UnmatchedShiftDoesNotStopLaterAction()
    {
        var laterDate = PilotDate.AddDays(1);
        var actions = SyncPlanner.Plan([new(PilotDate, "taken", TestGuard), new(laterDate, "open", null)],
            [new("test-later-event", SyncPlanner.Marker(laterDate), null)],
            Group with { Members = System.Array.Empty<ChildMember>() },
            new HashSet<DateOnly> { PilotDate, laterDate }, Today);

        Assert.Collection(actions,
            action =>
            {
                Assert.Equal(PilotDate, action.Date);
                Assert.Equal(SyncActionKind.SkipPhoneNotFound, action.Kind);
            },
            action =>
            {
                Assert.Equal(laterDate, action.Date);
                Assert.Equal(SyncActionKind.Delete, action.Kind);
                Assert.Equal("test-later-event", action.EventId);
            });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("+4690000001")]
    [InlineData("9000000x")]
    [InlineData("+4710000001")]
    public void NormalizePhone_RejectsInvalidPhones(string? phone) =>
        Assert.Null(SyncPlanner.NormalizePhone(phone));

    [Fact]
    public void MatchGuardians_IgnoresMatchingFamilyOutsideSubgroup()
    {
        var outside = Group.Members[0] with { SubGroups = new[] { "test-other-subgroup" } };
        Assert.Equal(GuardianMatchOutcome.PhoneNotFound,
            SyncPlanner.MatchGuardians(TestGuard, Group with { Members = new[] { outside } }).Outcome);
    }

    [Fact]
    public void MatchGuardians_MatchingChildrenDeduplicateSharedProfiles()
    {
        var sibling = Group.Members[0] with { Id = "test-other-child", Guardians = [
            Group.Members[0].Guardians[0], new Guardian("test-parent-c", null, "test-parent-c")] };
        Assert.Equal(new[] { "test-parent-a", "test-parent-b", "test-parent-c" }, SyncPlanner.MatchGuardians(TestGuard,
            Group with { Members = new[] { Group.Members[0], sibling } }).ProfileIds);
    }

    [Theory]
    [InlineData("test-child")]
    [InlineData("")]
    public void MatchGuardians_UnsafeGuardianIdentifiersFailClosed(string otherGuardianId)
    {
        var child = Group.Members[0] with
        {
            Guardians = new[] { new Guardian("test-parent-a", "+4790000001", "test-parent-a"), new Guardian(otherGuardianId, null, otherGuardianId == "" ? "test-profile" : otherGuardianId) }
        };
        Assert.Throws<SyncException>(() => SyncPlanner.MatchGuardians(TestGuard,
            Group with { Members = new[] { child } }));
    }

    private static TargetGroup PartialGroup => Group with { Members = [Group.Members[0] with
        { Guardians = [Group.Members[0].Guardians[0], new Guardian("test-parent-b", null)] }] };

    [Fact]
    public void MatchGuardians_ReportsEachOutcome()
    {
        var notFound = SyncPlanner.MatchGuardians(TestGuard with { Phone = "+4790000009" }, Group);
        Assert.Equal(GuardianMatchOutcome.PhoneNotFound, notFound.Outcome);
        Assert.Empty(notFound.ProfileIds);

        var partial = SyncPlanner.MatchGuardians(TestGuard, PartialGroup);
        Assert.Equal(GuardianMatchOutcome.Matched, partial.Outcome);
        Assert.True(partial.MissingProfiles);
        Assert.Equal(new[] { "test-parent-a" }, partial.ProfileIds);

        var full = SyncPlanner.MatchGuardians(TestGuard, Group);
        Assert.Equal(GuardianMatchOutcome.Matched, full.Outcome);
        Assert.False(full.MissingProfiles);
    }

    [Fact]
    public void Plan_PartialProfilesInvitesProfiledGuardiansAndWarns()
    {
        var actions = SyncPlanner.Plan([new(PilotDate, "taken", TestGuard)], [], PartialGroup,
            new HashSet<DateOnly> { PilotDate }, Today);

        Assert.Collection(actions,
            action =>
            {
                Assert.Equal(SyncActionKind.WarnMissingProfiles, action.Kind);
                Assert.Equal("Familien Test", action.GuardName);
                Assert.Null(action.Desired);
            },
            action =>
            {
                Assert.Equal(SyncActionKind.Create, action.Kind);
                Assert.Equal(new[] { "test-parent-a" }, action.Desired!.GuardianIds);
            });
    }

    [Fact]
    public void Plan_PartialProfilesSecondRunMakesNoChanges()
    {
        var state = SyncPlanner.DesiredEvent(PilotDate, PartialGroup, ["test-parent-a"]);
        var action = Assert.Single(SyncPlanner.Plan([new(PilotDate, "taken", TestGuard)],
            [new("test-event", state.Description, state)], PartialGroup, new HashSet<DateOnly> { PilotDate }, Today));
        Assert.Equal(SyncActionKind.WarnMissingProfiles, action.Kind);
        Assert.Null(action.EventId);
    }

    [Fact]
    public void Plan_NoGuardianProfilesSkipsShift()
    {
        var child = Group.Members[0] with { Guardians = [new Guardian("test-parent-a", "+4790000001"), new Guardian("test-parent-b", null)] };
        Assert.Equal(GuardianMatchOutcome.NoProfiles, SyncPlanner.MatchGuardians(TestGuard, Group with { Members = [child] }).Outcome);
        var action = Assert.Single(SyncPlanner.Plan([new(PilotDate, "taken", TestGuard)],
            [new("test-event", SyncPlanner.Marker(PilotDate), null)], Group with { Members = [child] },
            new HashSet<DateOnly> { PilotDate }, Today));
        Assert.Equal(SyncActionKind.SkipNoProfiles, action.Kind);
        Assert.Equal("Familien Test", action.GuardName);
        Assert.Null(action.EventId);
    }

    [Fact]
    public void Plan_MetadataOnlyChangeDoesNotReplaceGuardians()
    {
        var state = SyncPlanner.DesiredEvent(PilotDate, Group, ["test-parent-a", "test-parent-b"]) with { Heading = "Test" };
        var action = Assert.Single(SyncPlanner.Plan([new(PilotDate, "taken", TestGuard)],
            [new("test-event", state.Description, state)], Group, new HashSet<DateOnly> { PilotDate }, Today));
        Assert.Equal(SyncActionKind.Update, action.Kind);
    }

    [Fact]
    public void Plan_MissingRosterRowCannotDeleteOwnedEvent() =>
        Assert.Throws<SyncException>(() => SyncPlanner.Plan([], [new("test-event", SyncPlanner.Marker(PilotDate), null)],
            Group, new HashSet<DateOnly> { PilotDate }, Today));

    [Fact]
    public void Plan_DuplicateRosterDatesFailClosed() =>
        Assert.Throws<SyncException>(() => SyncPlanner.Plan(
            [new(PilotDate, "taken", TestGuard), new(PilotDate, "open", null)], [],
            Group, new HashSet<DateOnly> { PilotDate }, Today));

    [Fact]
    public void Plan_DuplicateOwnedEventsFailClosed() =>
        Assert.Throws<SyncException>(() => SyncPlanner.Plan([new(PilotDate, "taken", TestGuard)],
            [new("test-event-a", SyncPlanner.Marker(PilotDate), null), new("test-event-b", SyncPlanner.Marker(PilotDate), null)],
            Group, new HashSet<DateOnly> { PilotDate }, Today));

    [Theory]
    [InlineData("open", true)]
    [InlineData("taken", false)]
    [InlineData("unknown", true)]
    public void Plan_InconsistentRosterFailsClosed(string status, bool hasGuard) =>
        Assert.Throws<SyncException>(() => SyncPlanner.Plan([new(PilotDate, status, hasGuard ? TestGuard : null)],
            [], Group, new HashSet<DateOnly> { PilotDate }, Today));

    [Theory]
    [InlineData(null)]
    [InlineData("[tilsynsvakt-sync:2026-02-30]")]
    [InlineData("[tilsynsvakt-sync:2026-11-26] suffix")]
    [InlineData("prefix [tilsynsvakt-sync:2026-11-26]")]
    [InlineData(" [tilsynsvakt-sync:2026-11-26]")]
    [InlineData("[tilsynsvakt-sync:26.11.2026]")]
    public void OwnedDate_NonExactMarkerDoesNotGrantOwnership(string? description) =>
        Assert.Null(SyncPlanner.OwnedDate(description));

    [Fact]
    public void OwnedDate_DuplicateMarkerLinesFailClosed() =>
        Assert.Throws<SyncException>(() => SyncPlanner.OwnedDate(
            SyncPlanner.Marker(PilotDate) + "\n" + SyncPlanner.Marker(PilotDate)));
}
