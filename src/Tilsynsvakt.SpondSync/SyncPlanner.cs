using System.Globalization;

namespace Tilsynsvakt.SpondSync;

public sealed record Guard(int Id, string Name, string Phone);
public sealed record RosterShift(DateOnly Date, string Status, Guard? Guard);
// ProfilePhoneNumber is only used for matching; PhoneNumber is what is sent back to Spond.
public sealed record Guardian(string Id, string? PhoneNumber, string? ProfileId = null, string? Email = null,
    string? ProfilePhoneNumber = null);
// PhoneNumber is the member's own phone (adult members); it is only used to diagnose skipped shifts.
public sealed record ChildMember(string Id, IReadOnlyList<string> SubGroups, IReadOnlyList<Guardian> Guardians,
    string? PhoneNumber = null);
public sealed record TargetGroup(string Id, string SubgroupId, IReadOnlyList<ChildMember> Members);
// InviteAt is null when Spond omits inviteTime on readback, which means invitations were already sent.
public sealed record EventSpec(
    string Heading, string Description, string Location, DateTimeOffset Meet,
    DateTimeOffset Start, DateTimeOffset End, DateTimeOffset? InviteAt,
    string GroupId, string SubgroupId, IReadOnlyList<string> GuardianIds, bool AutoAccept = false);
public sealed record ExistingEvent(string Id, string? Description, EventSpec? VerifiedState);
public sealed record EventListing(IReadOnlyList<ExistingEvent> Owned, IReadOnlySet<DateOnly> ManualDates);
public enum SyncActionKind { Create, Update, Delete, SkipPhoneNotFound, SkipNoProfiles, WarnMissingProfiles, SkipManualEvent }
public enum GuardianMatchOutcome { Matched, PhoneNotFound, NoProfiles }
public enum PhoneNotFoundReason { NotInGroup, ChildOutsideSubgroup, MemberNotGuardian }
public sealed record GuardianMatch(GuardianMatchOutcome Outcome, IReadOnlyList<string> ProfileIds, bool MissingProfiles,
    PhoneNotFoundReason? Reason = null);
public sealed record SyncAction(DateOnly Date, SyncActionKind Kind, string? GuardName,
    string? EventId, EventSpec? Desired)
{
    public EventSpec? PriorState { get; init; }
    public PhoneNotFoundReason? PhoneReason { get; init; }
    // Last two digits of the backend phone; never the full number.
    public string? PhoneHint { get; init; }
}

public static class SyncCalendar
{
    public static TimeZoneInfo Oslo { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Oslo");

    public static DateOnly Today(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Oslo).DateTime);

    public static DateOnly? PeriodEnd(DateOnly today)
    {
        if (today >= new DateOnly(today.Year, 1, 5) && today <= new DateOnly(today.Year, 5, 29))
            return new DateOnly(today.Year, 5, 29);
        if (today >= new DateOnly(today.Year, 9, 1) && today <= new DateOnly(today.Year, 11, 28))
            return new DateOnly(today.Year, 11, 28);
        return null;
    }

    public static IReadOnlySet<DateOnly> SelectDates(DateOnly today, IReadOnlySet<DateOnly>? limit)
    {
        var end = PeriodEnd(today);
        if (end is null) return new HashSet<DateOnly>();
        return Enumerable.Range(0, end.Value.DayNumber - today.DayNumber + 1)
            .Select(today.AddDays)
            .Where(date => limit is null || limit.Contains(date))
            .ToHashSet();
    }

    public static DateTimeOffset At(DateOnly date, TimeOnly time) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), Oslo));
}

public static class SyncPlanner
{
    public const string Heading = "Tilsynsvakt Tasta Skole";
    public const string Location = "Tasta skole, Randabergveien, Stavanger";
    public const string WebsiteUrl = "https://tilsynsvakt.tastaskolekorps.no";

    // The shared site password is injected at runtime (API_PASSWORD) because Spond events are members-only.
    public static string Description(string sitePassword)
    {
        if (string.IsNullOrWhiteSpace(sitePassword) || sitePassword.AsSpan().IndexOfAny('\r', '\n') >= 0)
            throw new SyncException("Nettsidepassordet kan ikke brukes i arrangementsbeskrivelsen (tomt eller inneholder linjeskift).");
        return $"Se nettside {WebsiteUrl} for informasjon (passord: {sitePassword}) (der kan du bytte vakten din selv og fylle ut sjekklisten i stedet for skjema i perm), evt. ta kontakt på 924 23 946 hvis spørsmål. God vakt!";
    }

    // A blank line keeps the machine marker visually apart from the human text in Spond.
    public const string MarkerSeparator = "\n\n";

