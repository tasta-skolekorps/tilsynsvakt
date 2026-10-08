using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Tilsynsvakt.SpondSync;

namespace Tilsynsvakt.SpondSync.Tests;

public sealed class SpondContractTests
{
    private static readonly DateOnly Date = new(2026, 11, 26);
    private static readonly TargetGroup Group = new("test-group", "test-subgroup", [
        new ChildMember("test-child", ["test-subgroup"], [
            new Guardian("test-guardian", "+4790000001", "test-profile", "test-placeholder@example.invalid")])]);
    private static EventSpec Desired => SyncPlanner.DesiredEvent(Date, Group, ["test-profile"]);

    [Fact]
    public void Create_UsesVerifiedGuardianOnlyShape()
    {
        var payload = SpondPayloads.Create(Desired, Group, "test-owner");
        Assert.Equal("Tilsynsvakt Tasta Skole", payload.GetProperty("heading").GetString());
        Assert.Equal(Desired.Description, payload.GetProperty("description").GetString());
        Assert.Equal("event", payload.GetProperty("spondType").GetString());
        Assert.Equal("EVENT", payload.GetProperty("type").GetString());
        Assert.Equal("5", payload.GetProperty("meetupPrior").GetString());
        Assert.Equal("2026-11-26T15:45:00Z", payload.GetProperty("startTimestamp").GetString());
        Assert.Equal("2026-11-26T21:00:00Z", payload.GetProperty("endTimestamp").GetString());
        Assert.Equal("2026-11-19T15:45:00Z", payload.GetProperty("inviteTime").GetString());
        Assert.False(payload.GetProperty("autoAccept").GetBoolean());
        Assert.Equal("REMIND_48H_BEFORE", payload.GetProperty("autoReminderType").GetString());
        Assert.True(payload.GetProperty("commentsDisabled").GetBoolean());
        Assert.True(payload.GetProperty("participantsHidden").GetBoolean());
        Assert.Equal("INVITEES", payload.GetProperty("visibility").GetString());
        Assert.Equal("test-owner", Assert.Single(payload.GetProperty("owners").EnumerateArray()).GetProperty("id").GetString());
        var recipients = payload.GetProperty("recipients");
        Assert.Empty(recipients.GetProperty("groupMembers").EnumerateArray());
        Assert.False(recipients.TryGetProperty("profiles", out _));
        Assert.Equal("test-subgroup", Assert.Single(recipients.GetProperty("group").GetProperty("subGroups").EnumerateArray()).GetString());
        var guardian = Assert.Single(recipients.GetProperty("guardians").EnumerateArray());
        Assert.Equal(new[] { "email", "phoneNumber", "profileId" }, guardian.EnumerateObject().Select(property => property.Name));
        Assert.Equal("test-profile", guardian.GetProperty("profileId").GetString());
        Assert.Equal("+4790000001", guardian.GetProperty("phoneNumber").GetString());
        Assert.Equal("test-placeholder@example.invalid", guardian.GetProperty("email").GetString());
        Assert.DoesNotContain("test-child", payload.GetRawText());
        Assert.DoesNotContain("test-guardian", payload.GetRawText());
        Assert.Equal(0, payload.GetProperty("maxAccepted").GetInt32());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("rsvpDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("matchInfo").ValueKind);
        Assert.Empty(payload.GetProperty("attachments").EnumerateArray());
        Assert.Empty(payload.GetProperty("tasks").GetProperty("openTasks").EnumerateArray());
        Assert.Empty(payload.GetProperty("tasks").GetProperty("assignedTasks").EnumerateArray());
    }

    [Fact]
    public void AddRecipients_UsesObjectSubgroupsUnlikeCreate()
    {
        var payload = SpondPayloads.AddRecipients(Desired, Group);
        Assert.Empty(payload.GetProperty("profiles").EnumerateArray());
        Assert.Empty(payload.GetProperty("groupMembers").EnumerateArray());
        Assert.Equal("test-subgroup", Assert.Single(payload.GetProperty("group").GetProperty("subGroups").EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal("test-profile", Assert.Single(payload.GetProperty("guardians").EnumerateArray()).GetProperty("profileId").GetString());
    }

    [Fact]
    public void Delete_UsesQuietUrlAndEscapesIdentifier() =>
        Assert.Equal("https://api.spond.com/core/v1/sponds/test%2Fevent?quiet=true", SpondPayloads.QuietDeleteUrl("test/event").AbsoluteUri);

    [Fact]
    public void Create_RejectsMissingProfileOrOwner()
    {
        Assert.Throws<SyncException>(() => SpondPayloads.Create(Desired, Group, ""));
        Assert.Throws<SyncException>(() => SpondPayloads.Create(Desired, Group with { Members = [] }, "test-owner"));
    }

    [Theory]
    [InlineData("2026-10-27", "2026-10-20T14:45:00Z")]
    [InlineData("2026-11-26", "2026-11-19T15:45:00Z")]
    public void Create_InvitationUsesSevenOsloWallClockDays(string day, string inviteTime)
    {
        var desired = SyncPlanner.DesiredEvent(DateOnly.ParseExact(day, "yyyy-MM-dd"), Group, ["test-profile"]);
        Assert.Equal(inviteTime, SpondPayloads.Create(desired, Group, "test-owner").GetProperty("inviteTime").GetString());
    }
    [Fact]
    public void Metadata_PreservesInviteAndSettingsWithoutRecipients()
    {
        var source = SpondPayloads.Create(SyncPlanner.DesiredEvent(Date, Group, ["test-profile"]), Group, "test-owner");
        var fields = source.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone());
        fields["id"] = JsonSerializer.SerializeToElement("test-event");
        fields["location"] = JsonSerializer.SerializeToElement(new { id = "test-location", feature = SyncPlanner.Location });
        var payload = SpondPayloads.Metadata(JsonSerializer.SerializeToElement(fields), "Test", SyncPlanner.Marker(Date));
        Assert.False(payload.TryGetProperty("recipients", out _));
        Assert.False(payload.TryGetProperty("meetupPrior", out _));
        Assert.False(payload.TryGetProperty("maxAccepted", out _));
        Assert.False(payload.TryGetProperty("type", out _));
        Assert.Equal("EVENT", payload.GetProperty("spondType").GetString());
        Assert.Equal(source.GetProperty("inviteTime").GetString(), payload.GetProperty("inviteTime").GetString());
        foreach (var field in new[] { "location", "owners", "tasks", "attachments", "autoReminderType", "autoAccept" })
            Assert.Equal(fields[field].GetRawText(), payload.GetProperty(field).GetRawText());
    }

    private static JsonElement EventJson(string id = "test-event", string profileId = "test-profile", string heading = SyncPlanner.Heading)
    {
        var fields = SpondPayloads.Create(Desired, Group, "test-owner").EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());
        fields["id"] = JsonSerializer.SerializeToElement(id);
        fields["heading"] = JsonSerializer.SerializeToElement(heading);
        fields["meetupPrior"] = JsonSerializer.SerializeToElement(5);
        fields["location"] = JsonSerializer.SerializeToElement(new { id = "test-location", feature = SyncPlanner.Location });
        fields["recipients"] = JsonSerializer.SerializeToElement(new
        {
            group = new { id = Group.Id, subGroups = new[] { new { id = Group.SubgroupId, name = "Test subgroup" } } },
            profiles = System.Array.Empty<object>(), members = System.Array.Empty<object>(),
            guardians = new[] { new { profileId } }
        });
        return JsonSerializer.SerializeToElement(fields);
    }

    private sealed class MockHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }

