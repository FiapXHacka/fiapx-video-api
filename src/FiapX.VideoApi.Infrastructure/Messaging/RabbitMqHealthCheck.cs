using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FiapX.VideoApi.Infrastructure.Messaging;

internal sealed class RabbitMqHealthCheck(IRabbitMqConnection connection) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var conn = await connection.GetAsync(cancellationToken);
            return conn.IsOpen
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("connection is closed");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy(ex.Message, ex);
        }
    }
}
