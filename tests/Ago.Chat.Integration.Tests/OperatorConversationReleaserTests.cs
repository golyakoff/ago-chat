using Ago.Chat.Domain;
using Ago.Chat.Worker;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
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
                conversation.AssignTo(operatorId, Now);
                db.Conversations.Add(conversation);
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
        }

        await using var readConnection = await fixture.DataSource.OpenConnectionAsync();
        await using var readCommand = new NpgsqlCommand("SELECT active_chats FROM operators WHERE id = @id", readConnection);
        readCommand.Parameters.AddWithValue("id", operatorId.Value);
        Assert.Equal(0, (int)(await readCommand.ExecuteScalarAsync())!);
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
