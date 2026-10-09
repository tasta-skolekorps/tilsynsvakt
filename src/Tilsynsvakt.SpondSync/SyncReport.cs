using System.Globalization;

namespace Tilsynsvakt.SpondSync;

public enum DateOutcomeKind { Create, Update, Delete, Replace, Unchanged, NoShift, Skipped }

public sealed record DateOutcome(DateOnly Date, DateOutcomeKind Kind, string? GuardName,
    SyncActionKind? Warning = null, IReadOnlyList<string>? ChangedFields = null)
{
    // Number of Spond write actions that must be reported before this date's line is final.
    public int Writes { get; init; }
}

public static class SyncReport
{
    public static IReadOnlyList<DateOutcome> Outcomes(IReadOnlyList<SyncAction> actions,
        IReadOnlyList<RosterShift> roster, IReadOnlySet<DateOnly> dates, DateOnly today)
    {
        var outcomes = new List<DateOutcome>();
        foreach (var date in dates.Where(date => date >= today).Order())
        {
            var day = actions.Where(action => action.Date == date).ToArray();
            var shift = roster.FirstOrDefault(item => item.Date == date);
            var name = day.Select(action => action.GuardName).FirstOrDefault(item => item is not null)
                ?? (shift?.Status == "taken" ? shift.Guard?.Name : null);
            var warning = day.FirstOrDefault(action => action.Kind is SyncActionKind.SkipPhoneNotFound
                or SyncActionKind.SkipNoProfiles or SyncActionKind.WarnMissingProfiles)?.Kind;
            var create = day.Any(action => action.Kind == SyncActionKind.Create);
            var delete = day.Any(action => action.Kind == SyncActionKind.Delete);
            var update = day.FirstOrDefault(action => action.Kind == SyncActionKind.Update);
            var kind = warning is SyncActionKind.SkipPhoneNotFound or SyncActionKind.SkipNoProfiles
                ? DateOutcomeKind.Skipped
                : create && delete ? DateOutcomeKind.Replace
                : create ? DateOutcomeKind.Create
                : delete ? DateOutcomeKind.Delete
                : update is not null ? DateOutcomeKind.Update
                : name is not null ? DateOutcomeKind.Unchanged
                : DateOutcomeKind.NoShift;
            outcomes.Add(new(date, kind, name, warning,
                update is null ? null : ChangedFields(update.PriorState, update.Desired))
            {
                Writes = day.Count(action => action.Kind is SyncActionKind.Create
                    or SyncActionKind.Update or SyncActionKind.Delete)
            });
        }
        return outcomes;
    }

    public static IReadOnlyList<string> ChangedFields(EventSpec? prior, EventSpec? desired)
    {
        if (prior is null || desired is null) return [];
        var fields = new List<string>();
        if (prior.Heading != desired.Heading) fields.Add("tittel");
        if (prior.Description != desired.Description) fields.Add("beskrivelse");
        if (prior.Location != desired.Location) fields.Add("sted");
        if (prior.Meet != desired.Meet) fields.Add("oppmøte");
        if (prior.Start != desired.Start || prior.End != desired.End) fields.Add("tidspunkt");
        if (prior.InviteAt != desired.InviteAt) fields.Add("invitasjon");
        if (prior.GroupId != desired.GroupId || prior.SubgroupId != desired.SubgroupId) fields.Add("gruppe");
        if (prior.AutoAccept != desired.AutoAccept) fields.Add("autoaksept");
        if (!prior.GuardianIds.Order(StringComparer.Ordinal)
            .SequenceEqual(desired.GuardianIds.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            fields.Add($"foresatte ({prior.GuardianIds.Count}→{desired.GuardianIds.Count})");
        return fields;
    }

    public static IEnumerable<string> Lines(DateOutcome outcome, bool dryRun)
    {
        var date = outcome.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var name = PublicName(outcome.GuardName);
        if (outcome.Warning == SyncActionKind.WarnMissingProfiles)
            yield return $"::warning::{date} én eller flere foresatte mangler Spond-profil og ble ikke invitert{name}";
        var fields = outcome.ChangedFields is { Count: > 0 } changed ? $" ({string.Join(", ", changed)})" : "";
        var label = outcome.Kind switch
        {
            DateOutcomeKind.Create => dryRun ? "vil opprette" : "opprettet",
            DateOutcomeKind.Update => (dryRun ? "vil oppdatere" : "oppdatert") + fields,
            DateOutcomeKind.Delete => dryRun ? "vil slette" : "slettet",
            DateOutcomeKind.Replace => dryRun ? "vil erstatte (ny vakt)" : "erstattet",
            DateOutcomeKind.Unchanged => "uendret",
            DateOutcomeKind.NoShift => "ingen vakt",
            _ => outcome.Warning == SyncActionKind.SkipPhoneNotFound
                ? "telefonnummeret finnes ikke hos noen foresatt i undergruppen; vakt hoppet over"
                : "ingen av de foresatte har Spond-profil; vakt hoppet over"
        };
        yield return $"{(outcome.Kind == DateOutcomeKind.Skipped ? "::warning::" : "")}{date} {label}{name}";
    }

    public static string Header(bool dryRun, IReadOnlySet<DateOnly> dates)
    {
        var mode = dryRun ? "Tørrkjøring – ingen endringer sendes til Spond" : "Skriver til Spond";
        return dates.Count == 0
            ? $"{mode} (ingen datoer i synkroniseringsvinduet)"
            : $"{mode} ({dates.Min():yyyy-MM-dd} – {dates.Max():yyyy-MM-dd})";
    }

    public static string Summary(IReadOnlyList<DateOutcome> outcomes, bool dryRun)
    {
        int Count(params DateOutcomeKind[] kinds) => outcomes.Count(outcome => kinds.Contains(outcome.Kind));
        var created = Count(DateOutcomeKind.Create);
        var updated = Count(DateOutcomeKind.Update, DateOutcomeKind.Replace);
        var deleted = Count(DateOutcomeKind.Delete);
        var rest = $"{Count(DateOutcomeKind.Unchanged)} uendret, {Count(DateOutcomeKind.Skipped)} hoppet over";
        return dryRun
            ? $"Oppsummering: vil opprette {created}, vil oppdatere {updated}, vil slette {deleted}, {rest}"
            : $"Oppsummering: {created} opprettet, {updated} oppdatert, {deleted} slettet, {rest}";
    }

    public static string PublicName(string? name) => name is null ? "" : " – " +
        name.Replace("%", "%25").Replace("\r", "%0D").Replace("\n", "%0A");
}

// Streams one line per date once all of that date's Spond writes have been reported.
public sealed class SyncLog(IReadOnlyList<DateOutcome> outcomes, bool dryRun, Action<string> write)
{
    private readonly Dictionary<DateOnly, int> pending = outcomes.ToDictionary(outcome => outcome.Date, outcome => outcome.Writes);
    private int next;

    public void Start() => Flush();

    public void Reported(SyncAction action)
    {
        if (action.Kind is SyncActionKind.Create or SyncActionKind.Update or SyncActionKind.Delete &&
            pending.ContainsKey(action.Date))
            pending[action.Date]--;
        Flush();
    }

    public void Finish()
    {
        Flush();
        write(SyncReport.Summary(outcomes, dryRun));
    }

    private void Flush()
    {
        while (next < outcomes.Count && pending[outcomes[next].Date] <= 0)
            foreach (var line in SyncReport.Lines(outcomes[next++], dryRun)) write(line);
    }
}
