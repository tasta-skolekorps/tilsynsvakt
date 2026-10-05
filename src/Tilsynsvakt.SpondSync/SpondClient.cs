using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Globalization;
using System.Text.Json;

namespace Tilsynsvakt.SpondSync;

public sealed class SpondClient(HttpClient http)
{
    public static readonly Uri BaseUrl = new("https://api.spond.com/core/v1/");
    private const int EventLimit = 100;

    public async Task LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(new Uri(BaseUrl, "auth2/login"),
            new { email = username, password }, cancellationToken);
        using var document = await ReadAsync(response, cancellationToken);
        if (!document.RootElement.TryGetProperty("accessToken", out var access) ||
            access.ValueKind != JsonValueKind.Object || !access.TryGetProperty("token", out var token) ||
            token.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(token.GetString()))
            throw new SyncException("Spond-pålogging mislyktes; kontroller konfigurasjonen.");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.GetString());
    }

    public async Task<TargetGroup> GetTargetGroupAsync(string groupName, string subgroupName,
        CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(new Uri(BaseUrl, "groups"), cancellationToken);
        using var document = await ReadAsync(response, cancellationToken);
        var groups = Array(document.RootElement).Where(group => Text(group, "name") == groupName).ToArray();
        if (groups.Length != 1) throw new SyncException("Gruppenavnet må matche nøyaktig én Spond-gruppe.");
        var group = groups[0];
        var subgroups = Array(Property(group, "subGroups")).Where(item => Text(item, "name") == subgroupName).ToArray();
        if (subgroups.Length != 1) throw new SyncException("Undergruppenavnet må matche nøyaktig én Spond-undergruppe.");
        var members = Array(Property(group, "members")).Select(member => new ChildMember(
            Text(member, "id"), Array(Property(member, "subGroups")).Select(item => item.GetString()
                ?? throw new SyncException("Ufullstendig Spond-undergruppe.")).ToArray(),
            member.TryGetProperty("guardians", out var guardians) && guardians.ValueKind != JsonValueKind.Null
                ? Array(guardians).Select(guardian => new Guardian(Text(guardian, "id"),
                    OptionalText(guardian, "phoneNumber"),
                    guardian.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object
                        ? OptionalText(profile, "id") : null,
                    OptionalText(guardian, "email"))).ToArray()
                : System.Array.Empty<Guardian>())).ToArray();
        if (members.GroupBy(member => member.Id).Any(bucket => bucket.Count() != 1))
            throw new SyncException("Tvetydig medlemsliste fra Spond.");
        return new TargetGroup(Text(group, "id"), Text(subgroups[0], "id"), members);
    }

    public async Task<IReadOnlyList<ExistingEvent>> GetEventsAsync(string groupId,
        IReadOnlySet<DateOnly> dates, CancellationToken cancellationToken = default, DateTimeOffset? now = null)
    {
        var events = new Dictionary<string, ExistingEvent>(StringComparer.Ordinal);
        if (dates.Count == 0) return [];
        var cursor = SyncCalendar.At(dates.Min(), TimeOnly.MinValue);
        var end = SyncCalendar.At(dates.Max().AddDays(1), TimeOnly.MinValue);
        for (var pageNumber = 0; pageNumber < 1000; pageNumber++)
        {
            var query = "sponds?includeComments=true&includeHidden=false&addProfileInfo=true&scheduled=true" +
                $"&order=asc&max={EventLimit}&groupId={Uri.EscapeDataString(groupId)}" +
                $"&minStartTimestamp={Uri.EscapeDataString(SpondPayloads.Timestamp(cursor))}" +
                $"&maxStartTimestamp={Uri.EscapeDataString(SpondPayloads.Timestamp(end))}";
            using var response = await http.GetAsync(new Uri(BaseUrl, query), cancellationToken);
            using var document = await ReadAsync(response, cancellationToken);
            var page = Array(document.RootElement);
            var starts = page.Select(item => Instant(item, "startTimestamp")).ToArray();
            if (!starts.SequenceEqual(starts.Order()) || (starts.Length > 0 && starts[0] < cursor) ||
                (page.Length >= EventLimit && starts[^1] <= cursor))
                throw new SyncException("Arrangementlisten kan ikke pagineres sikkert; ingen endringer sendt.");
            foreach (var item in page)
            {
                var description = OptionalText(item, "description");
                var date = SyncPlanner.OwnedDate(description);
                if (date is null || !dates.Contains(date.Value)) continue;
                if (Text(Property(Property(item, "recipients"), "group"), "id") != groupId) continue;
                var start = Instant(item, "startTimestamp");
                if (start <= (now ?? DateTimeOffset.UtcNow)) continue;
                if (SyncCalendar.Today(start) != date)
                    throw new SyncException("Eid arrangementsdato stemmer ikke med starttid; ingen endringer sendt.");
                var id = Text(item, "id");
                events[id] = new ExistingEvent(id, description, ReadState(item));
            }
            if (page.Length < EventLimit) return events.Values.ToArray();
            cursor = starts[^1];
        }
        throw new SyncException("Arrangementlisten kan være avkortet; ingen endringer sendt.");
    }

    internal static DateTimeOffset Instant(JsonElement item, string field) =>
        DateTimeOffset.TryParse(Text(item, field), CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
            ? instant : throw new SyncException("Ugyldig arrangementstid; ingen endringer sendt.");

    internal static EventSpec? ReadState(JsonElement item)
    {
        var recipients = Property(item, "recipients");
        var group = Property(recipients, "group");
        var guardians = Array(Property(recipients, "guardians"));
        if (guardians.Length == 0 || guardians.Any(guardian => guardian.ValueKind != JsonValueKind.Object ||
            string.IsNullOrWhiteSpace(OptionalText(guardian, "profileId"))) ||
            Array(Property(recipients, "profiles")).Length != 0 ||
            (recipients.TryGetProperty("groupMembers", out var groupMembers) &&
                (groupMembers.ValueKind != JsonValueKind.Array || groupMembers.GetArrayLength() != 0)) ||
            (recipients.TryGetProperty("members", out var members) && Array(members).Length != 0)) return null;
        var subgroups = Array(Property(group, "subGroups"));
        if (subgroups.Length != 1 || subgroups[0].ValueKind != JsonValueKind.Object) return null;
        var start = Instant(item, "startTimestamp");
        var meet = item.TryGetProperty("meetupTimestamp", out var meetup) && meetup.ValueKind == JsonValueKind.String
            ? Instant(item, "meetupTimestamp") : start.AddMinutes(-Property(item, "meetupPrior").GetDouble());
        if (Text(item, "type") != "EVENT" || Text(item, "visibility") != "INVITEES" ||
            Text(item, "autoReminderType") != "REMIND_48H_BEFORE" ||
            !Property(item, "commentsDisabled").GetBoolean() || !Property(item, "participantsHidden").GetBoolean())
            throw new SyncException("Uventede innstillinger i eid arrangement; ingen endringer sendt.");
        return new EventSpec(Text(item, "heading"), Text(item, "description"), Text(Property(item, "location"), "feature"),
            meet, start, Instant(item, "endTimestamp"), Instant(item, "inviteTime"), Text(group, "id"), Text(subgroups[0], "id"),
            guardians.Select(guardian => Text(guardian, "profileId")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            Property(item, "autoAccept").GetBoolean());
    }

    public async Task<string> GetOwnerProfileIdAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(new Uri(BaseUrl, "profile"), cancellationToken);
        using var document = await ReadAsync(response, cancellationToken);
        return Text(document.RootElement, "id");
    }

    public async Task CreateEventAsync(JsonElement payload, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync(new Uri(BaseUrl, "sponds"), payload, cancellationToken);
        EnsureSuccess(response);
    }

    public async Task ExecutePlanAsync(IReadOnlyList<SyncAction> actions, TargetGroup group, bool dryRun,
        DateTimeOffset now, Action<SyncAction> report, CancellationToken cancellationToken = default)
    {
        if (dryRun)
        {
            foreach (var action in actions) report(action);
            return;
        }
        var owner = actions.Any(action => action.Kind == SyncActionKind.Create)
            ? await GetOwnerProfileIdAsync(cancellationToken) : null;
        var creates = new Dictionary<DateOnly, JsonElement>();
        foreach (var action in actions)
        {
            if (action.Kind == SyncActionKind.Create)
            {
                if (action.Desired is null || action.Desired.Start <= now)
                    throw new SyncException("Arrangementet har startet; ingen endringer sendt.");
                creates.Add(action.Date, SpondPayloads.Create(action.Desired, group, owner!));
            }
            else if (action.Kind is SyncActionKind.Delete or SyncActionKind.Update)
            {
                var current = await GetEventAsync(action.EventId!, cancellationToken);
                ValidateOwned(current, action.Date, group.Id, now);
                if (action.Kind == SyncActionKind.Update ||
                    actions.Any(candidate => candidate.Kind == SyncActionKind.Create && candidate.Date == action.Date))
                {
                    if (action.PriorState is null)
                        throw new SyncException("Mangler verifisert tidligere arrangement; planlegg synkroniseringen på nytt.");
                    ValidatePriorState(current, action.PriorState);
                }
                if (action.Kind == SyncActionKind.Update)
                    _ = SpondPayloads.Metadata(current, action.Desired!.Heading, action.Desired.Description);
            }
        }
        foreach (var action in actions)
        {
            switch (action.Kind)
            {
                case SyncActionKind.Create:
                    await CreateEventAsync(creates[action.Date], cancellationToken);
                    break;
                case SyncActionKind.Delete:
                    await DeleteOwnedEventAsync(action.EventId!, action.Date, group.Id, now, cancellationToken,
                        action.PriorState);
                    break;
                case SyncActionKind.Update:
                    await UpdateEventMetadataAsync(action.EventId!, action.Date, group.Id,
                        action.Desired!.Heading, action.Desired.Description, now, cancellationToken, action.PriorState);
                    break;
            }
            report(action);
        }
    }

    public async Task DeleteOwnedEventAsync(string eventId, DateOnly date, string groupId, DateTimeOffset now,
        CancellationToken cancellationToken = default, EventSpec? expectedState = null)
    {
        var current = await GetEventAsync(eventId, cancellationToken);
        ValidateOwned(current, date, groupId, now);
        if (expectedState is not null) ValidatePriorState(current, expectedState);
        using var response = await http.DeleteAsync(SpondPayloads.QuietDeleteUrl(eventId), cancellationToken);
        EnsureSuccess(response);
    }

    private static void ValidatePriorState(JsonElement current, EventSpec? expectedState)
    {
        var state = ReadState(current);
        if (state is null || (expectedState is not null && !SyncPlanner.Equivalent(state, expectedState)))
            throw new SyncException("Arrangementet er endret eller mottakerne er ukjente; planlegg synkroniseringen på nytt.");
    }

    private static void ValidateOwned(JsonElement current, DateOnly date, string groupId, DateTimeOffset now)
    {
        if (SyncPlanner.OwnedDate(OptionalText(current, "description")) != date ||
            Text(Property(Property(current, "recipients"), "group"), "id") != groupId ||
            Text(current, "type") != "EVENT" || Instant(current, "startTimestamp") <= now ||
            SyncCalendar.Today(Instant(current, "startTimestamp")) != date)
            throw new SyncException("Arrangementet kan ikke endres sikkert; ingen endringer sendt.");
    }

    public async Task<JsonElement> GetEventAsync(string eventId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(eventId)) throw new SyncException("Mangler arrangementsidentifikator.");
        using var response = await http.GetAsync(new Uri(BaseUrl, $"sponds/{Uri.EscapeDataString(eventId)}"),
            cancellationToken);
        using var document = await ReadAsync(response, cancellationToken);
        if (Text(document.RootElement, "id") != eventId)
            throw new SyncException("Uventet arrangement fra Spond; ingen endringer sendt.");
        return document.RootElement.Clone();
    }

    public async Task<bool> UpdateEventMetadataAsync(string eventId, DateOnly date, string groupId,
        string heading, string description, DateTimeOffset now, CancellationToken cancellationToken = default,
        EventSpec? expectedState = null)
    {
        var current = await GetEventAsync(eventId, cancellationToken);
        ValidateOwned(current, date, groupId, now);
        ValidatePriorState(current, expectedState);
        if (SyncPlanner.OwnedDate(description) != date)
            throw new SyncException("Arrangementet kan ikke endres sikkert; ingen endringer sendt.");
        if (Text(current, "heading") == heading && OptionalText(current, "description") == description)
            return false;

        var payload = SpondPayloads.Metadata(current, heading, description);
        using var response = await http.PostAsJsonAsync(new Uri(BaseUrl, $"sponds/{Uri.EscapeDataString(eventId)}"),
            payload, cancellationToken);
        EnsureSuccess(response);
        return true;
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new SyncException("Ekstern forespørsel mislyktes; ingen responsdetaljer logges.");
    }

    internal static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode) throw new SyncException("Ekstern forespørsel mislyktes; ingen responsdetaljer logges.");
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
    }

    internal static JsonElement Property(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value : throw new SyncException("Ufullstendig ekstern respons.");

    internal static JsonElement[] Array(JsonElement element) => element.ValueKind == JsonValueKind.Array
        ? element.EnumerateArray().ToArray() : throw new SyncException("Uventet format i ekstern respons.");

    internal static string Text(JsonElement element, string name) =>
        OptionalText(element, name) is { Length: > 0 } value ? value : throw new SyncException("Ufullstendig ekstern respons.");

    internal static string? OptionalText(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;
}