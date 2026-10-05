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
            inviteTime = Timestamp(desired.InviteAt),
            recipients = new { groupMembers = System.Array.Empty<object>(), guardians = Guardians(desired, group),
                group = new { id = group.Id, subGroups = new[] { group.SubgroupId } } }
        });
    }

    public static JsonElement AddRecipients(EventSpec desired, TargetGroup group) => JsonSerializer.SerializeToElement(new
    {
        profiles = System.Array.Empty<object>(), group = new { id = group.Id, subGroups = new[] { new { id = group.SubgroupId } } },
        groupMembers = System.Array.Empty<object>(), guardians = Guardians(desired, group)
    });

    public static JsonElement Metadata(JsonElement current, string heading, string description)
    {
        if (SpondClient.Text(current, "type") != "EVENT") throw new SyncException("Bare enkeltarrangementer kan endres.");
        var fields = new[] { "id", "startTimestamp", "endTimestamp", "commentsDisabled", "inviteTime", "location",
            "owners", "visibility", "participantsHidden", "autoReminderType", "autoAccept", "attachments", "tasks" };
        var payload = fields.ToDictionary(field => field, field => SpondClient.Property(current, field).Clone());
        payload["heading"] = JsonSerializer.SerializeToElement(heading);
        payload["description"] = JsonSerializer.SerializeToElement(description);
        payload["spondType"] = JsonSerializer.SerializeToElement("EVENT");
        payload["rsvpDate"] = JsonSerializer.SerializeToElement<object?>(null);
        payload["matchInfo"] = JsonSerializer.SerializeToElement<object?>(null);
        payload["owners"] = JsonSerializer.SerializeToElement(SpondClient.Array(SpondClient.Property(current, "owners"))
            .Select(owner => new { id = SpondClient.Text(owner, "id") }).ToArray());
        var location = SpondClient.Property(current, "location");
        payload["location"] = JsonSerializer.SerializeToElement(new { id = SpondClient.Text(location, "id"), feature = SpondClient.Text(location, "feature") });
        payload["payment"] = current.TryGetProperty("payment", out var payment) && payment.ValueKind == JsonValueKind.Object
            ? payment.Clone() : JsonSerializer.SerializeToElement(new { });
        return JsonSerializer.SerializeToElement(payload);
    }
}