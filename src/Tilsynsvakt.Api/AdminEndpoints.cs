using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace Tilsynsvakt.Api;

public static class AdminEndpoints
{
    private static readonly TimeOnly ScheduledStart = new(16, 45);
    private static readonly TimeOnly ScheduledEnd = new(22, 0);

    public static void MapAdminEndpoints(this WebApplication app, string? apiKey, string? username, string? password)
    {
        var admin = app.MapGroup("/api/admin")
            .ExcludeFromDescription()
            .RequireRateLimiting("admin")
            .AddEndpointFilter((context, next) =>
            {
                if (!IsAuthorized(context.HttpContext.Request, apiKey, username, password))
                {
                    return ValueTask.FromResult<object?>(Errors.Unauthorized().ToResult());
                }

                return next(context);
            });

        var guards = admin.MapGroup("/guards");
        guards.MapGet("", async (IStores stores, CancellationToken ct) =>
                Results.Ok(await stores.GetAllGuardsAsync(ct)))
            .WithName("GetAdminGuards");

        guards.MapPost("", CreateAsync)
            .WithName("CreateAdminGuard");

        guards.MapPut("/{id}", UpdateAsync)
            .WithName("UpdateAdminGuard");

        guards.MapDelete("/{id}", DeactivateAsync)
            .WithName("DeactivateAdminGuard");

        var duties = admin.MapGroup("/duties");
        duties.MapGet("", GetDutiesAsync)
            .WithName("GetAdminDuties");

        duties.MapPut("/{date}", UpsertDutyAsync)
            .WithName("UpsertAdminDuty");
    }

    private static async Task<IResult> CreateAsync(HttpRequest request, IStores stores, CancellationToken ct)
    {
        var body = await Req.BodyAsync<GuardBody>(request, ct);
        var guard = await stores.CreateGuardAsync(body.Name, body.Phone, ct);
        return Results.Created($"/api/admin/guards/{guard.Id}", guard);
    }

    private static async Task<IResult> UpdateAsync(string id, HttpRequest request, IStores stores, CancellationToken ct)
    {
        var guardId = Req.Id(id);
        if (guardId <= 0)
        {
            throw Errors.InvalidId();
        }

        var body = await Req.BodyAsync<AdminGuardBody>(request, ct);
        return Results.Ok(await stores.UpdateGuardAsync(guardId, body.Name, body.Phone, body.Active, ct));
    }

