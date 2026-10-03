using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Scalar.AspNetCore;
using Tilsynsvakt.Api;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.Services.AddOpenApi();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(ShiftCalendar.FromConfiguration(builder.Configuration.GetSection("Calendar:Periods")));
builder.Services.TryAddSingleton<TableStores>();
builder.Services.TryAddSingleton<IStores>(sp => sp.GetRequiredService<TableStores>());
builder.Services.TryAddSingleton<IStoreLifecycle>(sp => sp.GetRequiredService<TableStores>());
builder.Services.AddHealthChecks()
    .AddCheck<TableHealthCheck>("table", tags: ["ready"]);

var frontendOrigin = builder.Configuration["Frontend:Origin"];
if (!string.IsNullOrWhiteSpace(frontendOrigin)
    && (!Uri.TryCreate(frontendOrigin, UriKind.Absolute, out var parsedOrigin)
        || (parsedOrigin.Scheme != Uri.UriSchemeHttp && parsedOrigin.Scheme != Uri.UriSchemeHttps)
        || parsedOrigin.AbsolutePath != "/"
        || !string.IsNullOrEmpty(parsedOrigin.Query)
        || !string.IsNullOrEmpty(parsedOrigin.Fragment)))
{
    throw new InvalidOperationException("Frontend:Origin must be a single HTTP(S) origin without a path, query, or fragment.");
}

builder.Services.AddCors(options => options.AddPolicy("frontend", policy =>
{
    if (!string.IsNullOrWhiteSpace(frontendOrigin))
    {
        policy.WithOrigins(frontendOrigin);
    }

    policy.WithMethods("GET", "POST", "PUT", "DELETE")
        .WithHeaders("Content-Type");
}));

var mutationLimit = Math.Max(1, builder.Configuration.GetValue("RateLimit:MutationsPerMinute", 30));
var adminLimit = Math.Max(1, builder.Configuration.GetValue("RateLimit:AdminPerMinute", 20));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, _) =>
        await Errors.RateLimited().ToResult().ExecuteAsync(context.HttpContext);
    options.AddPolicy("mutations", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = mutationLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
    options.AddPolicy("admin", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = adminLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
});

var app = builder.Build();
app.UseMiddleware<ProblemMiddleware>();
app.UseCors();
app.UseRateLimiter();
app.MapDefaultEndpoints();
app.MapOpenApi();
app.MapScalarApiReference();
app.MapGuardEndpoints();
app.MapShiftEndpoints();

var adminApiKey = app.Configuration["Admin:ApiKey"];
if (!string.IsNullOrWhiteSpace(adminApiKey))
{
    app.MapAdminEndpoints(adminApiKey);
}

await app.Services.GetRequiredService<IStoreLifecycle>().InitializeAsync(app.Lifetime.ApplicationStopping);
app.Run();

public partial class Program;