using Ago.Chat.Contracts;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ago.Chat.Worker;

/// <summary>
/// `adr/0189`/`26-275` slice #3: the person-scoped counterpart to <see cref="SiteErasureJob"/> - stamps a
/// person's conversations for <see cref="ConversationErasureJob"/>'s own independent ticks, waits for
/// them to drain, then removes the <c>visitors</c> row itself. One aggregate down from
/// <see cref="SiteErasureJob"/>, and correspondingly simpler: a person has no operators, no Keycloak
/// identities, no modules and no message archive of their own to unwind, so this job's own tick is just
/// the site job's "stamp, wait, delete the parent row" skeleton with everything specific to a *site*
/// removed.
///
/// <para><b>Why this job stamps conversations and waits, rather than driving
/// <see cref="ConversationErasureJob.EraseConversationAsync"/> directly in a loop.</b> The identical
/// reasoning <see cref="SiteErasureJob"/>'s own remarks give in full: driving it directly would need that
/// job resolvable as a plain dependency here, for a benefit that is only latency - erasure is already
/// asynchronous by contract (a `PersonErased` consumer acks the moment the flag is set, long before any
/// deletion happens). Relying on <see cref="ConversationErasureJob"/>'s own ticks keeps the two jobs
/// decoupled, at the cost of at most a few extra <see cref="ConversationErasureJobOptions.Interval"/>s.</para>
///
/// Same `PeriodicTimer`/`BackgroundService` shape as every other job in this file.
/// </summary>
public sealed class PersonErasureJob(
    NpgsqlDataSource dataSource,
    IClock clock,
    IOptions<PersonErasureJobOptions> options,
    ILogger<PersonErasureJob> logger) : BackgroundService
{
    private const string TableTag = "persons_erasure";

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
                logger.LogError(ex, "Person erasure cycle failed; retrying next cycle.");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One bounded pass. <c>internal</c> for the same reason every other job in this file exposes
    /// one - an integration test drives exactly one cycle instead of waiting for a timer.</summary>
    internal async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;

        IReadOnlyList<PendingPersonErasure> pending;
        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            pending = await PersonErasureQuery.ListPendingAsync(connection, options.Value.BatchSize, cancellationToken);
        }

        var erased = 0;
        foreach (var candidate in pending)
        {
            try
            {
                if (await ProcessPersonAsync(candidate.PersonId, cancellationToken))
                {
                    erased++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One person's failure must not stop the others claimed in this cycle - the same
                // reasoning SiteErasureJob.SweepAsync's own per-site try/catch gives.
                logger.LogError(
                    ex, "Failed to process person {PersonId} for erasure; it stays flagged for the next cycle.",
                    candidate.PersonId);
            }
        }

        if (erased > 0)
        {
            logger.LogInformation("Person erasure removed {Count} person(s) and everything under them.", erased);
        }

        ChatMetrics.RecordRetentionPruneCycle(TableTag, erased, clock.UtcNow - startedAt);
        return erased;
    }

    /// <summary>
    /// One person, one tick: (a) idempotently stamp every conversation of theirs that does not carry the
    /// flag yet, (b) bail out this tick if any conversation still exists -
    /// <see cref="ConversationErasureJob"/>'s own ticks are what drains them - and only once none remain
    /// (c) delete the <c>visitors</c> row, cascading their contact details, person notes, channel
    /// identities and restrictions (<see cref="PersonErasureQuery.DeleteVisitorAsync"/>'s own remarks).
    /// A person with no conversation at all (a manual client, `26-268`) clears gate (b) on the very first
    /// tick and is fully erased in one pass.
    /// </summary>
    /// <returns><see langword="true"/> if this person was fully erased this call.</returns>
    internal async Task<bool> ProcessPersonAsync(Guid personId, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        await PersonErasureQuery.StampConversationsAsync(connection, personId, now, cancellationToken);

        if (await PersonErasureQuery.HasAnyConversationAsync(connection, personId, cancellationToken))
        {
            // Not an error and not a stall: ConversationErasureJob's own independent ticks are draining
            // these in bounded batches. Nothing more for this tick to do for this person.
            return false;
        }

        await PersonErasureQuery.DeleteVisitorAsync(connection, personId, cancellationToken);
        return true;
    }
}