    private static HttpResponseMessage Json(object payload) => new(HttpStatusCode.OK) { Content = JsonContent.Create(payload) };
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 13, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task GroupRead_UsesProfileIdNotGuardianIdAndStringMembership()
    {
        using var handler = new MockHandler(request =>
        {
            Assert.Equal("/core/v1/groups", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json(new[] { new { id = Group.Id, name = "Test group", subGroups = new[] {
                new { id = Group.SubgroupId, name = "Test subgroup" } }, members = new[] {
                new { id = "test-child", subGroups = new[] { Group.SubgroupId }, guardians = new[] {
                    new { id = "test-guardian", phoneNumber = "+4790000001", email = "test-placeholder@example.invalid",
                        profile = new { id = "test-profile", phoneNumber = "+4790000001" } } } } } } }));
        });
        using var http = new HttpClient(handler);
        var group = await new SpondClient(http).GetTargetGroupAsync("Test group", "Test subgroup");
        Assert.Equal("test-profile", Assert.Single(SyncPlanner.MatchGuardians(new(1, "Test guard", "+4790000001"), group).ProfileIds));
    }

    [Fact]
    public async Task ListRead_UsesVerifiedQueryAndProducesIdempotentPlan()
    {
        using var handler = new MockHandler(request =>
        {
            var query = request.RequestUri!.Query;
            Assert.Contains("scheduled=true", query);
            Assert.Contains("includeComments=true", query);
            Assert.Contains("includeHidden=false", query);
            Assert.Contains("addProfileInfo=true", query);
            Assert.Contains("order=asc&max=100", query);
            Assert.Contains("groupId=" + Uri.EscapeDataString(Group.Id), query);
            Assert.Contains("minStartTimestamp=" + Uri.EscapeDataString("2026-11-25T23:00:00Z"), query);
            Assert.Contains("maxStartTimestamp=" + Uri.EscapeDataString("2026-11-26T23:00:00Z"), query);
            Assert.DoesNotContain("minEndTimestamp", query);
            Assert.DoesNotContain("maxEndTimestamp", query);
            return Task.FromResult(Json(new[] { EventJson() }));
        });
        using var http = new HttpClient(handler);
        var existing = await new SpondClient(http).GetEventsAsync(Group.Id, new HashSet<DateOnly> { Date }, now: Now);
        Assert.Single(existing);
        Assert.Empty(SyncPlanner.Plan([new(Date, "taken", new(1, "Test guard", "+4790000001"))], existing,
            Group, new HashSet<DateOnly> { Date }, SyncCalendar.Today(Now)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Execute_GuardChangeUsesQuietDeleteThenCreateOrDryRun(bool dryRun)
    {
        var existing = new ExistingEvent("test-event", Desired.Description, Desired with { GuardianIds = ["test-old-profile"] });
        var actions = SyncPlanner.Plan([new(Date, "taken", new(1, "Test guard", "+4790000001"))], [existing],
            Group, new HashSet<DateOnly> { Date }, SyncCalendar.Today(Now));
        var writes = new List<string>();
        var requests = 0;
        using var handler = new MockHandler(async request =>
        {
            requests++;
            if (request.Method == HttpMethod.Get)
                return request.RequestUri!.AbsolutePath.EndsWith("/profile", StringComparison.Ordinal)
                    ? Json(new { id = "test-owner" }) : Json(EventJson(profileId: "test-old-profile"));
            writes.Add(request.Method.Method);
            if (request.Method == HttpMethod.Delete)
                Assert.Equal("?quiet=true", request.RequestUri!.Query);
            else
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/core/v1/sponds", request.RequestUri!.AbsolutePath);
                var payload = await request.Content!.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("test-profile", payload.GetProperty("recipients").GetProperty("guardians")[0].GetProperty("profileId").GetString());
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var http = new HttpClient(handler);
        var reported = new List<SyncAction>();
        await new SpondClient(http).ExecutePlanAsync(actions, Group, dryRun, Now, reported.Add);
        Assert.Equal(actions, reported);
        if (dryRun) Assert.Equal(0, requests);
        else Assert.Equal(new[] { "DELETE", "POST" }, writes);
    }

    [Theory]
    [InlineData(SyncActionKind.SkipPhoneNotFound)]
    [InlineData(SyncActionKind.SkipNoProfiles)]
    [InlineData(SyncActionKind.WarnMissingProfiles)]
    public async Task Execute_SkipOrWarningOnlyReportsWithoutRequests(SyncActionKind kind)
    {
        using var handler = new MockHandler(_ => throw new InvalidOperationException("Unexpected mock request"));
        using var http = new HttpClient(handler);
        var reported = new List<SyncAction>();
        await new SpondClient(http).ExecutePlanAsync([new(Date, kind, "Test guard", null, null)],
            Group, false, Now, reported.Add);
        Assert.Equal(kind, Assert.Single(reported).Kind);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Execute_UnknownOrChangedRecipientsStopsBeforeWrites(bool replacement, bool changed)
    {
        await AssertUnsafeFreshStateAsync(replacement, changed ? "guardian" : "unknown", 1);
        await AssertUnsafeFreshStateAsync(replacement, changed ? "guardian" : "unknown", 2);
    }

    [Theory]
    [InlineData(false, "subgroup")]
    [InlineData(true, "subgroup")]
    [InlineData(false, "groupMembers")]
    [InlineData(true, "groupMembers")]
    [InlineData(false, "heading")]
    [InlineData(true, "heading")]
    [InlineData(false, "end")]
    [InlineData(true, "end")]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    [InlineData(false, "malformed")]
    [InlineData(true, "malformed")]
    public async Task Execute_ChangedScopeOrPriorSpecStopsBeforeWrites(bool replacement, string condition)
    {
        await AssertUnsafeFreshStateAsync(replacement, condition, 1);
        await AssertUnsafeFreshStateAsync(replacement, condition, 2);
    }

    private static async Task AssertUnsafeFreshStateAsync(bool replacement, string condition, int changedRead)
    {
        var priorJson = EventJson(profileId: replacement ? "test-old-profile" : "test-profile", heading: "Test old heading");
        var prior = SpondClient.ReadState(priorJson)!;
        var actions = SyncPlanner.Plan([new(Date, "taken", new(1, "Test guard", "+4790000001"))],
            [new("test-event", prior.Description, prior)], Group, new HashSet<DateOnly> { Date }, SyncCalendar.Today(Now));
        Assert.Equal(prior, actions[0].PriorState);
        var recipients = priorJson.GetProperty("recipients");
        var unsafeJson = condition switch
        {
            "unknown" => WithFields(priorJson, ("recipients", WithFields(recipients,
                ("guardians", new[] { new { id = "test-unknown-shape" } })))),
            "empty" => WithFields(priorJson, ("recipients", WithFields(recipients,
                ("guardians", System.Array.Empty<object>())))),
            "malformed" => WithFields(priorJson, ("recipients", WithFields(recipients,
                ("guardians", new { profileId = "test-profile" })))),
            "guardian" => WithFields(priorJson, ("recipients", WithFields(recipients,
                ("guardians", new[] { new { profileId = "test-changed-profile" } })))),
            "subgroup" => WithFields(priorJson, ("recipients", WithFields(recipients,
                ("group", new { id = Group.Id, subGroups = new[] { new { id = "test-other-subgroup" } } })))),
            "groupMembers" => WithFields(priorJson, ("recipients", WithFields(recipients,
                ("groupMembers", new[] { new { } })))),
            "heading" => WithFields(priorJson, ("heading", "Test concurrently changed heading")),
            _ => WithFields(priorJson, ("endTimestamp", SpondPayloads.Timestamp(Desired.End.AddMinutes(1))))
        };
        var detailReads = 0;
        var writes = 0;
        using var handler = new MockHandler(request =>
        {
            if (request.Method != HttpMethod.Get)
            {
                writes++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/profile", StringComparison.Ordinal))
                return Task.FromResult(Json(new { id = "test-owner" }));
            detailReads++;
            return Task.FromResult(Json(detailReads == changedRead ? unsafeJson : priorJson));
        });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<SyncException>(() => new SpondClient(http).ExecutePlanAsync(actions,
            Group, false, Now, _ => { }));
        Assert.Equal(changedRead, detailReads);
        Assert.Equal(0, writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_MissingPriorStateStopsBeforeWrites(bool replacement)
    {
        var actions = replacement
            ? new SyncAction[] { new(Date, SyncActionKind.Delete, "Test guard", "test-event", null),
                new(Date, SyncActionKind.Create, "Test guard", null, Desired) }
            : [new(Date, SyncActionKind.Update, "Test guard", "test-event", Desired)];
        var writes = 0;
        using var handler = new MockHandler(request =>
        {
            if (request.Method != HttpMethod.Get) writes++;
            return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/profile", StringComparison.Ordinal)
                ? Json(new { id = "test-owner" }) : Json(EventJson()));
        });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<SyncException>(() => new SpondClient(http).ExecutePlanAsync(actions,
            Group, false, Now, _ => { }));
        Assert.Equal(0, writes);
    }

    [Fact]
    public async Task Execute_VerifiedMetadataUpdateOmitsRecipients()
    {
        var state = EventJson(heading: "Test old heading");
        var prior = SpondClient.ReadState(state)!;
        var actions = SyncPlanner.Plan([new(Date, "taken", new(1, "Test guard", "+4790000001"))],
            [new("test-event", prior.Description, prior)], Group, new HashSet<DateOnly> { Date }, SyncCalendar.Today(Now));
        var writes = 0;
        using var handler = new MockHandler(async request =>
        {
            if (request.Method == HttpMethod.Get) return Json(state);
            Assert.Equal(HttpMethod.Post, request.Method);
            var payload = await request.Content!.ReadFromJsonAsync<JsonElement>();
            Assert.False(payload.TryGetProperty("recipients", out _));
            Assert.Equal(Desired.Heading, payload.GetProperty("heading").GetString());
            Assert.Equal(Desired.Description, payload.GetProperty("description").GetString());
            writes++;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var http = new HttpClient(handler);
        await new SpondClient(http).ExecutePlanAsync(actions, Group, false, Now, _ => { });
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task Execute_ExplicitOpenCancellationDoesNotRequireGuardianReadback()
    {
        var state = WithFields(EventJson(), ("recipients", new { group = new { id = Group.Id } }));
        var actions = SyncPlanner.Plan([new(Date, "open", null)],
            [new("test-event", Desired.Description, null)], Group, new HashSet<DateOnly> { Date }, SyncCalendar.Today(Now));
        var writes = 0;
        using var handler = new MockHandler(request =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Json(state));
            Assert.Equal(HttpMethod.Delete, request.Method);
            Assert.Equal("?quiet=true", request.RequestUri!.Query);
            writes++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        using var http = new HttpClient(handler);
        await new SpondClient(http).ExecutePlanAsync(actions, Group, false, Now, _ => { });
        Assert.Equal(1, writes);
    }

    private static JsonElement WithFields(JsonElement source, params (string Name, object? Value)[] replacements)
    {
        var fields = source.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        foreach (var replacement in replacements) fields[replacement.Name] = JsonSerializer.SerializeToElement(replacement.Value);
        return JsonSerializer.SerializeToElement(fields);
    }

    [Fact]
    public async Task ListRead_PaginatesByStartWithMultidayEndsAndDeduplicates()
    {
        var page = Enumerable.Range(0, 100).Select(index => WithFields(EventJson("test-page-" + index),
            ("description", index == 99 ? Desired.Description : "Test unowned"),
            ("startTimestamp", SpondPayloads.Timestamp(Desired.Start.AddMinutes(index))),
            ("endTimestamp", SpondPayloads.Timestamp(index == 0 ? Desired.End.AddDays(2) : Desired.End.AddMinutes(index))))).ToArray();
        var requestCount = 0;
        using var handler = new MockHandler(request =>
        {
            requestCount++;
            if (requestCount == 1) return Task.FromResult(Json(page));
            Assert.Equal(2, requestCount);
            Assert.Contains("minStartTimestamp=" + Uri.EscapeDataString(SpondPayloads.Timestamp(Desired.Start.AddMinutes(99))),
                request.RequestUri!.Query);
            Assert.Contains("maxStartTimestamp=" + Uri.EscapeDataString("2026-11-26T23:00:00Z"), request.RequestUri.Query);
            return Task.FromResult(Json(new[] { page[^1], WithFields(EventJson(),
                ("startTimestamp", SpondPayloads.Timestamp(Desired.Start.AddMinutes(100)))) }));
        });
        using var http = new HttpClient(handler);
        var events = await new SpondClient(http).GetEventsAsync(Group.Id, new HashSet<DateOnly> { Date }, now: Now);
        Assert.Equal(new[] { "test-page-99", "test-event" }, events.Select(item => item.Id));
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task ListRead_StalledFullBoundaryFailsClosed()
    {
        var page = Enumerable.Range(0, 100).Select(index => WithFields(EventJson("test-page-" + index),
            ("description", "Test unowned"))).ToArray();
        var requests = 0;
        using var handler = new MockHandler(_ => { requests++; return Task.FromResult(Json(page)); });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<SyncException>(() => new SpondClient(http).GetEventsAsync(Group.Id,
            new HashSet<DateOnly> { Date }, now: Now));
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData("2026-09-01", "2026-09-03", "2026-08-31T22:00:00Z", "2026-09-03T22:00:00Z")]
    [InlineData("2026-11-24", "2026-11-26", "2026-11-23T23:00:00Z", "2026-11-26T23:00:00Z")]
    [InlineData("2026-10-24", "2026-10-25", "2026-10-23T22:00:00Z", "2026-10-25T23:00:00Z")]
    public async Task ListRead_BoundsStartWindowAtOsloMidnights(string first, string last, string minimum, string maximum)
    {
        using var handler = new MockHandler(request =>
        {
            var query = request.RequestUri!.Query;
            Assert.Contains("minStartTimestamp=" + Uri.EscapeDataString(minimum), query);
            Assert.Contains("maxStartTimestamp=" + Uri.EscapeDataString(maximum), query);
            Assert.Contains("groupId=test%2Fgroup", query);
            Assert.Contains("scheduled=true", query);
            Assert.Contains("max=100", query);
            return Task.FromResult(Json(System.Array.Empty<object>()));
        });
        using var http = new HttpClient(handler);
        Assert.Empty(await new SpondClient(http).GetEventsAsync("test/group",
            new HashSet<DateOnly> { DateOnly.ParseExact(last, "yyyy-MM-dd"), DateOnly.ParseExact(first, "yyyy-MM-dd") }, now: Now));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(100)]
    public async Task ListRead_NonascendingStartsFailsClosed(int count)
    {
        var page = Enumerable.Range(0, count).Select(index => WithFields(EventJson("test-page-" + index),
            ("description", "Test unowned"),
            ("startTimestamp", SpondPayloads.Timestamp(Desired.Start.AddMinutes(-index))))).ToArray();
        var requests = 0;
        using var handler = new MockHandler(_ => { requests++; return Task.FromResult(Json(page)); });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<SyncException>(() => new SpondClient(http).GetEventsAsync(Group.Id,
            new HashSet<DateOnly> { Date }, now: Now));
        Assert.Equal("Arrangementlisten kan ikke pagineres sikkert; ingen endringer sendt.", error.Message);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task ListRead_FullPageAtInitialCursorFailsClosed()
    {
        var page = Enumerable.Range(0, 100).Select(index => WithFields(EventJson("test-page-" + index),
            ("description", "Test unowned"),
            ("startTimestamp", SpondPayloads.Timestamp(SyncCalendar.At(Date, TimeOnly.MinValue))))).ToArray();
        var requests = 0;
        using var handler = new MockHandler(_ => { requests++; return Task.FromResult(Json(page)); });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<SyncException>(() => new SpondClient(http).GetEventsAsync(Group.Id,
            new HashSet<DateOnly> { Date }, now: Now));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task MetadataTransport_PreservesScheduledFieldsAndSecondRunIsNoOp()
    {
        var state = EventJson(heading: "Test old heading");
        var writes = 0;
        using var handler = new MockHandler(async request =>
        {
            if (request.Method == HttpMethod.Get) return Json(state);
            Assert.Equal(HttpMethod.Post, request.Method);
            var payload = await request.Content!.ReadFromJsonAsync<JsonElement>();
            Assert.False(payload.TryGetProperty("recipients", out _));
            Assert.Equal("2026-11-19T15:45:00Z", payload.GetProperty("inviteTime").GetString());
            Assert.Equal("test-location", payload.GetProperty("location").GetProperty("id").GetString());
            state = WithFields(state, ("heading", payload.GetProperty("heading").GetString()));
            writes++;
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var http = new HttpClient(handler);
        var client = new SpondClient(http);
        Assert.True(await client.UpdateEventMetadataAsync("test-event", Date, Group.Id, Desired.Heading, Desired.Description, Now));
        Assert.False(await client.UpdateEventMetadataAsync("test-event", Date, Group.Id, Desired.Heading, Desired.Description, Now));
        Assert.Equal(1, writes);
    }

    [Theory]
    [InlineData("unowned")]
    [InlineData("wrong-group")]
    [InlineData("past")]
    public async Task DeleteTransport_RefusesUnsafeEventsWithoutWrites(string condition)
    {
        var state = condition switch
        {
            "unowned" => WithFields(EventJson(), ("description", "Test unowned")),
            "wrong-group" => WithFields(EventJson(), ("recipients", new { group = new { id = "test-other-group" } })),
            _ => WithFields(EventJson(), ("startTimestamp", SpondPayloads.Timestamp(Now.AddDays(-1))))
        };
        using var handler = new MockHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(Json(state));
        });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<SyncException>(() => new SpondClient(http).DeleteOwnedEventAsync("test-event", Date, Group.Id, Now));
    }

    [Fact]
    public async Task Execute_MissingCreationProfileFailsBeforeDeleting()
    {
        var actions = new SyncAction[] { new(Date, SyncActionKind.Delete, "Test guard", "test-event", null),
            new(Date, SyncActionKind.Create, "Test guard", null, Desired) };
        using var handler = new MockHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/profile", StringComparison.Ordinal)
                ? Json(new { id = "test-owner" }) : Json(EventJson()));
        });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<SyncException>(() => new SpondClient(http).ExecutePlanAsync(actions, Group with { Members = [] },
            false, Now, _ => { }));
    }

    [Fact]
    public void ReadState_UnknownGuardianReadbackCannotClaimIdempotence()
    {
        var state = WithFields(EventJson(), ("recipients", new
        {
            group = new { id = Group.Id, subGroups = new[] { new { id = Group.SubgroupId } } },
            profiles = System.Array.Empty<object>(), guardians = new[] { new { id = "test-guardian-without-profile" } }
        }));
        Assert.Null(SpondClient.ReadState(state));
    }

    [Theory]
    [InlineData("[{}]")]
    [InlineData("[\"test-child\"]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("\"unknown\"")]
    [InlineData("42")]
    [InlineData("false")]
    public void ReadState_NonemptyOrMalformedGroupMembersFailsClosed(string groupMembers)
    {
        var state = EventJson();
        var recipients = WithFields(state.GetProperty("recipients"),
            ("groupMembers", JsonSerializer.Deserialize<JsonElement>(groupMembers)));
        Assert.Null(SpondClient.ReadState(WithFields(state, ("recipients", recipients))));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadState_EmptyOrAbsentGroupMembersAllowsVerifiedGuardians(bool present)
    {
        var state = EventJson();
        if (present)
            state = WithFields(state, ("recipients", WithFields(state.GetProperty("recipients"),
                ("groupMembers", System.Array.Empty<object>()))));
        Assert.NotNull(SpondClient.ReadState(state));
    }
}