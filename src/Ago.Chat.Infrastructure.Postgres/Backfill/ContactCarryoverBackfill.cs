using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres.Backfill;

/// <summary>
/// `23-59`/`adr/0147`: the retroactive carry-over pass, and the reason it does <b>not</b> take
/// <see cref="RoleAssignmentProjectionBackfill"/>'s own shape even though the brief that named it asked
/// this class to use that shape "if it fits" - said here, not merely assumed, because it does not.
///
/// <para><b>Why not that shape.</b> <see cref="RoleAssignmentProjectionBackfill"/> is a manually
/// triggered, run-to-completion pass over a bounded population (a site's own operators, at most a
/// handful) with no persisted cursor - restarting it after a crash simply reruns the whole thing, which
/// is cheap because the population is small and every republish is a no-op at the far side. Neither
/// property holds here: a tenant's contact history is explicitly unbounded ("a tenant with years of
/// chat history" - `adr/0147`'s own words), and `23-104` - landed in this same stage - is a standing
/// lesson against relying on a human to remember to run a tool at all ("a tool somebody has to remember
/// to run is what this item exists to stop needing"). This class is instead driven automatically, by
/// <c>Ago.Chat.Worker.ContactCarryoverJob</c>'s own recurring sweep, and processes one bounded batch of
/// one site at a time, persisting exactly how far it got - the same resumable-batch shape
/// <c>ConversationErasureJob</c>/<c>SiteErasureJob</c> already use for their own unbounded, per-tenant
/// work, adapted here because it is the closer fit, not because the brief did not offer a choice.</para>
///
/// <para><b>Every contact detail, every kind - not filtered to <c>Phone</c> here.</b> The identical
/// reasoning <see cref="Contracts.ContactCollected"/>'s own remarks give for the live publish path:
/// this class stays as ignorant as <c>RecordVisitorContactDetailHandler</c> of what the far side does
/// with a non-phone contact, and the calendar's own consumer is where that filtering happens.</para>
/// </summary>
public sealed class ContactCarryoverBackfill(AgoChatDbContext db, IIdGenerator idGenerator, IClock clock)
{
    /// <summary>
    /// One site, one bounded batch, one transaction: reads up to <paramref name="batchSize"/> contact
    /// details this site has that this request has not yet staged, stages
    /// <see cref="Contracts.ContactCollected"/> for each, advances the cursor past the last one, and -
    /// only when the batch came back short of <paramref name="batchSize"/>, meaning nothing is left -
    /// marks the request complete. A crash or a thrown exception anywhere in this method rolls the
    /// whole batch back, leaving the request's own cursor exactly where the previous successful batch
    /// left it - <see cref="Persistence.ContactCarryoverRequestEntity"/>'s own remarks on why that is
    /// the entire answer to "a failed carry-over can be re-run without re-granting anything": the next
    /// call to this method, from the next sweep tick, is that re-run, with nothing else to trigger.
    /// </summary>
    public async Task<ContactCarryoverBatchOutcome> RunOneBatchAsync(
        SiteId siteId, int batchSize, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        var request = await db.ContactCarryoverRequests
            .FirstOrDefaultAsync(r => r.SiteId == siteId, cancellationToken);
        if (request is null || request.CompletedAt is not null)
        {
            // Nothing pending - either no grant ever requested a carry-over for this site, or an
            // earlier batch already finished it. Both read identically to the caller (ContactCarryoverJob
            // simply will not have found this site in its own candidate list in the first place under
            // ordinary operation; this branch exists for the same "re-checked, not trusted" defence
            // every candidate-list consumer in this codebase applies to a list read outside a lock).
            return ContactCarryoverBatchOutcome.NothingPending;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var npgsqlTransaction = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();

        var rows = new List<(Guid Id, string Kind, string Value, DateTimeOffset RecordedAt)>();

        // Keyset pagination on the contact detail's own id, not offset: the same reasoning
        // ITenantRepository.ListIdsAsync's own remarks give (ago-calendar) - a walk that costs the same
        // on the last page as the first, and does not skip or repeat a row when the table it reads is
        // being written to concurrently by a live contact arriving mid-walk. VisitorContactDetailId is
        // UUIDv7 (time-ordered), so this also happens to visit contacts oldest-first, though nothing
        // here depends on that - only on every id being visited exactly once across the whole run.
        var sql = request.CursorContactId is null
            ? """
              select vcd.id, vcd.kind, vcd.value, vcd.recorded_at
              from visitor_contact_details vcd
              join visitors v on v.id = vcd.visitor_id
              where v.site_id = @siteId
              order by vcd.id
              limit @batchSize
              """
            : """
              select vcd.id, vcd.kind, vcd.value, vcd.recorded_at
              from visitor_contact_details vcd
              join visitors v on v.id = vcd.visitor_id
              where v.site_id = @siteId and vcd.id > @cursor
              order by vcd.id
              limit @batchSize
              """;

        await using (var command = new NpgsqlCommand(sql, connection, npgsqlTransaction))
        {
            command.Parameters.AddWithValue("siteId", siteId.Value);
            command.Parameters.AddWithValue("batchSize", batchSize);
            if (request.CursorContactId is { } cursor)
            {
                command.Parameters.AddWithValue("cursor", cursor);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                    reader.GetFieldValue<DateTimeOffset>(3)));
            }
        }

        var outbox = new EfOutboxWriter<AgoChatDbContext>(db);
        foreach (var row in rows)
        {
            outbox.Enqueue(ContactCollectedMapper.ToEnvelope(
                row.Id, siteId.Value, row.Kind, row.Value, row.RecordedAt, now, idGenerator));
        }

        if (rows.Count > 0)
        {
            request.CursorContactId = rows[^1].Id;
        }

        var completed = rows.Count < batchSize;
        if (completed)
        {
            request.CompletedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ContactCarryoverBatchOutcome(rows.Count, completed);
    }

    /// <summary>Every site with a request still pending, oldest request first -
    /// <c>ContactCarryoverJob.SweepAsync</c>'s own candidate list.</summary>
    public async Task<IReadOnlyList<SiteId>> ListPendingSiteIdsAsync(int limit, CancellationToken cancellationToken) =>
        await db.ContactCarryoverRequests
            .AsNoTracking()
            .Where(r => r.CompletedAt == null)
            .OrderBy(r => r.RequestedAt)
            .Select(r => r.SiteId)
            .Take(limit)
            .ToListAsync(cancellationToken);
}

/// <summary>One batch's own result. <paramref name="Completed"/> is <see langword="true"/> only when
/// this batch came back short of the batch size it asked for, meaning the site's contact history has
/// been fully walked.</summary>
public sealed record ContactCarryoverBatchOutcome(int Published, bool Completed)
{
    public static readonly ContactCarryoverBatchOutcome NothingPending = new(0, Completed: true);
}
