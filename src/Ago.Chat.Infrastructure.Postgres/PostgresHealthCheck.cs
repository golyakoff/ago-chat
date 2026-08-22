using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// 2-04: replaces the trivial always-healthy check from 0-03 with one that actually opens a
/// connection - readiness should mean "can do the job," not "the process is running."
///
/// Moved here from Ago.Chat.Worker at 3-06, not to Ago.Chat.Module: Ago.Chat.Api needed the
/// identical check too, but Module reaching for `Npgsql` directly would violate
/// `PersistenceBoundaryTests` (adr/0004, "one project per external technology") the same as a host
/// would - Infrastructure.Postgres is the one place allowed to see Npgsql at all, health check
/// included, and both hosts already depend on it transitively through Module.
/// </summary>
public sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Cannot reach Postgres.", ex);
        }
    }
}
