namespace Tilsynsvakt.Api;

public static class GuardEndpoints
{
    public static void MapGuardEndpoints(this WebApplication app)
    {
        app.MapGet("/api/guards", async (IStores stores, CancellationToken ct) =>
                Results.Ok(await stores.GetActiveGuardsAsync(ct)))
            .RequireCors("frontend")
            .WithName("GetGuards")
            .WithSummary("List active guards")
            .WithDescription("Returns active guards available for shift signup.")
            .Produces<IReadOnlyList<GuardDto>>(StatusCodes.Status200OK);
    }
}