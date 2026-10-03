using System.Security.Cryptography;
using System.Text;

namespace Tilsynsvakt.Api;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app, string apiKey)
    {
        var admin = app.MapGroup("/api/admin/guards")
            .ExcludeFromDescription()
            .RequireRateLimiting("admin")
            .AddEndpointFilter((context, next) =>
            {
                if (!IsAuthorized(context.HttpContext.Request, apiKey))
                {
                    return ValueTask.FromResult<object?>(Errors.Unauthorized().ToResult());
                }

                return next(context);
            });

        admin.MapGet("", async (IStores stores, CancellationToken ct) =>
                Results.Ok(await stores.GetAllGuardsAsync(ct)))
            .WithName("GetAdminGuards");

        admin.MapPost("", CreateAsync)
            .WithName("CreateAdminGuard");

        admin.MapPut("/{id}", UpdateAsync)
            .WithName("UpdateAdminGuard");

        admin.MapDelete("/{id}", DeactivateAsync)
            .WithName("DeactivateAdminGuard");
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

    private static bool IsAuthorized(HttpRequest request, string expectedKey)
    {
        var authorization = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suppliedKey = authorization[prefix.Length..];
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expectedKey));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedKey));
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }
}