    public static string Marker(DateOnly date) => $"[tilsynsvakt-sync:{date:yyyy-MM-dd}]";

    public static DateOnly? OwnedDate(string? description)
    {
        if (description is null) return null;
        const string prefix = "[tilsynsvakt-sync:";
        var dates = new List<DateOnly>();
        foreach (var raw in description.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == prefix.Length + 11 && line.StartsWith(prefix, StringComparison.Ordinal) &&
                line.EndsWith(']') && DateOnly.TryParseExact(line.AsSpan(prefix.Length, 10), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                dates.Add(date);
        }
        if (dates.Count > 1) throw new SyncException("Flere eierskapsmarkører i samme arrangement; synkronisering stoppet.");
        return dates.Count == 1 ? dates[0] : null;
    }

    public static string? NormalizePhone(string? value)
    {
        if (value is null) return null;
        var phone = string.Concat(value.Where(ch => !char.IsWhiteSpace(ch) && ch is not '-' and not '.' and not '(' and not ')'));
        if (phone.StartsWith("0047", StringComparison.Ordinal)) phone = "+47" + phone[4..];
        // Ten digits starting with 47 can only be a Norwegian number without the plus sign.
        if (phone.Length == 10 && phone.StartsWith("47", StringComparison.Ordinal)) phone = "+" + phone;
        if (phone.Length == 8) phone = "+47" + phone;
        return phone.Length == 11 && phone.StartsWith("+47", StringComparison.Ordinal) &&
            phone[3] is >= '2' and <= '9' && phone.AsSpan(3).IndexOfAnyExceptInRange('0', '9') < 0
            ? phone : null;
    }

    public static GuardianMatch MatchGuardians(Guard guard, TargetGroup group)
    {
        var phone = NormalizePhone(guard.Phone);
        if (phone is null) throw new SyncException("Ugyldig telefonformat i vaktlisten.");
        var children = group.Members.Where(member => member.SubGroups.Contains(group.SubgroupId, StringComparer.Ordinal) &&
            member.Guardians.Any(guardian => GuardianHasPhone(guardian, phone))).ToArray();
        if (children.Length == 0) return new(GuardianMatchOutcome.PhoneNotFound, [], false, NotFoundReason(phone, group));
        var guardians = children.SelectMany(child => child.Guardians).ToArray();
        var ids = guardians.Where(guardian => !string.IsNullOrWhiteSpace(guardian.ProfileId))
            .Select(guardian => guardian.ProfileId!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (guardians.Any(guardian => string.IsNullOrWhiteSpace(guardian.Id)) ||
            ids.Any(id => group.Members.Any(member => member.Id == id)))
            throw new SyncException("Foresattmottakere kan ikke avgrenses sikkert; synkronisering stoppet.");
        var missing = guardians.Any(guardian => string.IsNullOrWhiteSpace(guardian.ProfileId));
        return ids.Length == 0
            ? new(GuardianMatchOutcome.NoProfiles, [], true)
            : new(GuardianMatchOutcome.Matched, ids, missing);
    }

    private static bool GuardianHasPhone(Guardian guardian, string phone) =>
        NormalizePhone(guardian.PhoneNumber) == phone || NormalizePhone(guardian.ProfilePhoneNumber) == phone;

    // Diagnostic only: explains why a phone did not match, so the admin knows what to fix in Spond.
    private static PhoneNotFoundReason NotFoundReason(string phone, TargetGroup group) =>
        group.Members.Any(member => member.Guardians.Any(guardian => GuardianHasPhone(guardian, phone)))
            ? PhoneNotFoundReason.ChildOutsideSubgroup
            : group.Members.Any(member => NormalizePhone(member.PhoneNumber) == phone)
                ? PhoneNotFoundReason.MemberNotGuardian
                : PhoneNotFoundReason.NotInGroup;

    public static string? PhoneHint(string? phone) =>
        NormalizePhone(phone) is { } normalized ? normalized[^2..] : null;

    public static EventSpec DesiredEvent(DateOnly date, TargetGroup group, IReadOnlyList<string> guardianIds, string sitePassword) =>
        new(Heading, Description(sitePassword) + MarkerSeparator + Marker(date), Location,
            SyncCalendar.At(date, new TimeOnly(16, 40)), SyncCalendar.At(date, new TimeOnly(16, 45)),
            SyncCalendar.At(date, new TimeOnly(22, 0)), SyncCalendar.At(date.AddDays(-7), new TimeOnly(16, 45)),
            group.Id, group.SubgroupId, guardianIds.Order(StringComparer.Ordinal).ToArray());

    public static IReadOnlyList<SyncAction> Plan(
        IReadOnlyList<RosterShift> roster, IReadOnlyList<ExistingEvent> existing,
        TargetGroup group, IReadOnlySet<DateOnly> dates, DateOnly today, string sitePassword,
        IReadOnlySet<DateOnly>? manualDates = null)
    {
        _ = Description(sitePassword);
        var selected = dates.Where(date => date >= today).ToHashSet();
        var shifts = roster.Where(shift => selected.Contains(shift.Date)).ToArray();
        if (shifts.GroupBy(shift => shift.Date).Any(bucket => bucket.Count() != 1))
            throw new SyncException("Flere vakter på samme dato; synkronisering stoppet.");
        var owned = existing.Select(item => (Event: item, Date: OwnedDate(item.Description)))
            .Where(item => item.Date is not null && selected.Contains(item.Date.Value)).ToArray();
        if (owned.GroupBy(item => item.Date).Any(bucket => bucket.Count() != 1))
            throw new SyncException("Flere eide arrangementer på samme dato; synkronisering stoppet.");
        var actions = new List<SyncAction>();
        foreach (var date in selected.Order())
        {
            var shift = shifts.SingleOrDefault(item => item.Date == date);
            var current = owned.SingleOrDefault(item => item.Date == date).Event;
            if (manualDates?.Contains(date) == true)
            {
                actions.Add(new(date, SyncActionKind.SkipManualEvent,
                    shift?.Status == "taken" ? shift.Guard?.Name : null, null, null));
                continue;
            }
            if (shift is null && current is not null)
                throw new SyncException("Vakt-API-et mangler en eid arrangementsdato; sletting kan ikke planlegges sikkert.");
            if (shift is null || shift.Status == "open")
            {
                if (shift?.Guard is not null) throw new SyncException("Motstridende vaktstatus fra API-et.");
                if (current is not null) actions.Add(new(date, SyncActionKind.Delete, null, current.Id, null));
                continue;
            }
            if (shift.Status != "taken" || shift.Guard is null)
                throw new SyncException("Ukjent eller ufullstendig vaktstatus fra API-et.");
            var match = MatchGuardians(shift.Guard, group);
            if (match.Outcome != GuardianMatchOutcome.Matched)
            {
                actions.Add(new(date, match.Outcome == GuardianMatchOutcome.PhoneNotFound
                    ? SyncActionKind.SkipPhoneNotFound : SyncActionKind.SkipNoProfiles, shift.Guard.Name, null, null)
                {
                    PhoneReason = match.Reason,
                    PhoneHint = match.Outcome == GuardianMatchOutcome.PhoneNotFound ? PhoneHint(shift.Guard.Phone) : null
                });
                continue;
            }
            if (match.MissingProfiles)
                actions.Add(new(date, SyncActionKind.WarnMissingProfiles, shift.Guard.Name, null, null));
            var desired = DesiredEvent(date, group, match.ProfileIds, sitePassword);
            if (current is null)
                actions.Add(new(date, SyncActionKind.Create, shift.Guard.Name, null, desired));
            else if (current.VerifiedState is null)
                throw new SyncException("Eide arrangementsmottakere kan ikke leses sikkert; ingen endringer sendt.");
            else if (!current.VerifiedState.GuardianIds
                .Order(StringComparer.Ordinal).SequenceEqual(desired.GuardianIds, StringComparer.Ordinal))
            {
                actions.Add(new(date, SyncActionKind.Delete, shift.Guard.Name, current.Id, null)
                    { PriorState = current.VerifiedState });
                actions.Add(new(date, SyncActionKind.Create, shift.Guard.Name, null, desired));
            }
            else
            {
                // Every owned event is rewritten from the backend on each run, even when it looks unchanged.
                if (!Equivalent(Updatable(current.VerifiedState, desired), desired))
                    throw new SyncException("Endring av arrangementsinnstillinger kan ikke utføres sikkert.");
                actions.Add(new(date, SyncActionKind.Update, shift.Guard.Name, current.Id, desired)
                    { PriorState = current.VerifiedState });
            }
        }
        return actions;
    }

    // Fields an in-place update rewrites; meetup follows the start because Spond keeps meetupPrior.
    internal static EventSpec Updatable(EventSpec current, EventSpec desired) => current with
    {
        Heading = desired.Heading, Description = desired.Description, Location = desired.Location,
        Start = desired.Start, End = desired.End, Meet = current.Meet + (desired.Start - current.Start)
    };

    internal static bool Equivalent(EventSpec? current, EventSpec desired) => current is not null &&
        current with { GuardianIds = desired.GuardianIds, InviteAt = current.InviteAt ?? desired.InviteAt } == desired &&
        current.GuardianIds.Order(StringComparer.Ordinal).SequenceEqual(desired.GuardianIds, StringComparer.Ordinal);
}