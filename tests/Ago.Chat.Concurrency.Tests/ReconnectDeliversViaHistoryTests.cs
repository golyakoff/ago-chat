using Ago.Chat.Application.UseCases.GetConversationHistory;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>
/// `3-03`'s "does not need the fan-out path to still arrive eventually" case: messages sent while the
/// visitor has no live connection anywhere are not lost - they are still there, in full, the next
/// time that visitor reads the delta. No <c>OutboxDispatcher</c>, fan-out consumer, or connection
/// registry runs anywhere in this test, on purpose - that absence *is* the point being proven, not an
/// omission (the registry's own expiry behaviour is `HubConnectionRegistrationTests`' job, already
/// proven at `3-01`).
/// </summary>
[Collection(ConcurrencyCollection.Name)]
public sealed class ReconnectDeliversViaHistoryTests(ConcurrencyTestFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MessagesSentWithNoLiveRecipientAnywhere_AreStillReturnedInFull_OnTheNextDeltaRead()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var roleId = Guid.NewGuid();
        var conversationId = new ConversationId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            db.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Operator", Permissions = [Permission.ConversationSend.Value] });
            db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
            var conversation = Conversation.Start(conversationId, siteId, visitorId, Now);
            conversation.AssignTo(operatorId, Now);
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var sendMessage = new SendOperatorMessageHandler(
                new ConversationRepository(db), new PermissionChecker(db), new SystemClock(), new UuidV7Generator(),
                new EfOutboxWriter<AgoChatDbContext>(db));
            for (var i = 1; i <= 3; i++)
            {
                var sent = await sendMessage.HandleAsync(
                    new SendOperatorMessage(conversationId, operatorId, siteId, $"while you were away {i}"), CancellationToken.None);
                Assert.True(sent.IsSuccess);
            }
        }

        await using var readDb = fixture.CreateDbContext();
        var getHistory = new GetConversationHistoryHandler(
            new ConversationRepository(readDb), new ConversationReadStore(fixture.DataSource), new PermissionChecker(readDb));

        var delta = await getHistory.HandleDeltaAsVisitorAsync(
            new GetConversationDeltaAsVisitor(conversationId, visitorId, AfterSequence: 0), CancellationToken.None);

        Assert.True(delta.IsSuccess);
        Assert.Equal([1, 2, 3], delta.Value.Select(m => m.Sequence));
        Assert.Equal(
            ["while you were away 1", "while you were away 2", "while you were away 3"],
            delta.Value.Select(m => m.Body));
    }
}
