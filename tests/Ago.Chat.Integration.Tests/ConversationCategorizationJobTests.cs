using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.CategorizeConversation;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `19-02`: real Postgres, the real query (<see cref="ConversationCategorizationQuery"/>), and the real
/// domain path (<see cref="CategorizeConversationHandler"/> -> <see cref="ITagRepository.AddToConversationAsync"/>)
/// - the same bar <see cref="AutoCloseInactiveConversationsJobTests"/> already sets for its own job. The
/// LLM provider itself is the one thing not real here (<see cref="RecordingCategorizer"/> stands in for
/// it) - this class's own report explains why that boundary, not this one, is where "not confirmed
/// against a real LLM" honestly lives.
///
/// <para>Covers this item's own Done-when directly, against a real database: a real seeded tag
/// vocabulary gets picked from, a zero-tag site gets nothing, and an already-tagged conversation is
/// left alone - all three proven by reading the real <c>conversation_tags</c> rows back afterward, not
/// only by inspecting an in-memory fake's state the way <c>CategorizeConversationHandlerTests</c> does.
/// </para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConversationCategorizationJobTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now =
        new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task RunOnceAsync_TagsARecentlyClosedUntaggedConversation_WithASeededSiteVocabulary_AsAiSourced()
    {
        var (siteId, conversationId) = await SeedClosedConversationAsync(closedAt: Now - TimeSpan.FromHours(1));
        var billingTagId = await SeedTagAsync(siteId, "Billing");
        await SeedTagAsync(siteId, "Shipping");

        var categorizer = new RecordingCategorizer(new CategorizationResult.Success([billingTagId]));
        await CreateJob(categorizer).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, categorizer.CallCount);
        var candidateNames = categorizer.LastRequest!.CandidateTags.Select(c => c.Name).OrderBy(n => n).ToList();
        Assert.Equal(["Billing", "Shipping"], candidateNames);

        await using var verify = fixture.CreateDbContext();
        var applied = await verify.ConversationTags.Where(t => t.ConversationId == conversationId).ToListAsync();
        var only = Assert.Single(applied);
        Assert.Equal(billingTagId, only.TagId);
        Assert.Equal(TagSource.Ai, only.Source);
    }

    /// <summary>This item's own Done-when, proven against a real database: a site with zero configured
    /// tags produces zero AI-applied tags, and the provider is never even asked.</summary>
    [Fact]
    public async Task RunOnceAsync_SkipsASiteWithNoTagVocabularyAtAll()
    {
        var (_, conversationId) = await SeedClosedConversationAsync(closedAt: Now - TimeSpan.FromHours(1));

        var categorizer = new RecordingCategorizer(new CategorizationResult.Success([]));
        await CreateJob(categorizer).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, categorizer.CallCount);
        await using var verify = fixture.CreateDbContext();
        Assert.Empty(await verify.ConversationTags.Where(t => t.ConversationId == conversationId).ToListAsync());
    }

    /// <summary>This item's own Done-when, proven against a real database: a conversation that already
    /// carries a manual tag is skipped entirely - never added to, never overwritten, and the provider is
    /// never asked about it.</summary>
    [Fact]
    public async Task RunOnceAsync_LeavesAnAlreadyTaggedConversationAlone()
    {
        var (siteId, conversationId) = await SeedClosedConversationAsync(closedAt: Now - TimeSpan.FromHours(1));
        var vipTagId = await SeedTagAsync(siteId, "VIP");
        await SeedTagAsync(siteId, "Billing");
        await new TagRepository(fixture.CreateDbContext()).AddToConversationAsync(
            conversationId, vipTagId, TagSource.Operator, CancellationToken.None);

        var categorizer = new RecordingCategorizer(new CategorizationResult.Success([]));
        await CreateJob(categorizer).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, categorizer.CallCount);
        await using var verify = fixture.CreateDbContext();
        var stillOnly = Assert.Single(await verify.ConversationTags.Where(t => t.ConversationId == conversationId).ToListAsync());
        Assert.Equal(vipTagId, stillOnly.TagId);
        Assert.Equal(TagSource.Operator, stillOnly.Source);
    }

    /// <summary>`ConversationCategorizationQuery`'s own `state = 'Closed'` predicate - an open
    /// conversation is never a candidate, regardless of how long it has existed.</summary>
    [Fact]
    public async Task RunOnceAsync_LeavesAnOpenConversationAlone()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now - TimeSpan.FromDays(1)));
            db.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now - TimeSpan.FromDays(1)));
            await db.SaveChangesAsync();
        }

        await SeedTagAsync(siteId, "Billing");

        var categorizer = new RecordingCategorizer(new CategorizationResult.Success([]));
        await CreateJob(categorizer).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, categorizer.CallCount);
    }

    /// <summary><see cref="ConversationCategorizationJobOptions.LookbackWindow"/>'s own boundary - a
    /// conversation closed before the cutoff is not a candidate, the same "ages out, never rescanned
    /// forever" shape <see cref="ConversationCategorizationQuery"/>'s own remarks describe.</summary>
    [Fact]
    public async Task RunOnceAsync_LeavesAConversationClosedBeforeTheLookbackWindowAlone()
    {
        var lookback = TimeSpan.FromHours(24);
        var (siteId, conversationId) = await SeedClosedConversationAsync(closedAt: Now - lookback - TimeSpan.FromHours(1));
        await SeedTagAsync(siteId, "Billing");

        var categorizer = new RecordingCategorizer(new CategorizationResult.Success([]));
        await CreateJob(categorizer, lookback).RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, categorizer.CallCount);
        await using var verify = fixture.CreateDbContext();
        Assert.Empty(await verify.ConversationTags.Where(t => t.ConversationId == conversationId).ToListAsync());
    }

    private ConversationCategorizationJob CreateJob(RecordingCategorizer categorizer, TimeSpan? lookbackWindow = null) => new(
        fixture.DataSource,
        new DirectScopeFactory(fixture, categorizer),
        new FixedClock(Now),
        Options.Create(new ConversationCategorizationJobOptions
        {
            LookbackWindow = lookbackWindow ?? TimeSpan.FromHours(24),
            BatchSize = 50,
        }),
        NullLogger<ConversationCategorizationJob>.Instance);

    private async Task<(SiteId SiteId, ConversationId ConversationId)> SeedClosedConversationAsync(DateTimeOffset closedAt)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var createdAt = closedAt - TimeSpan.FromMinutes(10);

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));

        var conversation = Conversation.Start(conversationId, siteId, visitorId, createdAt);
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("do you ship to Kazan?"), createdAt);
        conversation.AssignTo(operatorId, createdAt, holdsCapacityClaim: false);
        conversation.AddOperatorMessage(operatorId, new MessageId(Guid.NewGuid()), new MessageBody("yes, 3-5 days"), createdAt + TimeSpan.FromMinutes(1));
        conversation.Close(closedAt);
        conversation.ClearDomainEvents();
        db.Conversations.Add(conversation);

        await db.SaveChangesAsync();
        return (siteId, conversationId);
    }

    private async Task<TagId> SeedTagAsync(SiteId siteId, string name)
    {
        var tagId = new TagId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Tags.Add(Tag.Create(tagId, siteId, name, Now));
        await db.SaveChangesAsync();
        return tagId;
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    /// <summary>Stands in for the real LLM provider - the one thing this test class does not exercise
    /// for real (this class's own remarks). Records every call so a test can assert the provider was, or
    /// was not, reached at all - the same signal `FakeReplyDraftGenerator`/`FakeConversationCategorizer`
    /// give `Ago.Chat.Application.Tests`, reimplemented here since that test project is not referenced
    /// from this one.</summary>
    private sealed class RecordingCategorizer(CategorizationResult result) : IConversationCategorizer
    {
        public int CallCount { get; private set; }

        public CategorizationRequest? LastRequest { get; private set; }

        public Task<CategorizationResult> CategorizeAsync(CategorizationRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(result);
        }
    }

    /// <summary>The identical "resolve the scoped handler's own dependencies against real Postgres, no
    /// full ASP.NET Core DI container" shape <see cref="AutoCloseInactiveConversationsJobTests.DirectScopeFactory"/>'s
    /// own remarks describe, reused here for <see cref="CategorizeConversationHandler"/>'s own
    /// dependency graph.</summary>
    private sealed class DirectScopeFactory(PostgresFixture fixture, RecordingCategorizer categorizer) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
        {
            var db = fixture.CreateDbContext();
            var handler = new CategorizeConversationHandler(
                new ConversationReadStore(fixture.DataSource),
                new TagRepository(db),
                categorizer,
                new CategorizationOptions(),
                NullLogger<CategorizeConversationHandler>.Instance);
            return new DirectScope(db, handler);
        }

        private sealed class DirectScope(AgoChatDbContext db, CategorizeConversationHandler handler) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new SingleServiceProvider(handler);

            public void Dispose() => db.Dispose();
        }

        private sealed class SingleServiceProvider(object service) : IServiceProvider
        {
            public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
        }
    }
}
