using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>adr/0005, 2-02: sending a message and staging its outbox row happen through the exact
/// same DbContext instance the real handler and real repository would share within one DI scope -
/// proving "same transaction" end to end, not just at the platform-generic level 2-01 already
/// covered.</summary>
[Collection(PostgresCollection.Name)]
public class SendMessageOutboxTests(PostgresFixture fixture)
{
    // Real time, not a fixed date - see MessageUniqueSequenceTests: 2-06's partitioned messages
    // table only ever has partitions for the current month and the next two. Truncated to whole
    // seconds so it round-trips through Postgres's timestamptz unchanged.
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
    private static readonly IIdGenerator IdGenerator = new UuidV7Generator();

    [Fact]
    public async Task SendVisitorMessage_WritesOneMessageRowAndOneMatchingOutboxRow()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Visitors.Add(new Visitor(visitorId, siteId, Now));
            seed.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var handler = new SendVisitorMessageHandler(
                new ConversationRepository(db), new FixedClock(Now), IdGenerator, new EfOutboxWriter<AgoChatDbContext>(db));

            var result = await handler.HandleAsync(
                new SendVisitorMessage(conversationId, visitorId, "hello"), CancellationToken.None);

            Assert.True(result.IsSuccess);
        }

        await using var verify = fixture.CreateDbContext();
        var message = await verify.Set<Message>().SingleAsync(CancellationToken.None);
        var outboxRow = await verify.Set<OutboxMessage>().SingleAsync(CancellationToken.None);

        Assert.Equal(message.Id.Value, outboxRow.Id);
        Assert.Equal("MessageAccepted", outboxRow.Type);
        Assert.Equal(conversationId.Value.ToString(), outboxRow.PartitionKey);
        Assert.Null(outboxRow.PublishedAt);
    }

    [Fact]
    public async Task SendVisitorMessage_WhenTheBodyIsInvalid_WritesNeitherRow()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seed.Visitors.Add(new Visitor(visitorId, siteId, Now));
            seed.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var handler = new SendVisitorMessageHandler(
                new ConversationRepository(db), new FixedClock(Now), IdGenerator, new EfOutboxWriter<AgoChatDbContext>(db));

            var result = await handler.HandleAsync(
                new SendVisitorMessage(conversationId, visitorId, "   "), CancellationToken.None);

            Assert.True(result.IsFailure);
        }

        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.Set<Message>().AnyAsync(CancellationToken.None));
        Assert.False(await verify.Set<OutboxMessage>().AnyAsync(CancellationToken.None));
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
