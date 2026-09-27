using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Mapping;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Infrastructure.Postgres;

/// <summary>
/// `23-03`'s <see cref="IConversationAssignmentLog"/> - EF change-tracking, not raw SQL, and
/// deliberately so: see the port's own remarks on why neither method here calls
/// <c>SaveChangesAsync</c>. <see cref="Open"/> adds a tracked entity; <see cref="CloseOpenAsync"/>
/// loads one and mutates it. Both ride whatever <c>SaveChangesAsync</c> (or explicit transaction) the
/// caller's own <see cref="AgoChatDbContext"/> commits next - the same instance
/// <c>ConversationRepository</c>/<c>OperatorCapacityStore</c> were constructed with for the same
/// request or batch, exactly like every other adapter in this project that needs to land inside a
/// caller-owned unit of work rather than open its own.
///
/// <para><b>`26-237`/`adr/0186` (operator-load rollup, decision B): this is also the one publisher of the
/// <see cref="ConversationAssignmentClosed"/> analytics event.</b> Every interval in the product closes
/// through <see cref="CloseOpenAsync"/> (assign-transfer's own close, conversation close/spam, idle
/// release, the Worker releaser), so it is the single seam where the operator-load rollup's own input can
/// be emitted exactly once per closed interval. The values the rollup needs but cannot recompute from
/// independent per-event rows - the operator's own concurrent load at the interval's start
/// (<see cref="ConversationAssignmentOverlapQuery.CountHeldAtAsync"/>, the identical overlap the live
/// report computes, resolved once here rather than O(intervals) on every read), the operator's capacity,
/// and the interval's first operator reply - are resolved at close and staged to the outbox in the same
/// unit of work as the close itself (rule 4). If the caller's transaction rolls back, the close and the
/// event vanish together; if it commits, they commit together. This follows <c>TagRepository</c>'s own
/// "the adapter that owns the write owns its event" precedent for a write with no aggregate to route a
/// domain event through - except here no explicit transaction is opened, because the outbox row is a
/// plain EF <c>Enqueue</c> on this same context that rides the caller's own <c>SaveChangesAsync</c>, the
/// way the interval's <see cref="ConversationAssignmentInterval.Close"/> already does.</para>
///
/// <para><b>The overlap and reply reads use the shared <see cref="NpgsqlDataSource"/>, not this context's
/// transaction.</b> They are read-only counts over committed data (an interval's concurrent load at its
/// start depends only on already-committed intervals - this interval's own not-yet-committed close does
/// not change its start-instant overlap), so reading them on a pooled connection is correct and never
/// blocks: plain <c>SELECT</c>s do not wait on the caller's row locks. The one uncommitted change (this
/// interval's <c>ended_at</c>) is irrelevant to the count at its own start, which is exactly why the value
/// this stamps equals the one the live report computes post-close.</para>
///
/// <para><b>The three analytics collaborators are optional</b> (<see langword="null"/> by default). In a
/// host they are always supplied - the DI container fills every parameter it has a registration for
/// (<see cref="NpgsqlDataSource"/>, <see cref="IOutboxWriter"/> and <see cref="IIdGenerator"/> are all
/// registered), and the Worker releaser constructs this type with them explicitly - so production always
/// emits. A bare <c>new ConversationAssignmentLog(db)</c> - used only by tests that exercise the interval
/// write itself, not its analytics side effect - closes without emitting. That the production emission
/// path is exercised is proven by its own integration test, not left to the bare-context tests.</para>
/// </summary>
public sealed class ConversationAssignmentLog(
    AgoChatDbContext db,
    NpgsqlDataSource? dataSource = null,
    IOutboxWriter? outbox = null,
    IIdGenerator? idGenerator = null)
    : IConversationAssignmentLog
{
    public void Open(ConversationAssignmentInterval interval) =>
        db.Set<ConversationAssignmentInterval>().Add(interval);

    public async Task CloseOpenAsync(ConversationId conversationId, DateTimeOffset endedAt, CancellationToken cancellationToken)
    {
        // At most one row can match: a conversation has at most one operator at a time, so it has at
        // most one open interval. No match at all is the honest, expected state for a conversation
        // assigned before this item shipped - see the port's own remarks on why that is a no-op, not
        // an error, and why nothing is then emitted either.
        var open = await db.Set<ConversationAssignmentInterval>()
            .FirstOrDefaultAsync(i => i.ConversationId == conversationId && i.EndedAt == null, cancellationToken);
        if (open is null)
        {
            return;
        }

        open.Close(endedAt);

        if (dataSource is not null && outbox is not null && idGenerator is not null)
        {
            await EnqueueClosedAnalyticsEventAsync(open, endedAt, dataSource, outbox, idGenerator, cancellationToken);
        }
    }

    // `26-237`: resolve the operator-load facts at close and stage the analytics event. All three reads are
    // read-only over committed data; the Enqueue is an EF-tracked outbox row on this same context, so it
    // commits with the caller's own SaveChangesAsync (rule 4) - no explicit transaction needed here.
    private async Task EnqueueClosedAnalyticsEventAsync(
        ConversationAssignmentInterval interval,
        DateTimeOffset endedAt,
        NpgsqlDataSource dataSource,
        IOutboxWriter outbox,
        IIdGenerator idGenerator,
        CancellationToken cancellationToken)
    {
        var concurrentLoad = await ConversationAssignmentOverlapQuery.CountHeldAtAsync(
            dataSource, interval.OperatorId, interval.StartedAt, cancellationToken);

        // Capacity as of close (the same live-value the report joins today) and the tenant zone - plain
        // scalar reads on this context, never a decision this write depends on (rule 8's stamp-not-gate
        // carve-out, the same read TagRepository.ResolveTenantZoneAsync makes for the identical reason).
        var capacity = await db.Operators
            .Where(o => o.Id == interval.OperatorId)
            .Select(o => (int?)o.Capacity)
            .FirstOrDefaultAsync(cancellationToken) ?? 0;

        var tenantZone = await db.Sites
            .Where(s => s.Id == interval.SiteId)
            .Select(s => s.TimeZone)
            .FirstOrDefaultAsync(cancellationToken) ?? "Europe/Moscow";

        var firstReplyAt = await ConversationAssignmentReplyQuery.FirstOperatorReplyAtAsync(
            dataSource, interval.SiteId, interval.ConversationId, interval.OperatorId,
            interval.StartedAt, endedAt, cancellationToken);

        outbox.Enqueue(ConversationAssignmentClosedMapper.ToEnvelope(
            interval.ConversationId.Value,
            interval.SiteId.Value,
            interval.OperatorId.Value,
            interval.StartedAt,
            endedAt,
            concurrentLoad,
            capacity,
            firstReplyAt,
            tenantZone,
            idGenerator));
    }
}
