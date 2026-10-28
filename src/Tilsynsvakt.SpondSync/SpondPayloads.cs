using System.Globalization;
using System.Text.Json;

namespace Tilsynsvakt.SpondSync;

public static class SpondPayloads
{
    public static string Timestamp(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static Uri QuietDeleteUrl(string eventId) => new(SpondClient.BaseUrl,
        $"sponds/{Uri.EscapeDataString(eventId)}?quiet=true");

    private static object[] Guardians(EventSpec desired, TargetGroup group)
    {
        if (desired.GroupId != group.Id || desired.SubgroupId != group.SubgroupId || desired.GuardianIds.Count == 0)
            throw new SyncException("Foresattmottakere kan ikke avgrenses sikkert.");
        var candidates = group.Members.Where(member => member.SubGroups.Contains(group.SubgroupId, StringComparer.Ordinal))
            .SelectMany(member => member.Guardians).Where(guardian => !string.IsNullOrWhiteSpace(guardian.ProfileId))
            .GroupBy(guardian => guardian.ProfileId!, StringComparer.Ordinal).ToDictionary(bucket => bucket.Key, bucket => bucket.First());
        return desired.GuardianIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(profileId =>
        {
            if (!candidates.TryGetValue(profileId, out var guardian))
                throw new SyncException("Foresattprofil mangler; ingen endringer sendt.");
            return (object)new { email = guardian.Email, phoneNumber = guardian.PhoneNumber, profileId };
        }).ToArray();
    }

    public static JsonElement Create(EventSpec desired, TargetGroup group, string ownerProfileId)
    {
        if (string.IsNullOrWhiteSpace(ownerProfileId)) throw new SyncException("Arrangementsansvarlig mangler.");
        return JsonSerializer.SerializeToElement(new
        {
            heading = desired.Heading, description = desired.Description, spondType = "event", type = "EVENT",
            startTimestamp = Timestamp(desired.Start), endTimestamp = Timestamp(desired.End), commentsDisabled = true,
            meetupPrior = "5", maxAccepted = 0, rsvpDate = (object?)null,
            location = new { feature = desired.Location }, owners = new[] { new { id = ownerProfileId } },
            visibility = "INVITEES", participantsHidden = true, autoReminderType = "REMIND_48H_BEFORE",
            matchInfo = (object?)null, autoAccept = false, attachments = System.Array.Empty<object>(),
            tasks = new { openTasks = System.Array.Empty<object>(), assignedTasks = System.Array.Empty<object>() },
            inviteTime = Timestamp(desired.InviteAt ?? throw new SyncException("Invitasjonstidspunkt mangler.")),
            recipients = new { groupMembers = System.Array.Empty<object>(), guardians = Guardians(desired, group),
                group = new { id = group.Id, subGroups = new[] { group.SubgroupId } } }
        });
    }

    public static JsonElement AddRecipients(EventSpec desired, TargetGroup group) => JsonSerializer.SerializeToElement(new
    {
        profiles = System.Array.Empty<object>(), group = new { id = group.Id, subGroups = new[] { new { id = group.SubgroupId } } },
        groupMembers = System.Array.Empty<object>(), guardians = Guardians(desired, group)
    });

    // Rewrites the backend-derived fields (heading, description, time, location) and preserves everything else.
    public static JsonElement Metadata(JsonElement current, EventSpec desired)
    {
        if (SpondClient.Text(current, "type") != "EVENT") throw new SyncException("Bare enkeltarrangementer kan endres.");
        _ = SpondClient.Text(current, "startTimestamp");
        _ = SpondClient.Text(current, "endTimestamp");
        var payload = new Dictionary<string, JsonElement>
        {
            ["id"] = JsonSerializer.SerializeToElement(SpondClient.Text(current, "id")),
            ["startTimestamp"] = JsonSerializer.SerializeToElement(Timestamp(desired.Start)),
            ["endTimestamp"] = JsonSerializer.SerializeToElement(Timestamp(desired.End)),
        };
        // Settings are preserved when Spond returns them; absent optional fields are omitted rather than invented.
        foreach (var field in new[] { "commentsDisabled", "inviteTime", "visibility", "participantsHidden",
            "autoReminderType", "autoAccept" })
            if (Present(current, field, out var value)) payload[field] = value.Clone();
        payload["attachments"] = Present(current, "attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array
            ? attachments.Clone() : JsonSerializer.SerializeToElement(System.Array.Empty<object>());
        payload["tasks"] = Present(current, "tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Object
            ? tasks.Clone()
            : JsonSerializer.SerializeToElement(new { openTasks = System.Array.Empty<object>(), assignedTasks = System.Array.Empty<object>() });
        payload["heading"] = JsonSerializer.SerializeToElement(desired.Heading);
        payload["description"] = JsonSerializer.SerializeToElement(desired.Description);
        payload["spondType"] = JsonSerializer.SerializeToElement("EVENT");
        payload["rsvpDate"] = JsonSerializer.SerializeToElement<object?>(null);
        payload["matchInfo"] = JsonSerializer.SerializeToElement<object?>(null);
        if (Present(current, "owners", out var owners))
            payload["owners"] = JsonSerializer.SerializeToElement(SpondClient.Array(owners)
                .Select(owner => SpondClient.OptionalText(owner, "id")).Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => new { id }).ToArray());
        // A Spond location id is only kept while it still describes the desired place.
        var feature = desired.Location;
        payload["location"] = Present(current, "location", out var location) &&
            SpondClient.Text(location, "feature") == feature &&
            SpondClient.OptionalText(location, "id") is { Length: > 0 } locationId
                ? JsonSerializer.SerializeToElement(new { id = locationId, feature })
                : JsonSerializer.SerializeToElement(new { feature });
        payload["payment"] = current.TryGetProperty("payment", out var payment) && payment.ValueKind == JsonValueKind.Object
            ? payment.Clone() : JsonSerializer.SerializeToElement(new { });
        return JsonSerializer.SerializeToElement(payload);
    }

    private static bool Present(JsonElement element, string name, out JsonElement value) =>
        element.TryGetProperty(name, out value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
}