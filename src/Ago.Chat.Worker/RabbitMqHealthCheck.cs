using Ago.Platform.Messaging.RabbitMq;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ago.Chat.Worker;

/// <summary>2-04: readiness must reflect whether the dispatcher can actually reach the broker, not
/// just that the process started.</summary>
public sealed class RabbitMqHealthCheck(RabbitMqConnection connection) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Cannot reach RabbitMQ.", ex);
        }
    }
}
