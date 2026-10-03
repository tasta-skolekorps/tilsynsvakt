namespace Tilsynsvakt.Api;

public static class ShiftEndpoints
{
    public static void MapShiftEndpoints(this WebApplication app)
    {
        var shifts = app.MapGroup("/api/shifts").RequireCors("frontend");

        shifts.MapGet("", GetRangeAsync)
            .WithName("GetShifts")
            .WithSummary("List eligible shifts")
            .WithDescription("Returns eligible shift dates in the requested range, including open dates.")
            .Produces<ShiftListDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        shifts.MapGet("/{date}", GetOneAsync)
            .WithName("GetShift")
            .WithSummary("Get a shift")
            .WithDescription("Returns the signup for one eligible shift date, or its open state.")
            .Produces<ShiftDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        shifts.MapPost("/{date}/signup", SignUpAsync)
            .RequireRateLimiting("mutations")
            .WithName("SignUpForShift")
            .WithSummary("Sign up for a shift")
            .WithDescription("Creates a signup without replacing another guard's signup.")
            .Produces<ShiftDto>(StatusCodes.Status200OK)
            .Produces<ShiftDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        shifts.MapPut("/{date}", ReplaceAsync)
            .RequireRateLimiting("mutations")
            .WithName("ReplaceShiftSignup")
            .WithSummary("Replace a shift signup")
            .WithDescription("Replaces the current signup only when the expected guard still holds the shift.")
            .Produces<ShiftDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        shifts.MapDelete("/{date}", DeleteAsync)
            .RequireRateLimiting("mutations")
            .WithName("CancelShiftSignup")
            .WithSummary("Cancel a shift signup")
            .WithDescription("Cancels a signup only when the expected guard still holds the shift.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status428PreconditionRequired)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }

    private static async Task<IResult> GetRangeAsync(
        HttpRequest request,
        IStores stores,
        ShiftCalendar calendar,
        TimeProvider clock,
        CancellationToken ct)
    {
        var today = Clock.Today(clock);
        var from = request.Query.TryGetValue("from", out var fromValue) ? Req.Date(fromValue.ToString()) : today;
        var to = request.Query.TryGetValue("to", out var toValue) ? Req.Date(toValue.ToString()) : today.AddDays(90);
        if (from > to || to.DayNumber - from.DayNumber >= 366)
        {
            throw Errors.InvalidRange();
        }

        var results = await stores.GetShiftsAsync(from, to, calendar, ct);
        return Results.Ok(new ShiftListDto(ShiftDto.Iso(from), ShiftDto.Iso(to), results));
    }

    private static async Task<IResult> GetOneAsync(string date, IStores stores, ShiftCalendar calendar, CancellationToken ct)
    {
        var parsedDate = Req.Date(date);
        if (!calendar.IsShiftDay(parsedDate))
        {
            throw Errors.NotAShiftDay(StatusCodes.Status404NotFound);
        }

        return Results.Ok(await stores.GetShiftAsync(parsedDate, ct) ?? ShiftDto.From(parsedDate, null));
    }

    private static async Task<IResult> SignUpAsync(
        string date,
        HttpRequest request,
        IStores stores,
        ShiftCalendar calendar,
        TimeProvider clock,
        CancellationToken ct)
    {
        var parsedDate = Req.Date(date);
        ValidateMutationDate(parsedDate, calendar, clock);
        var body = await Req.BodyAsync<SignUpBody>(request, ct);
        var guardId = RequiredId(body.GuardId);
        var result = await stores.SignUpAsync(parsedDate, guardId, ct);
        return result.Created
            ? Results.Created($"/api/shifts/{ShiftDto.Iso(parsedDate)}", result.Shift)
            : Results.Ok(result.Shift);
    }

    private static async Task<IResult> ReplaceAsync(
        string date,
        HttpRequest request,
        IStores stores,
        ShiftCalendar calendar,
        TimeProvider clock,
        CancellationToken ct)
    {
        var parsedDate = Req.Date(date);
        ValidateMutationDate(parsedDate, calendar, clock);
        var body = await Req.BodyAsync<ReplaceBody>(request, ct);
        var guardId = RequiredId(body.GuardId);
        var expectedGuardId = RequiredId(body.ExpectedGuardId);
        return Results.Ok(await stores.ReplaceAsync(parsedDate, guardId, expectedGuardId, ct));
    }

    private static async Task<IResult> DeleteAsync(
        string date,
        HttpRequest request,
        IStores stores,
        ShiftCalendar calendar,
        TimeProvider clock,
        CancellationToken ct)
    {
        var parsedDate = Req.Date(date);
        ValidateMutationDate(parsedDate, calendar, clock);
        int? expectedGuardId = request.Query.TryGetValue("expectedGuardId", out var expectedValue)
            ? Req.Id(expectedValue.ToString())
            : null;
        if (expectedGuardId is <= 0)
        {
            throw Errors.InvalidId();
        }

        await stores.DeleteAsync(parsedDate, expectedGuardId, ct);
        return Results.NoContent();
    }

    private static int RequiredId(int? value) => value is > 0 ? value.Value : throw Errors.InvalidBody();

    private static void ValidateMutationDate(DateOnly date, ShiftCalendar calendar, TimeProvider clock)
    {
        if (!calendar.IsShiftDay(date))
        {
            throw Errors.NotAShiftDay(StatusCodes.Status422UnprocessableEntity);
        }

        var today = Clock.Today(clock);
        if (date < today)
        {
            throw Errors.DateInPast();
        }

        if (date > today.AddDays(400))
        {
            throw Errors.DateTooFarAhead();
        }
    }
}