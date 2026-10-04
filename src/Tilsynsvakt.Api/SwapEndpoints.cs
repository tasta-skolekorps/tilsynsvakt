namespace Tilsynsvakt.Api;

public static class SwapEndpoints
{
    public static void MapSwapEndpoints(this WebApplication app)
    {
        var swaps = app.MapGroup("/api/swap-requests").RequireCors("frontend");

        swaps.MapGet("", GetPendingAsync)
            .WithName("GetSwapRequests")
            .WithSummary("List pending swap requests")
            .WithDescription("Returns pending requests to swap two taken shifts. Requests whose shifts have changed are omitted.")
            .Produces<IReadOnlyList<SwapRequestDto>>(StatusCodes.Status200OK);

        swaps.MapPost("", CreateAsync)
            .RequireRateLimiting("mutations")
            .WithName("CreateSwapRequest")
            .WithSummary("Request a shift swap")
            .WithDescription("Asks the holder of the target shift to swap shifts with the requester.")
            .Produces<SwapRequestDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        swaps.MapPost("/{date}/{targetDate}/accept", AcceptAsync)
            .RequireRateLimiting("mutations")
            .WithName("AcceptSwapRequest")
            .WithSummary("Approve a swap request")
            .WithDescription("Swaps the two shifts when the target guard approves and both shifts are unchanged.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        swaps.MapDelete("/{date}/{targetDate}", DeleteAsync)
            .RequireRateLimiting("mutations")
            .WithName("DeleteSwapRequest")
            .WithSummary("Decline or withdraw a swap request")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> GetPendingAsync(IStores stores, TimeProvider clock, CancellationToken ct)
    {
        var today = Clock.Today(clock);
        var pending = new List<SwapRequestDto>();
        foreach (var request in await stores.GetSwapRequestsAsync(ct))
        {
            var date = Req.Date(request.Date);
            var targetDate = Req.Date(request.TargetDate);
            if (date < today || targetDate < today)
            {
                continue;
            }

            var own = await stores.GetShiftAsync(date, ct);
            var other = await stores.GetShiftAsync(targetDate, ct);
            if (own?.Guard?.Id == request.Requester.Id && other?.Guard?.Id == request.Target.Id)
            {
                pending.Add(request);
            }
        }

        return Results.Ok(pending);
    }

    private static async Task<IResult> CreateAsync(
        HttpRequest request, IStores stores, ShiftCalendar calendar, TimeProvider clock, CancellationToken ct)
    {
        var body = await Req.BodyAsync<SwapRequestBody>(request, ct);
        var date = Req.Date(body.Date);
        var targetDate = Req.Date(body.TargetDate);
        var requesterId = body.RequesterGuardId is > 0 ? body.RequesterGuardId.Value : throw Errors.InvalidBody();
        Validate(date, targetDate, calendar, clock);
        return Results.Ok(await stores.CreateSwapRequestAsync(date, targetDate, requesterId, ct));
    }

    private static async Task<IResult> AcceptAsync(
        string date, string targetDate, HttpRequest request, IStores stores, ShiftCalendar calendar, TimeProvider clock, CancellationToken ct)
    {
        var body = await Req.BodyAsync<SwapAcceptBody>(request, ct);
        var guardId = body.GuardId is > 0 ? body.GuardId.Value : throw Errors.InvalidBody();
        var first = Req.Date(date);
        var second = Req.Date(targetDate);
        Validate(first, second, calendar, clock);
        await stores.AcceptSwapRequestAsync(first, second, guardId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(
        string date, string targetDate, HttpRequest request, IStores stores, CancellationToken ct)
    {
        var guardId = request.Query.TryGetValue("guardId", out var value) ? Req.Id(value.ToString()) : throw Errors.InvalidId();
        if (guardId <= 0)
        {
            throw Errors.InvalidId();
        }

        await stores.DeleteSwapRequestAsync(Req.Date(date), Req.Date(targetDate), guardId, ct);
        return Results.NoContent();
    }

    private static void Validate(DateOnly date, DateOnly targetDate, ShiftCalendar calendar, TimeProvider clock)
    {
        if (date == targetDate)
        {
            throw Errors.SwapInvalid();
        }

        var today = Clock.Today(clock);
        foreach (var candidate in new[] { date, targetDate })
        {
            if (!calendar.IsShiftDay(candidate))
            {
                throw Errors.NotAShiftDay(StatusCodes.Status422UnprocessableEntity);
            }

            if (candidate < today)
            {
                throw Errors.DateInPast();
            }

            if (candidate > today.AddDays(400))
            {
                throw Errors.DateTooFarAhead();
            }
        }
    }
}
