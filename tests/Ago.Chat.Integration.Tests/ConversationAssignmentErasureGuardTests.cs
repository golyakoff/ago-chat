using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Worker;
using Ago.Platform.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `adr/0101`'s own central claim, guarded rather than merely documented: erasing a conversation
/// (`16-02`/`23-08`'s <see cref="ConversationErasureJob"/>, which deletes the whole `conversations` row,
/// not just its content - `ConversationErasureQuery.DeleteConversationAsync`) must not take
/// `conversation_assignments` with it - `decisions.md` §2's "erasing a conversation need not take last
/// month's numbers with it."
///
/// <para><b>This is a guard, not a fix - true today by construction.</b>
/// <c>conversation_assignments.conversation_id</c> carries no foreign key at all
/// (<c>ConversationAssignmentIntervalConfiguration</c>'s own remarks), so there is nothing for
/// <see cref="ConversationErasureJob"/> to cascade into even if it wanted to, and that job's own code
/// never mentions <c>conversation_assignments</c> - there is no code path from erasure to this table to
/// break, and the fails-before table for this item records exactly that: this test cannot be made to
/// fail by mutating <see cref="ConversationErasureJob"/>'s own code today.</para>
///
/// <para><b>Why it still earns its place.</b> It exists so a future change - a foreign key added "for
/// consistency" with `conversation_notes`/`conversation_tags`, or an explicit drain step added here by
/// a reviewer who assumed the same symmetry those two tables use - cannot quietly reverse `adr/0101`'s
/// decision with no suite going red. `MessagePartitionPruneJobTests.PruneAsync_RemovesTheExpiredMessage_ButLeavesTheVisitorsContactDetailsStanding`
/// is the identical shape for a different table and a different amendment (`23-08`/`decisions.md` §4) -
/// this is that same guard, for this table's own erasure exception.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConversationAssignmentErasureGuardTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EraseConversationAsync_DeletesTheConversation_ButLeavesItsAssignmentIntervalsStanding()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var intervalId = new ConversationAssignmentId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));

            var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
            conversation.AssignTo(operatorId, Now);
            conversation.Close(Now.AddMinutes(10));
            db.Conversations.Add(conversation);

            // One closed interval, standing in for the assignment history a real closed conversation
            // would have - exactly the row this test exists to prove survives the conversation's own
            // deletion.
            var interval = ConversationAssignmentInterval.Open(
                intervalId, siteId, conversationId, operatorId, ConversationAssignmentSource.Assigned, Now);
            interval.Close(Now.AddMinutes(10));
            db.ConversationAssignments.Add(interval);

            await db.SaveChangesAsync();
        }

        var erasureOptions = new ConversationErasureJobOptions();
        var archiveEraser = new ConversationArchiveEraser(
            new FakeFileStorage(), new MessageArchiveRepository(fixture.DataSource), erasureOptions,
            NullLogger<ConversationArchiveEraser>.Instance);
        var job = new ConversationErasureJob(
            fixture.DataSource, new FakeFileStorage(), archiveEraser, new SystemClock(),
            Options.Create(erasureOptions), NullLogger<ConversationErasureJob>.Instance);

        // Called directly rather than through SweepAsync's own claim query - this test does not need
        // erasure_requested_at set, since EraseConversationAsync itself has no opinion about that flag;
        // claiming pending rows is SweepAsync's own job, not this method's precondition.
        var erased = await job.EraseConversationAsync(conversationId.Value, siteId.Value, visitorId.Value, CancellationToken.None);
        Assert.True(erased);

        await using var verify = fixture.CreateDbContext();
        Assert.Equal(0, await verify.Conversations.CountAsync(c => c.Id == conversationId));

        // The conversation is gone; its assignment interval is not, and is still readable through the
        // ordinary DbSet - not merely present as an orphaned row nothing can reach.
        var survivingInterval = await verify.ConversationAssignments.AsNoTracking().SingleAsync(i => i.Id == intervalId);
        Assert.Equal(conversationId, survivingInterval.ConversationId);
        Assert.Equal(operatorId, survivingInterval.OperatorId);
        Assert.NotNull(survivingInterval.EndedAt);
    }
}
