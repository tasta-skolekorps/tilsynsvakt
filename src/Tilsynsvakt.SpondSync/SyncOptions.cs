using System.Globalization;

namespace Tilsynsvakt.SpondSync;

public sealed class SyncException(string message) : Exception(message);

public sealed record SyncOptions(
    string Username, string Password, Uri ApiBaseUrl, string GroupName,
    string SubgroupName, bool DryRun, IReadOnlySet<DateOnly>? Dates)
{
    public static SyncOptions FromEnvironment(Func<string, string?> read)
    {
        var username = Required(read, "SPOND_USERNAME");
        var password = Required(read, "SPOND_PASSWORD");
        var origin = Required(read, "API_BASE_URL");
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new SyncException("API_BASE_URL må være en HTTP(S)-opprinnelse uten sti eller påloggingsinformasjon.");

        var dryRunValue = read("DRY_RUN");
        if (string.IsNullOrEmpty(dryRunValue)) dryRunValue = "true";
        if (!string.Equals(dryRunValue, "true", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(dryRunValue, "false", StringComparison.OrdinalIgnoreCase))
            throw new SyncException("DRY_RUN må være true eller false.");
        var dryRun = string.Equals(dryRunValue, "true", StringComparison.OrdinalIgnoreCase);

        HashSet<DateOnly>? dates = null;
        var dateValue = read("SPOND_SYNC_DATES");
        if (!string.IsNullOrWhiteSpace(dateValue))
        {
            dates = [];
            foreach (var value in dateValue.Split(','))
            {
                if (!DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date))
                    throw new SyncException("SPOND_SYNC_DATES må inneholde kommaseparerte datoer i formatet yyyy-MM-dd.");
                dates.Add(date);
            }
        }
        return new(username, password, uri,
            read("SPOND_GROUP_NAME") ?? "Tasta Skolekorps - Medlemmer",
            read("SPOND_SUBGROUP_NAME") ?? "Tilsynsvakt", dryRun, dates);
    }

    private static string Required(Func<string, string?> read, string key) =>
        string.IsNullOrWhiteSpace(read(key))
            ? throw new SyncException($"Mangler obligatorisk konfigurasjon: {key}")
            : read(key)!;
}