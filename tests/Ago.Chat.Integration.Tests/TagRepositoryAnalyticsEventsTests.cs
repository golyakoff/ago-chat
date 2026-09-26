using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `adr/0186` S1: rule 4 - "a state change and its integration event are committed in one
/// transaction" - proved against a real Postgres for a write with no aggregate of its own, the same bar
/// <see cref="ModuleQuantityGrantedOutboxTests"/> already sets for its own aggregate-less write. Unlike
/// that store's EF-tracked <c>SaveChangesAsync</c>, <see cref="TagRepository.AddToConversationAsync"/>/
/// <see cref="TagRepository.RemoveFromConversationAsync"/> issue raw SQL that commits on its own -
/// <see cref="TagRepository"/>'s own remarks explain why that needed an explicit
/// <c>BeginTransactionAsync</c> to keep the guarantee, which this file is what actually exercises it
/// against a real database rather than the in-memory <c>FakeTagRepository</c>
/// (<c>TagConversationHandlerTests</c>'s own remarks on the split).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TagRepositoryAnalyticsEventsTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now =
        new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task AddToConversationAsync_WhenTheTagIsNewToTheConversation_StagesOneConversationTaggedRow()
    {
        var (siteId, conversationId, tagId) = await SeedSiteConversationAndTagAsync();

        await using (var db = fixture.CreateDbContext())
        {
            var tags = new TagRepository(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            await tags.AddToConversationAsync(conversationId, siteId, tagId, TagSource.Operator, Now, CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        Assert.NotNull(await verify.ConversationTags.SingleOrDefaultAsync(
            t => t.ConversationId == conversationId && t.TagId == tagId));

        var outboxRow = await verify.Set<OutboxMessage>().SingleAsync(
            o => o.Type == nameof(ConversationTagged) && o.PartitionKey == conversationId.Value.ToString());
        var contract = System.Text.Json.JsonSerializer.Deserialize<ConversationTagged>(outboxRow.Payload)!;
        Assert.Equal(conversationId.Value, contract.ConversationId);
        Assert.Equal(siteId.Value, contract.SiteId);
        Assert.Equal(tagId.Value, contract.TagId);
        Assert.Null(outboxRow.PublishedAt);
    }

    /// <summary>The fails-before this slice exists to fix: `AddToConversationAsync`'s own contract is
    /// idempotent (`ON CONFLICT DO NOTHING`) - a second identical call must change nothing and, now,
    /// must not fabricate a second analytics fact for a conversation that was already tagged.</summary>
    [Fact]
    public async Task AddToConversationAsync_WhenTheConversationIsAlreadyTagged_StagesNothingASecondTime()
    {
        var (siteId, conversationId, tagId) = await SeedSiteConversationAndTagAsync();

        await using (var db = fixture.CreateDbContext())
        {
            var tags = new TagRepository(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            await tags.AddToConversationAsync(conversationId, siteId, tagId, TagSource.Operator, Now, CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var tags = new TagRepository(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            await tags.AddToConversationAsync(conversationId, siteId, tagId, TagSource.Operator, Now.AddSeconds(1), CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var outboxRows = await verify.Set<OutboxMessage>()
            .Where(o => o.Type == nameof(ConversationTagged) && o.PartitionKey == conversationId.Value.ToString())
            .ToListAsync();
        Assert.Single(outboxRows);
    }

    [Fact]
    public async Task RemoveFromConversationAsync_WhenTheTagWasApplied_StagesOneConversationUntaggedRow()
    {
        var (siteId, conversationId, tagId) = await SeedSiteConversationAndTagAsync();

        await using (var db = fixture.CreateDbContext())
        {
            var tags = new TagRepository(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            await tags.AddToConversationAsync(conversationId, siteId, tagId, TagSource.Operator, Now, CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var tags = new TagRepository(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            await tags.RemoveFromConversationAsync(conversationId, siteId, tagId, Now.AddSeconds(1), CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        Assert.Null(await verify.ConversationTags.SingleOrDefaultAsync(
            t => t.ConversationId == conversationId && t.TagId == tagId));

        var outboxRow = await verify.Set<OutboxMessage>().SingleAsync(
            o => o.Type == nameof(ConversationUntagged) && o.PartitionKey == conversationId.Value.ToString());
        var contract = System.Text.Json.JsonSerializer.Deserialize<ConversationUntagged>(outboxRow.Payload)!;
        Assert.Equal(conversationId.Value, contract.ConversationId);
        Assert.Equal(siteId.Value, contract.SiteId);
        Assert.Equal(tagId.Value, contract.TagId);
    }

    /// <summary>The mirror no-op case - removing a tag that was never applied must not fabricate an
    /// untagged fact for something that never happened.</summary>
    [Fact]
    public async Task RemoveFromConversationAsync_WhenTheTagWasNeverApplied_StagesNothing()
    {
        var (siteId, conversationId, tagId) = await SeedSiteConversationAndTagAsync();

        await using (var db = fixture.CreateDbContext())
        {
            var tags = new TagRepository(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            await tags.RemoveFromConversationAsync(conversationId, siteId, tagId, Now, CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        Assert.Empty(await verify.Set<OutboxMessage>()
            .Where(o => o.Type == nameof(ConversationUntagged) && o.PartitionKey == conversationId.Value.ToString())
            .ToListAsync());
    }

    private async Task<(SiteId SiteId, ConversationId ConversationId, TagId TagId)> SeedSiteConversationAndTagAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            await db.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
            await db.SaveChangesAsync();
        }

        var tag = Tag.Create(new TagId(Guid.NewGuid()), siteId, "vip", Now);
        await using (var db = fixture.CreateDbContext())
        {
            await new TagRepository(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator()).SaveAsync(tag, CancellationToken.None);
        }

        return (siteId, conversationId, tag.Id);
    }
}
