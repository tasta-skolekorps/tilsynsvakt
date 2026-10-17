namespace Tilsynsvakt.Api;

public sealed record LoginBody(string? Password);

public sealed record LoginResponse(string ApiKey);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/login", async (HttpContext context, AccessSettings settings, CancellationToken ct) =>
            {
                var body = await Req.BodyAsync<LoginBody>(context.Request, ct);
                if (string.IsNullOrWhiteSpace(body.Password))
                {
                    throw Errors.InvalidBody();
                }

                if (!AccessKey.Matches(settings.Password, body.Password))
                {
                    throw Errors.InvalidPassword();
                }

                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new LoginResponse(settings.ApiKey));
            })
            .RequireCors("frontend")
            .RequireRateLimiting("login")
            .WithName("Login")
            .WithSummary("Exchange the shared password for an API key")
            .WithDescription("Returns the API key to send in the X-Api-Key header on all other /api endpoints.")
            .Produces<LoginResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);
    }
}
