using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Tilsynsvakt.Api;

public sealed class TableHealthCheck(IStoreLifecycle lifecycle) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await lifecycle.CheckReadyAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Table Storage unavailable", ex);
        }
    }
}