using System.Globalization;

namespace Tilsynsvakt.SpondSync;

public sealed record Guard(int Id, string Name, string Phone);
public sealed record RosterShift(DateOnly Date, string Status, Guard? Guard);
public sealed record Guardian(string Id, string? PhoneNumber, string? ProfileId = null, string? Email = null);
public sealed record ChildMember(string Id, IReadOnlyList<string> SubGroups, IReadOnlyList<Guardian> Guardians);
public sealed record TargetGroup(string Id, string SubgroupId, IReadOnlyList<ChildMember> Members);
public sealed record EventSpec(
    string Heading, string Description, string Location, DateTimeOffset Meet,
    DateTimeOffset Start, DateTimeOffset End, DateTimeOffset InviteAt,
    string GroupId, string SubgroupId, IReadOnlyList<string> GuardianIds, bool AutoAccept = false);
public sealed record ExistingEvent(string Id, string? Description, EventSpec? VerifiedState);
public sealed record EventListing(IReadOnlyList<ExistingEvent> Owned, IReadOnlySet<DateOnly> ManualDates);
public enum SyncActionKind { Create, Update, Delete, SkipPhoneNotFound, SkipNoProfiles, WarnMissingProfiles, SkipManualEvent }
public enum GuardianMatchOutcome { Matched, PhoneNotFound, NoProfiles }
public sealed record GuardianMatch(GuardianMatchOutcome Outcome, IReadOnlyList<string> ProfileIds, bool MissingProfiles);
public sealed record SyncAction(DateOnly Date, SyncActionKind Kind, string? GuardName,
    string? EventId, EventSpec? Desired)
{
    public EventSpec? PriorState { get; init; }
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
    public const string Description = "Se nettside https://tasta-skolekorps.github.io/tilsynsvakt/ for informasjon, evt. ta kontakt på 924 23 946 hvis spørsmål. God vakt!";

    public static string Marker(DateOnly date) => $"[tilsynsvakt-sync:{date:yyyy-MM-dd}]";

    public static DateOnly? OwnedDate(string? description)
    {
        if (description is null) return null;
        const string prefix = "[tilsynsvakt-sync:";
        var dates = new List<DateOnly>();
        foreach (var line in description.Replace("\r\n", "\n").Split('\n'))
        {
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
        var phone = value.Replace(" ", "").Replace("-", "");
        if (phone.StartsWith("0047", StringComparison.Ordinal)) phone = "+47" + phone[4..];
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
            member.Guardians.Any(guardian => NormalizePhone(guardian.PhoneNumber) == phone)).ToArray();
        if (children.Length == 0) return new(GuardianMatchOutcome.PhoneNotFound, [], false);
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

    public static EventSpec DesiredEvent(DateOnly date, TargetGroup group, IReadOnlyList<string> guardianIds) =>
        new(Heading, Description + "\n" + Marker(date), Location,
            SyncCalendar.At(date, new TimeOnly(16, 40)), SyncCalendar.At(date, new TimeOnly(16, 45)),
            SyncCalendar.At(date, new TimeOnly(22, 0)), SyncCalendar.At(date.AddDays(-7), new TimeOnly(16, 45)),
            group.Id, group.SubgroupId, guardianIds.Order(StringComparer.Ordinal).ToArray());

    public static IReadOnlyList<SyncAction> Plan(
        IReadOnlyList<RosterShift> roster, IReadOnlyList<ExistingEvent> existing,
        TargetGroup group, IReadOnlySet<DateOnly> dates, DateOnly today, IReadOnlySet<DateOnly>? manualDates = null)
    {
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
                    ? SyncActionKind.SkipPhoneNotFound : SyncActionKind.SkipNoProfiles, shift.Guard.Name, null, null));
                continue;
            }
            if (match.MissingProfiles)
                actions.Add(new(date, SyncActionKind.WarnMissingProfiles, shift.Guard.Name, null, null));
            var desired = DesiredEvent(date, group, match.ProfileIds);
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
            else if (!Equivalent(current.VerifiedState, desired))
            {
                if (!Equivalent(current.VerifiedState with { Heading = desired.Heading, Description = desired.Description }, desired))
                    throw new SyncException("Endring av arrangementsinnstillinger kan ikke utføres sikkert.");
                actions.Add(new(date, SyncActionKind.Update, shift.Guard.Name, current.Id, desired)
                    { PriorState = current.VerifiedState });
            }
        }
        return actions;
    }

    internal static bool Equivalent(EventSpec? current, EventSpec desired) => current is not null &&
        current with { GuardianIds = desired.GuardianIds } == desired &&
        current.GuardianIds.Order(StringComparer.Ordinal).SequenceEqual(desired.GuardianIds, StringComparer.Ordinal);
}