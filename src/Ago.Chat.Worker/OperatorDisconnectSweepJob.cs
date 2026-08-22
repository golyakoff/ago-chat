using Ago.Chat.Application.Realtime;
using Ago.Chat.Domain;
using Ago.Chat.Module;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `4-04`'s periodic backstop: catches an operator disconnect that never fired
/// `OperatorHub.OnDisconnectedAsync`'s own `OperatorPresenceLost` at all - a hard process kill on
/// the client side, or `Ago.Chat.Api` itself dying before the publish completed. Every tick, for
/// every operator with at least one `Assigned` conversation, checks presence
/// (`IConnectionRegistry`) and re-publishes `OperatorPresenceLost` for any with none - reusing the
/// exact same signal `OperatorDisconnectGraceConsumer` already waits on, so the grace-period logic
/// stays in one place rather than being duplicated here.
///
/// Presence is read, never trusted as definitive (`adr/0009`): a stale-but-not-yet-expired registry
/// entry biases this sweep toward "assume still connected," the same direction `OperatorHub`'s own
/// fast path already leans (a connection genuinely gone is what deregisters its entry immediately;
/// this only ever finds entries that are *actually* absent, never a guess). Getting this wrong the
/// other way - releasing a still-connected operator's conversations - would be the more visible,
/// more damaging mistake, so leaning toward "wait, don't release" here is the deliberate choice.
/// </summary>
public sealed class OperatorDisconnectSweepJob(
    NpgsqlDataSource dataSource,
    IConnectionRegistry connectionRegistry,
    OperatorPresencePublisher presencePublisher,
    IOptions<OperatorDisconnectSweepJobOptions> options,
    ILogger<OperatorDisconnectSweepJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // concurrency.md: a BackgroundService catches and continues - a transient Postgres
                // or Redis blip here must not permanently kill the backstop sweep.
                logger.LogError(ex, "Operator-disconnect sweep cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken)); // runs once immediately, then every Interval
    }

    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        foreach (var (operatorId, siteId) in await GetOperatorsWithAssignedConversationsAsync(cancellationToken))
        {
            var connections = await connectionRegistry.GetConnectionsAsync(
                PrincipalKeys.ForOperator(operatorId), cancellationToken);
            if (connections.Count == 0)
            {
                await presencePublisher.PublishLostAsync(operatorId, siteId, cancellationToken);
            }
        }
    }

    private async Task<IReadOnlyList<(OperatorId OperatorId, SiteId SiteId)>> GetOperatorsWithAssignedConversationsAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT DISTINCT o.id, o.site_id
            FROM operators o
            JOIN conversations c ON c.operator_id = o.id
            WHERE c.state = 'Assigned'
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);

        var results = new List<(OperatorId, SiteId)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add((new OperatorId(reader.GetGuid(0)), new SiteId(reader.GetGuid(1))));
        }

        return results;
    }
}
