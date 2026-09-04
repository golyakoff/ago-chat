using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Worker;

/// <summary>
/// `23-03`: the interval row for <see cref="SkipLockedAssignmentClaimer"/> and
/// <see cref="RedisLockAssignmentClaimer"/> only - raw SQL, deliberately not
/// <c>IConversationAssignmentLog</c>. Both claimers already write everything else about a claim (the
/// capacity compare-and-set via <c>OperatorCapacityStore</c>, the conversation's own save) as
/// statements against the one <c>AgoChatDbContext</c> they built on their own connection and
/// transaction for this batch - adding the port here as a second, differently-shaped way to reach the
/// identical transaction would let a future edit change one path (say, swap the port for a queued
/// write) without the other failing to compile, which is exactly how "a claim commits without its
/// interval" happens unnoticed. Keeping this call textually next to <c>AssignTo</c> in each claimer,
/// issued through the same <c>db.Database.ExecuteSqlInterpolatedAsync</c> idiom
/// <c>OperatorCapacityStore</c>'s own claim already uses on this same connection, makes the coupling
/// visible in the diff instead of hidden behind an interface.
///
/// <para>A shared static method rather than duplicating the five-line <c>INSERT</c> in both claimer
/// files: the SQL text itself has no reason to differ between the two mechanisms, and a second,
/// silently-drifted copy would be worse than a shared one two callers both see.</para>
/// </summary>
internal static class ConversationAssignmentIntervalSql
{
    public static Task InsertOpenAsync(
        AgoChatDbContext db, IIdGenerator idGenerator, SiteId siteId, ConversationId conversationId,
        OperatorId operatorId, ConversationAssignmentSource source, DateTimeOffset startedAt,
        CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO conversation_assignments (id, site_id, conversation_id, operator_id, started_at, ended_at, source)
            VALUES ({idGenerator.NewId(startedAt)}, {siteId.Value}, {conversationId.Value}, {operatorId.Value}, {startedAt}, NULL, {source.ToString()})
            """,
            cancellationToken);
}
