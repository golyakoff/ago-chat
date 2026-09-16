using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.AiAddOn;
using Ago.Chat.Application.UseCases.CategorizeConversation;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
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

    // ---------------------------------------------------------------------------------------------
    // `25-04`: the AI add-on gate, proven here - against the real job, the real query and real rows -
    // rather than against CategorizeConversationHandler called directly. The item's own Done-when asks
    // for exactly that distinction, because the hazard it names is a background sweep walking backwards
    // through an archive the tenant never agreed to have sent anywhere.

    /// <summary>`25-04`'s first Done-when, against the real job: with the add-on disabled - which is
    /// every tenant until they act - nothing reaches the vendor, and the categorizer is <b>never
    /// constructed</b>. The factory below throws, so this test fails if construction happens at all,
    /// even if the constructed client were then left unused.</summary>
    [Fact]
    public async Task RunOnceAsync_WithTheAiAddOnDisabled_NeverEvenConstructsACategorizer()
    {
        var (siteId, conversationId) = await SeedClosedConversationAsync(closedAt: Now - TimeSpan.FromHours(1));
        await SeedTagAsync(siteId, "Billing");
        // Bought, but never enabled - the state a tenant is in between paying and accepting.
        await SeedModuleGrantAsync(siteId);

        await CreateJob(ThrowingCategorizerFactory, gate: RealGate()).RunOnceAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        Assert.Empty(await verify.ConversationTags.Where(t => t.ConversationId == conversationId).ToListAsync());

        await RetireCandidateAsync(conversationId);
    }

    /// <summary>`25-04`'s fourth Done-when, against the real job: a conversation that was already closed
    /// before the tenant enabled the add-on is never categorised - the cut-off read back out of a real
    /// `ai_add_on_enablements` row by the real read store, and the categorizer again never
    /// constructed.</summary>
    [Fact]
    public async Task RunOnceAsync_WithAConversationClosedBeforeTheCutOff_NeverEvenConstructsACategorizer()
    {
        var closedAt = Now - TimeSpan.FromHours(2);
        var (siteId, conversationId) = await SeedClosedConversationAsync(closedAt);
        await SeedTagAsync(siteId, "Billing");
        await SeedModuleGrantAsync(siteId);
        // Enabled after that conversation had already closed - decision 6's own "the archive is not
        // re-processed".
        await SeedEnablementAsync(siteId, enabledAt: closedAt + TimeSpan.FromMinutes(30));

        await CreateJob(ThrowingCategorizerFactory, gate: RealGate()).RunOnceAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        Assert.Empty(await verify.ConversationTags.Where(t => t.ConversationId == conversationId).ToListAsync());

        await RetireCandidateAsync(conversationId);
    }

    /// <summary>The other side of the same boundary, so the two tests above cannot both pass by the gate
    /// simply always refusing: a conversation created after the cut-off, for a tenant who bought and
    /// enabled the add-on, is categorised exactly as `19-02` always did.</summary>
    [Fact]
    public async Task RunOnceAsync_WithAConversationCreatedAfterTheCutOff_StillCategorisesIt()
    {
        var closedAt = Now - TimeSpan.FromHours(1);
        var (siteId, conversationId) = await SeedClosedConversationAsync(closedAt);
        var billingTagId = await SeedTagAsync(siteId, "Billing");
        await SeedModuleGrantAsync(siteId);
        // Well before the conversation was created (SeedClosedConversationAsync creates it ten minutes
        // before it closes).
        await SeedEnablementAsync(siteId, enabledAt: closedAt - TimeSpan.FromDays(1));

        var categorizer = new RecordingCategorizer(new CategorizationResult.Success([billingTagId]));
        await CreateJob(categorizer, gate: RealGate()).RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, categorizer.CallCount);
        await using var verify = fixture.CreateDbContext();
        var only = Assert.Single(await verify.ConversationTags.Where(t => t.ConversationId == conversationId).ToListAsync());
        Assert.Equal(billingTagId, only.TagId);
        Assert.Equal(TagSource.Ai, only.Source);
    }

    /// <summary>`25-04`: enabled and past the cut-off, but the subscription lapsed - the entitlement is
    /// re-read every call rather than captured at enable time, so transmission stops.</summary>
    [Fact]
    public async Task RunOnceAsync_WithTheAddOnEnabledButNoModuleQuantity_NeverEvenConstructsACategorizer()
    {
        var closedAt = Now - TimeSpan.FromHours(1);
        var (siteId, conversationId) = await SeedClosedConversationAsync(closedAt);
        await SeedTagAsync(siteId, "Billing");
        await SeedEnablementAsync(siteId, enabledAt: closedAt - TimeSpan.FromDays(1));
        // No SeedModuleGrantAsync at all - nothing bought, or a lapsed subscription written back to zero.

        await CreateJob(ThrowingCategorizerFactory, gate: RealGate()).RunOnceAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        Assert.Empty(await verify.ConversationTags.Where(t => t.ConversationId == conversationId).ToListAsync());

        await RetireCandidateAsync(conversationId);
    }

    /// <summary>The real gate over real Postgres - the real read store and the real grant store, not a
    /// stand-in, because "the cut-off stops the background job" is only proven when the cut-off makes the
    /// round trip through a row.</summary>
    private AiProcessingGate RealGate()
    {
        var db = fixture.CreateDbContext();
        return new AiProcessingGate(
            new AiAddOnReadStore(fixture.DataSource),
            new ModuleQuantityGrantStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(), new Ago.Platform.Hosting.SystemClock()),
            AiGateFixtures.Options);
    }

    /// <summary>A categorizer factory that throws the moment it is invoked - the whole point of
    /// `Lazy&lt;IConversationCategorizer&gt;`. A test using this fails if the client is constructed at
    /// all, which a call-count assertion on an already-built fake can never show.</summary>
    private static IConversationCategorizer ThrowingCategorizerFactory() =>
        throw new InvalidOperationException(
            "The conversation categorizer must never be constructed for a site the AI add-on gate refuses.");

    /// <summary>`ConversationCategorizationQuery` is not site-scoped - it sweeps the whole table - and
    /// this class's tests share one database. A conversation these gate tests deliberately leave
    /// *untagged* therefore stays an eligible candidate for every later test in the collection, which is
    /// exactly what made three pre-existing tests start counting three provider calls instead of zero.
    /// Ageing it out of any plausible lookback window after the assertions restores the isolation those
    /// tests were written under, without weakening what this one proved.</summary>
    private async Task RetireCandidateAsync(ConversationId conversationId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(CancellationToken.None);
        await using var command = new Npgsql.NpgsqlCommand(
            "UPDATE conversations SET closed_at = @agedOut WHERE id = @id", connection);
        command.Parameters.AddWithValue("agedOut", Now - TimeSpan.FromDays(400));
        command.Parameters.AddWithValue("id", conversationId.Value);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private async Task SeedModuleGrantAsync(SiteId siteId)
    {
        await using var db = fixture.CreateDbContext();
        var store = new ModuleQuantityGrantStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(), new Ago.Platform.Hosting.SystemClock());
        await store.GrantAsync(siteId, new ModuleKey(AiGateFixtures.ModuleKey), 1, Now, CancellationToken.None);
    }

    private async Task SeedEnablementAsync(SiteId siteId, DateTimeOffset enabledAt)
    {
        await using var db = fixture.CreateDbContext();
        var enablement = AiAddOnEnablement.ForSite(siteId);
        enablement.Enable(new OperatorId(Guid.NewGuid()), "ai-processing-addendum", "v1", enabledAt);
        await new AiAddOnEnablementRepository(db).SaveAsync(enablement, CancellationToken.None);
    }

    private ConversationCategorizationJob CreateJob(
        RecordingCategorizer categorizer, TimeSpan? lookbackWindow = null, AiProcessingGate? gate = null) =>
        CreateJob(() => categorizer, lookbackWindow, gate);

    /// <summary>`25-04`: the factory overload - a test that must prove the categorizer is never
    /// *constructed* passes a factory that throws, which no already-instantiated fake could express.</summary>
    private ConversationCategorizationJob CreateJob(
        Func<IConversationCategorizer> categorizerFactory, TimeSpan? lookbackWindow = null, AiProcessingGate? gate = null) => new(
        fixture.DataSource,
        new DirectScopeFactory(fixture, categorizerFactory, gate ?? AiGateFixtures.Allowing()),
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
    private sealed class DirectScopeFactory(
        PostgresFixture fixture, Func<IConversationCategorizer> categorizerFactory, AiProcessingGate gate) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
        {
            var db = fixture.CreateDbContext();
            var handler = new CategorizeConversationHandler(
                new ConversationReadStore(fixture.DataSource),
                new TagRepository(db),
                // `25-04`: lazily, so a refused candidate provably never constructs a categorizer at all -
                // `categorizerFactory` throws in the tests that assert exactly that.
                new Lazy<IConversationCategorizer>(categorizerFactory),
                gate,
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
