using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
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
                // `25-221`: a brand-new conversation starts Pending, not Waiting - graduate it with the
                // visitor's own real first message before AssignTo, which still only accepts Waiting.
                conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
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
            closed.AddVisitorMessage(closedVisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
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
                // `25-221`: a brand-new conversation starts Pending, not Waiting - graduate it with the
                // visitor's own real first message before AssignTo, which still only accepts Waiting.
                conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
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

    /// <summary>
    /// `26-238`: the runaway that accrued ~1100 `conversation_assignments` intervals for 9 conversations
    /// on the demo stand, reproduced against the real release path and the real assignment engine.
    ///
    /// <para>The loop needs no user action: an operator left `Online` in `operators.status` but with no
    /// live connection (what an ungraceful `Ago.Chat.Api` shutdown leaves behind, since
    /// `OperatorHub.OnDisconnectedAsync`'s `GoOffline` never ran) is a phantom the two subsystems
    /// disagree about - the engine (`SkipLockedAssignmentClaimer`, filtering `Status == Online`) re-hands
    /// the released conversation straight back, and the sweep/grace release it again a grace period later,
    /// each cycle writing a fresh interval. This test performs one release and then runs the real engine:
    /// before the fix the operator stayed `Online` and the engine re-grabbed the conversation, writing a
    /// second interval; the fix takes them `Offline` in the release transaction, so the engine finds no
    /// candidate and the churn stops at the one original, now-closed interval.</para>
    /// </summary>
    [Fact]
    public async Task ReleaseAllAsync_TakesAPhantomOnlineOperatorOffline_SoTheEngineStopsReclaimingTheSameConversation()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = await SeedOnlineSeatedOperatorHoldingOneConversationAsync(siteId, operatorId);

        var releaser = new OperatorConversationReleaser(fixture.DataSource, new SystemClock(), new UuidV7Generator());
        Assert.Equal(1, await releaser.ReleaseAllAsync(operatorId, CancellationToken.None));

        // The fix: the released operator is now Offline in the same transaction, not left Online.
        await using (var afterRelease = fixture.CreateDbContext())
        {
            var status = await afterRelease.Operators.AsNoTracking()
                .Where(o => o.Id == operatorId).Select(o => o.Status).SingleAsync();
            Assert.Equal(OperatorStatus.Offline, status);
        }

        // The real assignment engine, unchanged: with the operator now Offline it is not a candidate, so
        // the conversation stays Waiting and - crucially - no second interval is written. Before the fix
        // this claimed 1 and inserted a fresh interval, the exact per-cycle churn 26-238 describes.
        var claimer = new SkipLockedAssignmentClaimer(fixture.DataSource, new SystemClock(), new UuidV7Generator());
        Assert.Equal(0, await claimer.AssignWaitingConversationsAsync(siteId, batchSize: 10, CancellationToken.None));

        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations.AsNoTracking().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(ConversationState.Waiting, conversation.State);
        Assert.Null(conversation.OperatorId);

        var intervalCount = await verify.ConversationAssignments.CountAsync(i => i.ConversationId == conversationId);
        Assert.Equal(1, intervalCount);
        var openIntervals = await verify.ConversationAssignments
            .CountAsync(i => i.ConversationId == conversationId && i.EndedAt == null);
        Assert.Equal(0, openIntervals);
    }

    /// <summary>
    /// `26-238`: the other side of the fix - stopping the phantom-loop churn must not suppress a
    /// legitimate re-hold. An operator who genuinely comes back (a real reconnect flips them `Online`
    /// again via `Operator.NoteConnected`/`OperatorHub.OnConnectedAsync`, modelled here by the same
    /// `GoOnline` domain transition that path performs) and is then assigned the still-Waiting
    /// conversation by the real engine must record a real, fresh interval - the closed original plus a
    /// new open one, exactly two.
    /// </summary>
    [Fact]
    public async Task ReleaseAllAsync_DoesNotSuppressALaterGenuineReassignment_WhichStillWritesANewInterval()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = await SeedOnlineSeatedOperatorHoldingOneConversationAsync(siteId, operatorId);

        var releaser = new OperatorConversationReleaser(fixture.DataSource, new SystemClock(), new UuidV7Generator());
        Assert.Equal(1, await releaser.ReleaseAllAsync(operatorId, CancellationToken.None));

        // A genuine return: the operator reconnects, which flips them back Online. This is the real
        // release-then-later-reassign case 26-238 must preserve, not the phantom loop it must break.
        await using (var reconnect = fixture.CreateDbContext())
        {
            var operatorEntity = await reconnect.Operators.SingleAsync(o => o.Id == operatorId);
            operatorEntity.GoOnline();
            await reconnect.SaveChangesAsync();
        }

        var claimer = new SkipLockedAssignmentClaimer(fixture.DataSource, new SystemClock(), new UuidV7Generator());
        Assert.Equal(1, await claimer.AssignWaitingConversationsAsync(siteId, batchSize: 10, CancellationToken.None));

        await using var verify = fixture.CreateDbContext();
        var conversation = await verify.Conversations.AsNoTracking().SingleAsync(c => c.Id == conversationId);
        Assert.Equal(ConversationState.Assigned, conversation.State);
        Assert.Equal(operatorId, conversation.OperatorId);

        // A real hold resumed, so a real interval is recorded: the closed original plus a fresh open one.
        var intervalCount = await verify.ConversationAssignments.CountAsync(i => i.ConversationId == conversationId);
        Assert.Equal(2, intervalCount);
        var open = await verify.ConversationAssignments
            .SingleAsync(i => i.ConversationId == conversationId && i.EndedAt == null);
        Assert.Equal(operatorId, open.OperatorId);
    }

    /// <summary>Seeds a site with one `Online`, seated (`25-170`: a real Operator-role seat, which the
    /// engine now requires regardless of status) operator holding exactly one `Assigned` conversation
    /// with an open interval, and `active_chats = 1` to match. Returns the conversation id.</summary>
    private async Task<ConversationId> SeedOnlineSeatedOperatorHoldingOneConversationAsync(
        SiteId siteId, OperatorId operatorId)
    {
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            var operatorRoleId = Guid.NewGuid();
            db.Roles.Add(new RoleRecord { Id = operatorRoleId, SiteId = siteId, Name = "Operator", Permissions = [] });
            db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = operatorRoleId });
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
            // `25-221`: graduate Pending -> Waiting with the visitor's own first message before AssignTo.
            conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
            conversation.AssignTo(operatorId, Now, holdsCapacityClaim: true);
            db.Conversations.Add(conversation);
            db.ConversationAssignments.Add(ConversationAssignmentInterval.Open(
                new ConversationAssignmentId(Guid.NewGuid()), siteId, conversationId, operatorId,
                ConversationAssignmentSource.Assigned, Now));
            await db.SaveChangesAsync();
        }

        // active_chats is a shadow property (4-01) - seed it directly to match the AssignTo above.
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("UPDATE operators SET active_chats = 1 WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", operatorId.Value);
        await command.ExecuteNonQueryAsync();

        return conversationId;
    }
}
