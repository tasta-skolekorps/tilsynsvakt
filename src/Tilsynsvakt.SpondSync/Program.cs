using System.Globalization;
using System.Text.Json;
using Tilsynsvakt.SpondSync;

try
{
    var options = SyncOptions.FromEnvironment(Environment.GetEnvironmentVariable);
    var today = SyncCalendar.Today(DateTimeOffset.UtcNow);
    var dates = SyncCalendar.SelectDates(today, options.Dates);
    Console.WriteLine(SyncReport.Header(options.DryRun, dates));
    if (dates.Count == 0) return 0;

    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
    using var backendHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    var from = dates.Min().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    var to = dates.Max().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    using var response = await backendHttp.GetAsync(new Uri(options.ApiBaseUrl,
        $"api/shifts?from={from}&to={to}"), timeout.Token);
    using var document = await SpondClient.ReadAsync(response, timeout.Token);
    var roster = SpondClient.Array(SpondClient.Property(document.RootElement, "shifts")).Select(item =>
    {
        if (!DateOnly.TryParseExact(SpondClient.Text(item, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date)) throw new SyncException("Ugyldig dato fra vakt-API-et.");
        var guardJson = SpondClient.Property(item, "guard");
        var guard = guardJson.ValueKind == JsonValueKind.Null ? null : new Guard(
            SpondClient.Property(guardJson, "id").GetInt32(), SpondClient.Text(guardJson, "name"),
            SpondClient.Text(guardJson, "phone"));
        return new RosterShift(date, SpondClient.Text(item, "status"), guard);
    }).ToArray();

    using var spondHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    var spond = new SpondClient(spondHttp);
    await spond.LoginAsync(options.Username, options.Password, timeout.Token);
    var group = await spond.GetTargetGroupAsync(options.GroupName, options.SubgroupName, timeout.Token);
    var existing = await spond.GetEventsAsync(group.Id, dates, timeout.Token);
    var actions = SyncPlanner.Plan(roster, existing, group, dates, today);
    var log = new SyncLog(SyncReport.Outcomes(actions, roster, dates, today), options.DryRun, Console.WriteLine);
    log.Start();
    await spond.ExecutePlanAsync(actions, group, options.DryRun, DateTimeOffset.UtcNow, log.Reported, timeout.Token);
    log.Finish();
    return 0;
}
catch (SyncException exception)
{
    Console.Error.WriteLine($"::error::{exception.Message}");
    return 1;
}
catch (Exception)
{
    Console.Error.WriteLine("::error::Synkronisering mislyktes; ingen personopplysninger eller responsdetaljer logges.");
    return 1;
}