    private static async Task<IResult> DeactivateAsync(string id, IStores stores, CancellationToken ct)
    {
        var guardId = Req.Id(id);
        if (guardId <= 0)
        {
            throw Errors.InvalidId();
        }

        await stores.DeactivateGuardAsync(guardId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetDutiesAsync(HttpRequest request, IStores stores, ShiftCalendar calendar, CancellationToken ct)
    {
        var season = request.Query["season"].ToString();
        var yearValue = request.Query["year"].ToString();
        if (!int.TryParse(yearValue, CultureInfo.InvariantCulture, out var year)
            || year < 2000
            || year > 2100
            || !calendar.TryGetSeasonRange(season, year, out var from, out var to))
        {
            throw Errors.InvalidSeason();
        }

        var shifts = await stores.GetAdminShiftsAsync(from, to, calendar, ct);
        var duties = shifts.Select(ToAdminDuty).ToArray();
        var totals = duties
            .Where(duty => duty.Guard is not null)
            .GroupBy(duty => duty.Guard!, duty => duty, (guard, grouped) =>
            {
                var entries = grouped.ToArray();
                return new AdminDutyTotalDto(
                    guard,
                    entries.Length,
                    entries.Sum(entry => entry.DurationHours),
                    entries.Count(entry => !entry.HasRecordedCheckOut));
            })
            .OrderBy(total => total.Guard.Name, StringComparer.Ordinal)
            .ToArray();

        return Results.Ok(new AdminDutyListDto(
            season.Trim().ToLowerInvariant(),
            year,
            ShiftDto.Iso(from),
            ShiftDto.Iso(to),
            duties,
            totals));
    }

    private static async Task<IResult> UpsertDutyAsync(
        string date,
        HttpRequest request,
        IStores stores,
        ShiftCalendar calendar,
        CancellationToken ct)
    {
        var parsedDate = Req.Date(date);
        if (!calendar.IsAdminDutyDay(parsedDate))
        {
            throw Errors.NotAnAdminDutyDay(StatusCodes.Status422UnprocessableEntity);
        }

        var body = await Req.BodyAsync<AdminDutyBody>(request, ct);
        if (body.GuardId is null)
        {
            if (!string.IsNullOrWhiteSpace(body.CheckOutTime))
            {
                throw Errors.InvalidBody();
            }

            return Results.Ok(await stores.UpsertAdminShiftAsync(parsedDate, null, null, ct));
        }

        if (body.GuardId <= 0)
        {
            throw Errors.InvalidBody();
        }

        var signedOffAt = ParseCheckOutTime(parsedDate, body.CheckOutTime);
        return Results.Ok(await stores.UpsertAdminShiftAsync(parsedDate, body.GuardId.Value, signedOffAt, ct));
    }

    private static bool IsAuthorized(HttpRequest request, string? expectedKey, string? expectedUsername, string? expectedPassword)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (IsMatchingBearer(authorization, expectedKey))
        {
            return true;
        }

        return IsMatchingBasic(authorization, expectedUsername, expectedPassword);
    }

    private static bool IsMatchingBearer(string authorization, string? expectedKey)
    {
        if (string.IsNullOrWhiteSpace(expectedKey))
        {
            return false;
        }

        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suppliedKey = authorization[prefix.Length..];
        return MatchesSecret(expectedKey, suppliedKey);
    }

    private static bool IsMatchingBasic(string authorization, string? expectedUsername, string? expectedPassword)
    {
        if (string.IsNullOrWhiteSpace(expectedUsername) || string.IsNullOrWhiteSpace(expectedPassword))
        {
            return false;
        }

        const string prefix = "Basic ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        byte[] buffer;
        try
        {
            buffer = Convert.FromBase64String(authorization[prefix.Length..]);
        }
        catch (FormatException)
        {
            return false;
        }

        var decoded = Encoding.UTF8.GetString(buffer);
        var separator = decoded.IndexOf(':');
        if (separator < 0)
        {
            return false;
        }

        var suppliedUsername = decoded[..separator];
        var suppliedPassword = decoded[(separator + 1)..];
        return MatchesSecret(expectedUsername, suppliedUsername) && MatchesSecret(expectedPassword, suppliedPassword);
    }

    private static bool MatchesSecret(string expected, string supplied)
    {
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }

    private static DateTimeOffset? ParseCheckOutTime(DateOnly date, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var checkOutTime))
        {
            throw Errors.InvalidTime();
        }

        return Clock.ToUtc(date, checkOutTime);
    }
    private static AdminDutyDto ToAdminDuty(ShiftDto shift)
    {
        var recordedCheckOut = ParseSignedOffAt(shift.SignedOffAt);
        var endTime = recordedCheckOut is null ? ScheduledEnd : Clock.ToLocalTime(recordedCheckOut.Value);
        var durationHours = RoundUpToHalfHour((decimal)(endTime.ToTimeSpan() - ScheduledStart.ToTimeSpan()).TotalHours);

        return new AdminDutyDto(
            shift.Date,
            shift.DayOfWeek,
            shift.Status,
            shift.Guard,
            ScheduledStart.ToString("HH:mm", CultureInfo.InvariantCulture),
            endTime.ToString("HH:mm", CultureInfo.InvariantCulture),
            recordedCheckOut is not null,
            durationHours);
    }

    private static DateTimeOffset? ParseSignedOffAt(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private static decimal RoundUpToHalfHour(decimal hours)
    {
        if (hours <= 0)
        {
            return 0m;
        }

        return Math.Ceiling(hours * 2m) / 2m;
    }
}