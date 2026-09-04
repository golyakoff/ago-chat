using Ago.Chat.Domain;
using Ago.Chat.Worker;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `4-04`'s atomic release claim, in isolation from the RabbitMQ/grace-period machinery around it:
/// every conversation `Assigned` to an operator is released and their capacity freed, all in one
/// transaction.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OperatorConversationReleaserTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReleaseAllAsync_ReleasesEveryAssignedConversation_AndFreesCapacityForEach()
    {
        const int capacity = 5;
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationIds = new List<ConversationId>();

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity));

            for (var i = 0; i < 3; i++)
            {
                var visitorId = new VisitorId(Guid.NewGuid());
                var conversationId = new ConversationId(Guid.NewGuid());
                db.Visitors.Add(new Visitor(visitorId, siteId, Now));
                var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
                // `6-09`: holdsCapacityClaim: true - these three stand in for engine-made assignments,
                // which is what the active_chats = 3 seeded below actually represents. The sweep now
                // releases a slot only for a conversation that holds the receipt for one; the
                // hand-picked case has its own test right underneath.
                conversation.AssignTo(operatorId, Now, holdsCapacityClaim: true);
                db.Conversations.Add(conversation);
                // `23-03`: an open interval per assignment, standing in for what
                // SkipLockedAssignmentClaimer/RedisLockAssignmentClaimer would really have written -
                // this test seeds the conversation directly rather than through either claimer, so the
                // interval has to be seeded the same deliberate way `active_chats` is seeded below.
                db.ConversationAssignments.Add(ConversationAssignmentInterval.Open(
                    new ConversationAssignmentId(Guid.NewGuid()), siteId, conversationId, operatorId,
                    ConversationAssignmentSource.Assigned, Now));
                conversationIds.Add(conversationId);
            }

            // A closed conversation for the same operator - must be left alone, it is not "Assigned"
            // anymore and releasing it would be a real bug (resurrecting a closed conversation).
            var closedVisitorId = new VisitorId(Guid.NewGuid());
            db.Visitors.Add(new Visitor(closedVisitorId, siteId, Now));
            var closed = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, closedVisitorId, Now);
            closed.AssignTo(operatorId, Now);
            closed.Close(Now);
            db.Conversations.Add(closed);

            await db.SaveChangesAsync();
        }

        // active_chats is a shadow property (4-01) - seed it directly to match the 3 AssignTo calls
        // above, since EF never writes it.
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand(
                "UPDATE operators SET active_chats = 3 WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", operatorId.Value);
            await command.ExecuteNonQueryAsync();
        }

        var releaser = new OperatorConversationReleaser(fixture.DataSource, new SystemClock(), new UuidV7Generator());
        var released = await releaser.ReleaseAllAsync(operatorId, CancellationToken.None);

        Assert.Equal(3, released);

        await using var verify = fixture.CreateDbContext();
        foreach (var conversationId in conversationIds)
        {
            var conversation = await verify.Conversations.FindAsync(conversationId);
            Assert.Equal(ConversationState.Waiting, conversation!.State);
            Assert.Null(conversation.OperatorId);

            // `23-03`'s own Done-when: OperatorConversationReleaser closes without opening.
            var interval = await verify.ConversationAssignments.SingleAsync(i => i.ConversationId == conversationId);
            Assert.NotNull(interval.EndedAt);
        }

        await using var readConnection = await fixture.DataSource.OpenConnectionAsync();
        await using var readCommand = new NpgsqlCommand("SELECT active_chats FROM operators WHERE id = @id", readConnection);
        readCommand.Parameters.AddWithValue("id", operatorId.Value);
        Assert.Equal(0, (int)(await readCommand.ExecuteScalarAsync())!);
    }

    /// <summary>
    /// `6-09`: the sweep releases a slot per conversation that actually holds one, not per assigned
    /// conversation. An operator who picked conversations up by hand
    /// (<c>AssignConversationHandler</c>, behind <c>OperatorHub.JoinConversationAsync</c>) never took
    /// a slot for them, so decrementing once per assigned conversation - what this did before - asks
    /// for more decrements than there were claims.
    ///
    /// <para><b>Stated honestly: the end state here is the same either way</b>, because
    /// <c>OperatorCapacityStore.ReleaseAsync</c> floors at zero and the sweep releases <em>every</em>
    /// one of this operator's assignments, so both the old count-assignments arithmetic and the new
    /// count-claims arithmetic land on zero. The conditional is what stops the sweep depending on that
    /// floor to be correct - it makes the sweep obey the same "one release per claim" rule
    /// <c>CloseConversationHandler</c> now obeys, so the two paths cannot drift apart when one of them
    /// changes. This test pins the invariant, not a number that used to be wrong.</para>
    /// </summary>
    [Fact]
    public async Task ReleaseAllAsync_ReleasesCapacityOnlyForConversationsThatHoldAClaim()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationIds = new List<ConversationId>();

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));

            foreach (var holdsCapacityClaim in new[] { true, false, false })
            {
                var visitorId = new VisitorId(Guid.NewGuid());
                db.Visitors.Add(new Visitor(visitorId, siteId, Now));
                var conversationId = new ConversationId(Guid.NewGuid());
                var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
                conversation.AssignTo(operatorId, Now, holdsCapacityClaim);
                db.Conversations.Add(conversation);
                // `23-03`: every assigned conversation gets an interval regardless of whether it holds
                // a capacity claim - CloseOpenAsync in the release loop is unconditional, only the
                // capacity release itself is gated on the claim.
                db.ConversationAssignments.Add(ConversationAssignmentInterval.Open(
                    new ConversationAssignmentId(Guid.NewGuid()), siteId, conversationId, operatorId,
                    ConversationAssignmentSource.Assigned, Now));
                conversationIds.Add(conversationId);
            }

            await db.SaveChangesAsync();
        }

        // One claim taken, matching the single engine-made assignment above.
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        {
            await using var command = new NpgsqlCommand(
                "UPDATE operators SET active_chats = 1 WHERE id = @id", connection);
            command.Parameters.AddWithValue("id", operatorId.Value);
            await command.ExecuteNonQueryAsync();
        }

        var releaser = new OperatorConversationReleaser(fixture.DataSource, new SystemClock(), new UuidV7Generator());

        Assert.Equal(3, await releaser.ReleaseAllAsync(operatorId, CancellationToken.None));

        await using var readConnection = await fixture.DataSource.OpenConnectionAsync();
        await using var readCommand = new NpgsqlCommand("SELECT active_chats FROM operators WHERE id = @id", readConnection);
        readCommand.Parameters.AddWithValue("id", operatorId.Value);
        Assert.Equal(0, (int)(await readCommand.ExecuteScalarAsync())!);

        await using var verify = fixture.CreateDbContext();
        foreach (var conversationId in conversationIds)
        {
            var interval = await verify.ConversationAssignments.SingleAsync(i => i.ConversationId == conversationId);
            Assert.NotNull(interval.EndedAt);
        }
    }

    [Fact]
    public async Task ReleaseAllAsync_WhenOperatorHasNoAssignedConversations_ReturnsZero_AndDoesNothing()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            await db.SaveChangesAsync();
        }

        var releaser = new OperatorConversationReleaser(fixture.DataSource, new SystemClock(), new UuidV7Generator());
        var released = await releaser.ReleaseAllAsync(operatorId, CancellationToken.None);

        Assert.Equal(0, released);
    }
}
