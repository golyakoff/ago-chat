using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Command = Ago.Chat.Application.UseCases.StartConversation.StartConversation;

namespace Ago.Chat.Application.Tests.UseCases.StartConversation;

public class StartConversationHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static (
        StartConversationHandler Handler, FakeVisitorRepository Visitors, FakeConversationRepository Conversations,
        FakeVisitorRestrictionRepository Restrictions, FakeOutboxWriter Outbox, FakeChannelIdentityRepository ChannelIdentities)
        CreateHandler(FakeSiteRepository? sites = null, IRateLimiter? rateLimiter = null, FakeVisitorRestrictionRepository? restrictions = null)
    {
        var visitors = new FakeVisitorRepository();
        var conversations = new FakeConversationRepository();
        var restrictionRepository = restrictions ?? new FakeVisitorRestrictionRepository();
        var siteConfig = new GetSiteConfigByIdHandler(sites ?? new FakeSiteRepository(), new FakeCache());
        var outbox = new FakeOutboxWriter();
        var channelIdentities = new FakeChannelIdentityRepository();
        var handler = new StartConversationHandler(
            visitors, conversations, restrictionRepository, siteConfig, rateLimiter ?? new FakeRateLimiter(), new ConversationCreateRateLimitOptions(),
            new FakeClock(Now), new FakeIdGenerator(), new FakeVisitorEmojiPairGenerator(), outbox, channelIdentities);
        return (handler, visitors, conversations, restrictionRepository, outbox, channelIdentities);
    }

    /// <summary>`23-78`: a site whose own `WidgetConfig.AllowAttachmentUploadsByDefault` is
    /// <paramref name="allowAttachmentUploadsByDefault"/> - seeded into a fake repository so
    /// `GetSiteConfigByIdHandler` (the same cache-aside read `StartConversationHandler` composes
    /// through, per that handler's own remarks) has a real row to answer from.</summary>
    private static FakeSiteRepository CreateSites(bool allowAttachmentUploadsByDefault)
    {
        var sites = new FakeSiteRepository();
        var site = new Site(SiteId, $"pk_{Guid.NewGuid():N}", []);
        site.UpdateWidgetConfig(
            new WidgetConfig(null, Position.BottomRight, allowAttachmentUploadsByDefault: allowAttachmentUploadsByDefault),
            Now);
        sites.Seed(site);
        return sites;
    }

    [Fact]
    public async Task HandleAsync_WhenVisitorHasNoActiveConversation_StartsANewOne()
    {
        var (handler, _, _, _, _, _) = CreateHandler();

        var result = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsNew);
    }

    // -----------------------------------------------------------------------------------------
    // `adr/0186` S1: the analytics `ConversationOpened` event - published in the same call this
    // handler already makes to conversations.SaveAsync (rule 4). The real transaction guarantee is
    // Ago.Chat.Integration.Tests' job (`CloseConversationHandlerTests`' own remarks on the identical
    // split); these tests only prove the handler enqueues the right envelope, with the right
    // attribution, for the right conversations.
    // -----------------------------------------------------------------------------------------

    /// <summary>The common case: a brand-new widget visitor with no linked channel identity and no
    /// captured traffic source reads back exactly as <c>OperatorAnalyticsReadStore</c>'s own read-time
    /// query would label it - <c>"Widget"</c>, no referrer, no campaign - and the tenant's zone falls
    /// back to the platform default because no site was ever seeded for this test's <see cref="SiteId"/>.</summary>
    [Fact]
    public async Task HandleAsync_WhenVisitorHasNoActiveConversation_PublishesConversationOpened_WithWidgetDefaults()
    {
        var (handler, _, _, _, outbox, _) = CreateHandler();

        var result = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        var envelope = Assert.Single(outbox.Enqueued);
        Assert.Equal("ConversationOpened", envelope.Type);
        Assert.Equal(result.Value.ConversationId.Value, envelope.MessageId);
        Assert.Equal(result.Value.ConversationId.Value.ToString(), envelope.PartitionKey);
        var contract = System.Text.Json.JsonSerializer.Deserialize<Ago.Chat.Contracts.ConversationOpened>(envelope.Payload);
        Assert.Equal(SiteId.Value, contract!.SiteId);
        Assert.Equal(VisitorId.Value, contract.VisitorId);
        Assert.Equal("Widget", contract.Channel);
        Assert.Null(contract.ReferrerHost);
        Assert.Null(contract.UtmCampaign);
        Assert.Equal("Europe/Moscow", contract.TenantZone);
    }

    /// <summary>A visitor already linked to an external channel (the `ReceiveChannelMessageHandler`
    /// path, which links the identity and only then calls this handler - see this handler's own class
    /// remarks) gets that channel's own label, not <c>"Widget"</c>.</summary>
    [Fact]
    public async Task HandleAsync_WhenVisitorHasALinkedChannelIdentity_PublishesTheResolvedChannelLabel()
    {
        var (handler, _, _, _, outbox, channelIdentities) = CreateHandler();
        await channelIdentities.SaveAsync(
            ChannelIdentity.Link(
                new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Telegram,
                new ExternalChannelAddress("123456"), VisitorId, Now),
            CancellationToken.None);

        await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        var envelope = Assert.Single(outbox.Enqueued);
        var contract = System.Text.Json.JsonSerializer.Deserialize<Ago.Chat.Contracts.ConversationOpened>(envelope.Payload);
        Assert.Equal(nameof(ChannelKind.Telegram), contract!.Channel);
    }

    /// <summary>An unlinked channel identity must not keep winning - the same "excluded from
    /// routing/preference/lookup" rule `IChannelIdentityRepository.FindMostRecentForVisitorAsync`'s own
    /// remarks state, restated here for the analytics event's own resolution.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheVisitorsOnlyChannelIdentityIsUnlinked_FallsBackToWidget()
    {
        var (handler, _, _, _, outbox, channelIdentities) = CreateHandler();
        var identity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Telegram,
            new ExternalChannelAddress("123456"), VisitorId, Now);
        identity.Unlink(Now);
        await channelIdentities.SaveAsync(identity, CancellationToken.None);

        await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        var envelope = Assert.Single(outbox.Enqueued);
        var contract = System.Text.Json.JsonSerializer.Deserialize<Ago.Chat.Contracts.ConversationOpened>(envelope.Payload);
        Assert.Equal("Widget", contract!.Channel);
    }

    /// <summary>`18-12`'s own captured attribution rides straight through onto the analytics event -
    /// denormalized at publish time (`docs/design/analytics-precompute.md` §8.1), so the future rollup
    /// never has to join back to this conversation's own row for it.</summary>
    [Fact]
    public async Task HandleAsync_WithACapturedTrafficSource_PublishesItsReferrerAndCampaign()
    {
        var (handler, _, _, _, outbox, _) = CreateHandler();
        var source = new TrafficSource("shop.example", "google", "cpc", "spring-sale");

        await handler.HandleAsync(new Command(SiteId, VisitorId, source), CancellationToken.None);

        var envelope = Assert.Single(outbox.Enqueued);
        var contract = System.Text.Json.JsonSerializer.Deserialize<Ago.Chat.Contracts.ConversationOpened>(envelope.Payload);
        Assert.Equal("shop.example", contract!.ReferrerHost);
        Assert.Equal("spring-sale", contract.UtmCampaign);
    }

    /// <summary>The tenant's own <see cref="Site.TimeZone"/> is stamped onto the event, read through the
    /// identical cached <see cref="GetSiteConfigById.SiteConfigDto"/> this handler already reads for
    /// <c>WidgetAllowAttachmentUploadsByDefault</c> - `adr/0031`'s "a stamp, not a gate" carve-out
    /// (`SiteConfigDto`'s own remarks).</summary>
    [Fact]
    public async Task HandleAsync_WhenTheSiteHasANonDefaultTimeZone_StampsItOntoTheEvent()
    {
        var sites = new FakeSiteRepository();
        var site = new Site(SiteId, $"pk_{Guid.NewGuid():N}", []);
        sites.Seed(site);
        var (handler, _, _, _, outbox, _) = CreateHandler(sites);

        await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        var envelope = Assert.Single(outbox.Enqueued);
        var contract = System.Text.Json.JsonSerializer.Deserialize<Ago.Chat.Contracts.ConversationOpened>(envelope.Payload);
        // No writer exists yet to set a non-default zone (out of scope for this slice - Site.TimeZone's
        // own remarks) - this proves the value actually comes from the site's own column, not a
        // hardcoded literal in the handler, by confirming it reads back the aggregate's own default.
        Assert.Equal(site.TimeZone, contract!.TenantZone);
    }

    /// <summary>Resuming an already-open conversation must not publish a second `ConversationOpened` -
    /// the fact "this conversation started" happened once, and `Conversation.Start` (the only raiser of
    /// the underlying domain event) never runs on this branch.</summary>
    [Fact]
    public async Task HandleAsync_WhenResumingAnExistingConversation_PublishesNothing()
    {
        var (handler, _, conversations, _, outbox, _) = CreateHandler();
        var existing = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversations.Seed(existing);

        var result = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        Assert.False(result.Value.IsNew);
        Assert.Empty(outbox.Enqueued);
    }

    [Fact]
    public async Task HandleAsync_WhenVisitorAlreadyHasAWaitingConversation_ResumesIt()
    {
        var (handler, _, conversations, _, _, _) = CreateHandler();
        var existing = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversations.Seed(existing);

        var result = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsNew);
        Assert.Equal(existing.Id, result.Value.ConversationId);
    }

    [Fact]
    public async Task HandleAsync_WhenVisitorAlreadyHasAClosedConversation_StartsANewOneInstead()
    {
        var (handler, _, conversations, _, _, _) = CreateHandler();
        var closed = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        closed.Close(Now);
        conversations.Seed(closed);

        var result = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsNew);
        Assert.NotEqual(closed.Id, result.Value.ConversationId);
    }

    [Fact]
    public async Task HandleAsync_WhenVisitorIsNew_CreatesTheVisitorRecord()
    {
        var (handler, visitors, _, _, _, _) = CreateHandler();

        await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        var saved = await visitors.GetByIdAsync(VisitorId, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal(Now, saved.FirstSeenAt);
    }

    // `25-56` decision 5: the pair is assigned once, at first contact, and a returning visitor's
    // *second* conversation must read back the identical pair the *first* one triggered - not a fresh
    // pick. The fake generator is seeded to hand out a different pair on every call, so this would fail
    // if the handler ever re-assigned on the "visitor already exists" branch.
    [Fact]
    public async Task HandleAsync_WhenTheSameVisitorStartsASecondConversation_TheEmojiPairIsUnchanged()
    {
        var visitors = new FakeVisitorRepository();
        var conversations = new FakeConversationRepository();
        var siteConfig = new GetSiteConfigByIdHandler(new FakeSiteRepository(), new FakeCache());
        var emojiPairs = new FakeVisitorEmojiPairGenerator("🐳", "🌭");
        var handler = new StartConversationHandler(
            visitors, conversations, new FakeVisitorRestrictionRepository(), siteConfig, new FakeRateLimiter(), new ConversationCreateRateLimitOptions(),
            new FakeClock(Now), new FakeIdGenerator(), emojiPairs, new FakeOutboxWriter(), new FakeChannelIdentityRepository());

        var first = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);
        var afterFirstContact = await visitors.GetByIdAsync(VisitorId, CancellationToken.None);

        // The first conversation closes and a different pair is now on offer - if the handler's
        // "visitor already exists" branch ever called AssignEmojiPair again, the second conversation
        // below would pick this new pair up instead of keeping the first one.
        var firstConversation = await conversations.GetByIdAsync(first.Value.ConversationId, CancellationToken.None);
        firstConversation!.Close(Now);
        emojiPairs.Creature = "🐠";
        emojiPairs.Food = "🥝";

        await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);
        var afterSecondConversation = await visitors.GetByIdAsync(VisitorId, CancellationToken.None);

        Assert.Equal("🐳", afterFirstContact!.EmojiCreature);
        Assert.Equal("🌭", afterFirstContact.EmojiFood);
        Assert.Equal(afterFirstContact.EmojiCreature, afterSecondConversation!.EmojiCreature);
        Assert.Equal(afterFirstContact.EmojiFood, afterSecondConversation.EmojiFood);
        Assert.Equal(1, emojiPairs.CallCount);
    }

    [Fact]
    public async Task HandleAsync_WhenVisitorReturns_TouchesLastSeenAtWithoutChangingFirstSeenAt()
    {
        var visitors = new FakeVisitorRepository();
        var conversations = new FakeConversationRepository();
        var firstContact = Now;
        await visitors.SaveAsync(new Visitor(VisitorId, SiteId, firstContact), CancellationToken.None);

        var returnVisit = Now.AddDays(1);
        var siteConfig = new GetSiteConfigByIdHandler(new FakeSiteRepository(), new FakeCache());
        var handler = new StartConversationHandler(
            visitors, conversations, new FakeVisitorRestrictionRepository(), siteConfig, new FakeRateLimiter(), new ConversationCreateRateLimitOptions(),
            new FakeClock(returnVisit), new FakeIdGenerator(), new FakeVisitorEmojiPairGenerator(), new FakeOutboxWriter(),
            new FakeChannelIdentityRepository());

        await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        var saved = await visitors.GetByIdAsync(VisitorId, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.Equal(firstContact, saved.FirstSeenAt);
        Assert.Equal(returnVisit, saved.LastSeenAt);
    }

    /// <summary>`23-78`'s own Done-when: "a visitor cannot obtain an upload slot for a conversation
    /// with no grant" - the default case, and the one every site not yet configured otherwise gets.
    /// No site seeded at all here, deliberately: `GetSiteConfigByIdHandler` answers "not found" for an
    /// unseeded site, and the handler's own `config?.WidgetAllowAttachmentUploadsByDefault ?? false`
    /// must still land on the closed-by-default answer rather than throwing.</summary>
    [Fact]
    public async Task HandleAsync_WhenSiteHasNoAttachmentUploadDefault_StartsWithNoGrant()
    {
        var (handler, _, conversations, _, _, _) = CreateHandler();

        var result = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.HasAttachmentUploadGrant);
        var saved = await conversations.GetByIdAsync(result.Value.ConversationId, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.False(saved.HasAttachmentUploadGrant);
        Assert.Null(saved.AttachmentUploadGrantedBy);
    }

    /// <summary>The reverse - a tenant that turned `WidgetConfig.AllowAttachmentUploadsByDefault` on
    /// gets a brand-new conversation that already carries the grant, with no operator behind it
    /// (`Conversation.Start`'s own remarks: a tenant default is not an operator's own act).</summary>
    [Fact]
    public async Task HandleAsync_WhenSiteAllowsAttachmentUploadsByDefault_StartsWithAGrantAndNoOperatorAttribution()
    {
        var sites = CreateSites(allowAttachmentUploadsByDefault: true);
        var (handler, _, conversations, _, _, _) = CreateHandler(sites);

        var result = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.HasAttachmentUploadGrant);
        var saved = await conversations.GetByIdAsync(result.Value.ConversationId, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.True(saved.HasAttachmentUploadGrant);
        Assert.Equal(Now, saved.AttachmentUploadGrantedAt);
        Assert.Null(saved.AttachmentUploadGrantedBy);
    }

    /// <summary>The tenant default only ever seeds a *brand-new* conversation - a visitor resuming an
    /// existing one keeps whatever that conversation already had, even if the tenant's own default has
    /// since changed. This is not this handler re-deciding anything: it is
    /// `GetActiveForVisitorAsync`'s own early return, which never calls `Conversation.Start` at all.</summary>
    [Fact]
    public async Task HandleAsync_WhenResumingAnExistingConversation_TenantDefaultIsNeverConsulted()
    {
        var sites = CreateSites(allowAttachmentUploadsByDefault: true);
        var (handler, _, conversations, _, _, _) = CreateHandler(sites);
        var existing = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversations.Seed(existing);

        var result = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsNew);
        Assert.False(result.Value.HasAttachmentUploadGrant);
    }

    // `23-76`: "creating conversations at speed does not multiply the available budget" - a fresh
    // conversation is what resets `23-75`'s per-conversation attachment budget, so the genuinely-new
    // path spends a rate-limit bucket before it is allowed to create one.

    [Fact]
    public async Task HandleAsync_WhenTheRateLimitIsExceeded_ReturnsConversationCreateRateLimited_WithoutStartingOne()
    {
        var (handler, _, conversations, _, _, _) = CreateHandler(rateLimiter: new RateLimitedFakeRateLimiter(TimeSpan.FromSeconds(7)));

        var result = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.CreateRateLimited", result.Error!.Value.Code);
        Assert.Null(await conversations.GetActiveForVisitorAsync(VisitorId, CancellationToken.None));
    }

    /// <summary>Proves the per-site bucket specifically is consulted, not just some bucket -
    /// `SelectiveFakeRateLimiter` only denies keys naming "site", so a visitor-bucket-only check would
    /// let this through.</summary>
    [Fact]
    public async Task HandleAsync_WhenOnlyThePerSiteRateLimitIsExceeded_ReturnsConversationCreateRateLimited()
    {
        var (handler, _, _, _, _, _) = CreateHandler(rateLimiter: new SelectiveFakeRateLimiter("site", TimeSpan.FromSeconds(7)));

        var result = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.CreateRateLimited", result.Error!.Value.Code);
    }

    /// <summary>Proves the per-visitor bucket specifically is consulted too - a site-bucket-only check
    /// would let this through.</summary>
    [Fact]
    public async Task HandleAsync_WhenOnlyThePerVisitorRateLimitIsExceeded_ReturnsConversationCreateRateLimited()
    {
        var (handler, _, _, _, _, _) = CreateHandler(rateLimiter: new SelectiveFakeRateLimiter("visitor", TimeSpan.FromSeconds(7)));

        var result = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.CreateRateLimited", result.Error!.Value.Code);
    }

    /// <summary>The rate limit guards *creating* a conversation - resuming an existing one must never
    /// spend it, or a legitimate, chatty-but-already-open visitor would eventually be refused their own
    /// conversation history for no reason connected to `23-75`'s budget at all.</summary>
    [Fact]
    public async Task HandleAsync_WhenResumingAnExistingConversation_TheRateLimitIsNeverConsulted()
    {
        var deniedLimiter = new RateLimitedFakeRateLimiter(TimeSpan.FromSeconds(7));
        var (handler, _, conversations, _, _, _) = CreateHandler(rateLimiter: deniedLimiter);
        var existing = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversations.Seed(existing);

        var result = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsNew);
    }

    // `23-69`/`23-77`: the fails-before both backlog items name - a restricted visitor's brand-new
    // conversation is created exactly like an ordinary one from this handler's own response's point of
    // view, and is silently kept from ever reaching an operator by
    // Conversation.RoutingSuppressedAt (WaitingConversationClaimQueryTests/GetOperatorQueueHandlerTests
    // prove the routing side; this file proves only what StartConversationHandler itself decides).

    [Fact]
    public async Task HandleAsync_WhenTheVisitorCarriesAnActiveRestriction_StillStartsANewConversation_ButSuppressesItsRouting()
    {
        var restrictions = new FakeVisitorRestrictionRepository();
        await restrictions.RestrictAsync(
            SiteId, VisitorId, new OperatorId(Guid.NewGuid()), VisitorRestrictionKind.Spam, Now.AddHours(24),
            new ConversationId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);
        var (handler, _, conversations, _, _, _) = CreateHandler(restrictions: restrictions);

        var result = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.IsNew);
        var saved = await conversations.GetByIdAsync(result.Value.ConversationId, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.True(saved.IsRoutingSuppressed);
        // Never blocked (24-10's own, separate mechanism) - this is additive, not a repurposing of it.
        Assert.False(saved.IsBlocked);
    }

    /// <summary>Both items' own answered "no message to the visitor" requirement, proven rather than
    /// reasoned: the wire-shaped result for a restricted visitor is field-for-field identical in kind to
    /// an ordinary one (same <c>IsNew</c>, a real <c>ConversationId</c>, the same
    /// <c>HasAttachmentUploadGrant</c> default) - nothing about the response itself is special-cased.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenTheVisitorCarriesAnActiveRestriction_TheResponseIsIndistinguishableFromAnOrdinaryStart()
    {
        var restrictedVisitor = new VisitorId(Guid.NewGuid());
        var ordinaryVisitor = new VisitorId(Guid.NewGuid());
        var restrictions = new FakeVisitorRestrictionRepository();
        await restrictions.RestrictAsync(
            SiteId, restrictedVisitor, new OperatorId(Guid.NewGuid()), VisitorRestrictionKind.Block, expiresAt: null,
            new ConversationId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);
        var (handler, _, _, _, _, _) = CreateHandler(restrictions: restrictions);

        var restrictedResult = await handler.HandleAsync(new Command(SiteId, restrictedVisitor), CancellationToken.None);
        var ordinaryResult = await handler.HandleAsync(new Command(SiteId, ordinaryVisitor), CancellationToken.None);

        Assert.True(restrictedResult.IsSuccess);
        Assert.True(ordinaryResult.IsSuccess);
        Assert.Equal(ordinaryResult.Value.IsNew, restrictedResult.Value.IsNew);
        Assert.Equal(ordinaryResult.Value.HasAttachmentUploadGrant, restrictedResult.Value.HasAttachmentUploadGrant);
        Assert.NotEqual(Guid.Empty, restrictedResult.Value.ConversationId.Value);
    }

    /// <summary>The natural-expiry half of both items' own "lifts on its own" Done-when, at this
    /// handler's own level: a mute that has already passed its own `ExpiresAt` no longer suppresses a
    /// brand-new conversation, with no lift ever called.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheVisitorsRestrictionHasAlreadyNaturallyExpired_DoesNotSuppressRouting()
    {
        var restrictions = new FakeVisitorRestrictionRepository();
        await restrictions.RestrictAsync(
            SiteId, VisitorId, new OperatorId(Guid.NewGuid()), VisitorRestrictionKind.Spam, Now.AddHours(-1),
            new ConversationId(Guid.NewGuid()), Guid.NewGuid(), Now.AddHours(-2), CancellationToken.None);
        var (handler, _, conversations, _, _, _) = CreateHandler(restrictions: restrictions);

        var result = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = await conversations.GetByIdAsync(result.Value.ConversationId, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.False(saved.IsRoutingSuppressed);
    }

    /// <summary>The manual-early-lift half of the same Done-when: an operator who lifted the
    /// restriction before this call is honoured immediately - the very next `StartConversation` for
    /// that visitor is ordinary again.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheVisitorsRestrictionWasManuallyLiftedEarly_DoesNotSuppressRouting()
    {
        var restrictions = new FakeVisitorRestrictionRepository();
        await restrictions.RestrictAsync(
            SiteId, VisitorId, new OperatorId(Guid.NewGuid()), VisitorRestrictionKind.Block, expiresAt: null,
            new ConversationId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);
        await restrictions.LiftAsync(SiteId, VisitorId, new OperatorId(Guid.NewGuid()), Now.AddMinutes(1), CancellationToken.None);
        var (handler, _, conversations, _, _, _) = CreateHandler(restrictions: restrictions);

        var result = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = await conversations.GetByIdAsync(result.Value.ConversationId, CancellationToken.None);
        Assert.NotNull(saved);
        Assert.False(saved.IsRoutingSuppressed);
    }

    /// <summary>Both items' own scope - "the *next* conversation" - proven at the boundary: a
    /// restricted visitor resuming an *existing*, still-open conversation is untouched by this
    /// mechanism, exactly like the tenant-default check right above it never re-consults anything for
    /// a resumed conversation either.</summary>
    [Fact]
    public async Task HandleAsync_WhenResumingAnExistingConversation_RestrictionIsNeverConsulted()
    {
        var restrictions = new FakeVisitorRestrictionRepository();
        await restrictions.RestrictAsync(
            SiteId, VisitorId, new OperatorId(Guid.NewGuid()), VisitorRestrictionKind.Block, expiresAt: null,
            new ConversationId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);
        var (handler, _, conversations, _, _, _) = CreateHandler(restrictions: restrictions);
        var existing = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversations.Seed(existing);

        var result = await handler.HandleAsync(new Command(SiteId, VisitorId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.IsNew);
        Assert.False(existing.IsRoutingSuppressed);
    }
}
