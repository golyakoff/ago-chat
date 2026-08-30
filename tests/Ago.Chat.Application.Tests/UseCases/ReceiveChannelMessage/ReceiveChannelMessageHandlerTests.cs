using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ReceiveChannelMessage;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ReceiveChannelMessage;

/// <summary>
/// `14-01`: the end-to-end mapping proof the backlog item's Done-when asks for - a fake inbound
/// channel message reaching <c>SendVisitorMessage</c>'s own pipeline and being written exactly as a
/// widget message would be, with no parallel path anywhere. What each group of tests below is
/// actually defending:
/// <list type="bullet">
/// <item><b>Resolution</b> - the same external address always resolves to the same visitor and
/// conversation, which is the founding requirement AGO Inbox exists to meet.</item>
/// <item><b>Separation</b> - a channel identity is never inferred to be an existing visitor. This is
/// `adr/0055`'s identity decision, and it is here rather than only in prose because it is the kind of
/// decision a later change can reverse by accident.</item>
/// <item><b>Idempotency</b> - CLAUDE.md rule 5, at all three levels a redelivery could duplicate:
/// visitor, conversation, message.</item>
/// <item><b>Ordering</b> - CLAUDE.md rules 6 and 11: sequence comes from the write, never from
/// anything a provider said.</item>
/// </list>
/// </summary>
public class ReceiveChannelMessageHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private sealed record Harness(
        ReceiveChannelMessageHandler Handler,
        FakeChannelIdentityRepository Identities,
        FakeVisitorRepository Visitors,
        FakeConversationRepository Conversations,
        FakeApplyingMessagePipeline Pipeline,
        FakePendingChannelLinkRequestRepository PendingLinks,
        FakeClock Clock,
        FakeIdGenerator IdGenerator);

    private static Harness CreateHandler()
    {
        var identities = new FakeChannelIdentityRepository();
        var visitors = new FakeVisitorRepository();
        var conversations = new FakeConversationRepository();
        var pendingLinks = new FakePendingChannelLinkRequestRepository();
        var clock = new FakeClock(Now);
        var idGenerator = new FakeIdGenerator();
        var pipeline = new FakeApplyingMessagePipeline(conversations, clock, idGenerator);

        var handler = new ReceiveChannelMessageHandler(
            identities,
            visitors,
            pendingLinks,
            new StartConversationHandler(visitors, conversations, clock, idGenerator),
            new SendVisitorMessageHandler(
                conversations, new FakeRateLimiter(), new MessageSendRateLimitOptions(), pipeline),
            clock,
            idGenerator);

        return new Harness(handler, identities, visitors, conversations, pipeline, pendingLinks, clock, idGenerator);
    }

    private static Application.UseCases.ReceiveChannelMessage.ReceiveChannelMessage Inbound(
        SiteId siteId, ChannelKind kind = ChannelKind.Sms, string sender = "+70000000000",
        string externalMessageId = "provider-1", string body = "hello") =>
        new(siteId, kind, new ExternalChannelAddress(sender), new ExternalMessageId(externalMessageId), body);

    private static byte[] Hash(string code) => SHA256.HashData(Encoding.UTF8.GetBytes(code));

    private static PendingChannelLinkRequest SeedLivePendingLink(
        Harness harness, SiteId siteId, VisitorId visitorId, ChannelKind kind, string code,
        TimeSpan? validFor = null)
    {
        var request = PendingChannelLinkRequest.Request(
            new PendingChannelLinkRequestId(Guid.NewGuid()), siteId, visitorId, kind, Hash(code),
            requestedByOperatorId: null, harness.Clock.UtcNow, validFor ?? TimeSpan.FromMinutes(15));
        harness.PendingLinks.Stage(request);
        return request;
    }

    // -----------------------------------------------------------------------------------------
    // `14-12`/`adr/0079`: the verified-link confirmation branch
    // -----------------------------------------------------------------------------------------

    /// <summary>The item's own headline case: an inbound address with no existing identity whose body
    /// exactly matches a live pending code links to that request's own, already-existing visitor -
    /// never mints a new one.</summary>
    [Fact]
    public async Task AMessageMatchingALivePendingCode_LinksToThePendingRequestsVisitor_NotANewOne()
    {
        var site = new SiteId(Guid.NewGuid());
        var harness = CreateHandler();
        var existingVisitorId = new VisitorId(Guid.NewGuid());
        await harness.Visitors.SaveAsync(new Visitor(existingVisitorId, site, Now), CancellationToken.None);
        SeedLivePendingLink(harness, site, existingVisitorId, ChannelKind.Telegram, "482913");

        var result = await harness.Handler.HandleAsync(
            Inbound(site, ChannelKind.Telegram, sender: "tg-user-1", body: "482913"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(existingVisitorId, result.Value.VisitorId);
        Assert.False(result.Value.VisitorWasNew);
        var identity = Assert.Single(harness.Identities.All);
        Assert.Equal(existingVisitorId, identity.VisitorId);
        Assert.Equal("tg-user-1", identity.Address.Value);
    }

    [Fact]
    public async Task AMessageMatchingALivePendingCode_ConsumesTheRequest()
    {
        var site = new SiteId(Guid.NewGuid());
        var harness = CreateHandler();
        var existingVisitorId = new VisitorId(Guid.NewGuid());
        await harness.Visitors.SaveAsync(new Visitor(existingVisitorId, site, Now), CancellationToken.None);
        var pending = SeedLivePendingLink(harness, site, existingVisitorId, ChannelKind.Telegram, "482913");

        await harness.Handler.HandleAsync(
            Inbound(site, ChannelKind.Telegram, sender: "tg-user-1", body: "482913"), CancellationToken.None);

        Assert.NotNull(pending.ConsumedAt);
    }

    /// <summary>
    /// The highest-value fails-before for this item: an ordinary message whose body merely resembles a
    /// code (a wrong value) must fall through to exactly the same "no match -> mint a new visitor" path
    /// every other unrecognised message already takes - never silently swallowed, never an error.
    /// </summary>
    [Fact]
    public async Task AMessageWithAWrongCode_FallsThroughToTheOrdinaryNewVisitorPath()
    {
        var site = new SiteId(Guid.NewGuid());
        var harness = CreateHandler();
        var existingVisitorId = new VisitorId(Guid.NewGuid());
        await harness.Visitors.SaveAsync(new Visitor(existingVisitorId, site, Now), CancellationToken.None);
        SeedLivePendingLink(harness, site, existingVisitorId, ChannelKind.Telegram, "482913");

        var result = await harness.Handler.HandleAsync(
            Inbound(site, ChannelKind.Telegram, sender: "tg-user-1", body: "000000"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.VisitorWasNew);
        Assert.NotEqual(existingVisitorId, result.Value.VisitorId);
    }

    /// <summary>The second half of the same fails-before: a message matching a code's exact text after
    /// that code has expired is treated identically to a wrong code, not specially reported.</summary>
    [Fact]
    public async Task AMessageMatchingAnExpiredCode_FallsThroughToTheOrdinaryNewVisitorPath()
    {
        var site = new SiteId(Guid.NewGuid());
        var harness = CreateHandler();
        var existingVisitorId = new VisitorId(Guid.NewGuid());
        await harness.Visitors.SaveAsync(new Visitor(existingVisitorId, site, Now), CancellationToken.None);
        SeedLivePendingLink(harness, site, existingVisitorId, ChannelKind.Telegram, "482913", TimeSpan.FromMinutes(15));
        harness.Clock.UtcNow = Now.AddMinutes(16);

        var result = await harness.Handler.HandleAsync(
            Inbound(site, ChannelKind.Telegram, sender: "tg-user-1", body: "482913"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.VisitorWasNew);
        Assert.NotEqual(existingVisitorId, result.Value.VisitorId);
    }

    /// <summary>Cross-site isolation (`14-12`'s own Done-when): a pending code for one site must never
    /// match an inbound message on another site, even with the identical code value by coincidence.</summary>
    [Fact]
    public async Task AMessageMatchingACodeMintedForADifferentSite_FallsThroughToTheOrdinaryNewVisitorPath()
    {
        var siteA = new SiteId(Guid.NewGuid());
        var siteB = new SiteId(Guid.NewGuid());
        var harness = CreateHandler();
        var visitorOnSiteA = new VisitorId(Guid.NewGuid());
        await harness.Visitors.SaveAsync(new Visitor(visitorOnSiteA, siteA, Now), CancellationToken.None);
        SeedLivePendingLink(harness, siteA, visitorOnSiteA, ChannelKind.Telegram, "482913");

        var result = await harness.Handler.HandleAsync(
            Inbound(siteB, ChannelKind.Telegram, sender: "tg-user-1", body: "482913"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.VisitorWasNew);
        Assert.NotEqual(visitorOnSiteA, result.Value.VisitorId);
    }

    /// <summary>The same isolation, for channel kind rather than site - a code minted for a Telegram
    /// link must never confirm over a different channel.</summary>
    [Fact]
    public async Task AMessageMatchingACodeMintedForADifferentChannelKind_FallsThroughToTheOrdinaryNewVisitorPath()
    {
        var site = new SiteId(Guid.NewGuid());
        var harness = CreateHandler();
        var existingVisitorId = new VisitorId(Guid.NewGuid());
        await harness.Visitors.SaveAsync(new Visitor(existingVisitorId, site, Now), CancellationToken.None);
        SeedLivePendingLink(harness, site, existingVisitorId, ChannelKind.Telegram, "482913");

        var result = await harness.Handler.HandleAsync(
            Inbound(site, ChannelKind.Vk, sender: "vk-user-1", body: "482913"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.VisitorWasNew);
        Assert.NotEqual(existingVisitorId, result.Value.VisitorId);
    }

    /// <summary>
    /// The collision case `14-12`'s own backlog names: a claimed address already linked to a different
    /// visitor is refused, not merged - and, per <c>ReceiveChannelMessageHandler</c>'s own remarks,
    /// refused as a structural consequence of the confirmation branch's own precondition (this address
    /// already has an identity, so the branch never runs), never a second check bolted on. Proven here
    /// by seeding a colliding identity and asserting no mutation happens to either row.
    /// </summary>
    [Fact]
    public async Task AnAddressAlreadyLinkedToADifferentVisitor_IsUnaffectedByALiveCodeItHappensToMatch()
    {
        var site = new SiteId(Guid.NewGuid());
        var harness = CreateHandler();

        var existingOwnerVisitorId = new VisitorId(Guid.NewGuid());
        await harness.Visitors.SaveAsync(new Visitor(existingOwnerVisitorId, site, Now), CancellationToken.None);
        var collidingIdentity = ChannelIdentity.Link(
            new ChannelIdentityId(Guid.NewGuid()), site, ChannelKind.Telegram,
            new ExternalChannelAddress("tg-user-1"), existingOwnerVisitorId, Now);
        await harness.Identities.SaveAsync(collidingIdentity, CancellationToken.None);

        var pendingTargetVisitorId = new VisitorId(Guid.NewGuid());
        await harness.Visitors.SaveAsync(new Visitor(pendingTargetVisitorId, site, Now), CancellationToken.None);
        var pending = SeedLivePendingLink(harness, site, pendingTargetVisitorId, ChannelKind.Telegram, "482913");

        var result = await harness.Handler.HandleAsync(
            Inbound(site, ChannelKind.Telegram, sender: "tg-user-1", body: "482913"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        // Refused: the message is delivered as the existing owner's own ordinary message, never
        // re-pointed at the pending request's target visitor.
        Assert.Equal(existingOwnerVisitorId, result.Value.VisitorId);
        Assert.False(result.Value.VisitorWasNew);
        // No mutation: still exactly the one identity that existed before, still owned by the same
        // visitor, and the pending request is untouched.
        var identity = Assert.Single(harness.Identities.All);
        Assert.Equal(existingOwnerVisitorId, identity.VisitorId);
        Assert.Null(pending.ConsumedAt);
    }

    // -----------------------------------------------------------------------------------------
    // Resolution
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task FirstMessageFromAnUnknownAddress_CreatesTheVisitorIdentityConversationAndMessage()
    {
        var site = new SiteId(Guid.NewGuid());
        var harness = CreateHandler();

        var result = await harness.Handler.HandleAsync(Inbound(site), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.VisitorWasNew);
        Assert.Equal(1, result.Value.Sequence);

        var identity = Assert.Single(harness.Identities.All);
        Assert.Equal(result.Value.VisitorId, identity.VisitorId);
        Assert.Equal(ChannelKind.Sms, identity.Kind);

        var conversation = await harness.Conversations.GetByIdAsync(
            result.Value.ConversationId, CancellationToken.None);
        Assert.NotNull(conversation);
        var message = Assert.Single(conversation.Messages);
        Assert.Equal("hello", message.Body.Value);
        Assert.Equal(MessageAuthorKind.Visitor, message.AuthorKind);
    }

    /// <summary>
    /// The Done-when in one test: "a repeated message from the same external identifier resolves to
    /// the same Visitor/Conversation, not a new one each time."
    /// </summary>
    [Fact]
    public async Task SecondMessageFromTheSameAddress_ResolvesToTheSameVisitorAndConversation()
    {
        var site = new SiteId(Guid.NewGuid());
        var harness = CreateHandler();

        var first = await harness.Handler.HandleAsync(
            Inbound(site, externalMessageId: "provider-1"), CancellationToken.None);
        var second = await harness.Handler.HandleAsync(
            Inbound(site, externalMessageId: "provider-2", body: "still me"), CancellationToken.None);

        Assert.Equal(first.Value.VisitorId, second.Value.VisitorId);
        Assert.Equal(first.Value.ConversationId, second.Value.ConversationId);
        Assert.False(second.Value.VisitorWasNew);
        Assert.Single(harness.Identities.All);

        var conversation = await harness.Conversations.GetByIdAsync(
            first.Value.ConversationId, CancellationToken.None);
        Assert.Equal(2, conversation!.Messages.Count);
    }

    // -----------------------------------------------------------------------------------------
    // Separation - `adr/0055`'s identity decision, made falsifiable
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// <b>The decision this item turns on.</b> The same human reaching a shop through the widget and
    /// by SMS is two visitors, and this test is what stops that becoming untrue by accident. The
    /// widget visitor is seeded exactly as `1-06`'s token-based path creates one; the SMS message
    /// then arrives on the same site, and gets its own visitor and its own conversation. Nothing here
    /// tries to match them, because nothing in either signal proves they are the same person - and
    /// guessing wrong discloses one channel's history to whoever holds the other.
    /// </summary>
    [Fact]
    public async Task AWidgetVisitorAndAnSmsSender_AreTwoVisitors_NotOne()
    {
        var site = new SiteId(Guid.NewGuid());
        var harness = CreateHandler();

        var widgetVisitorId = new VisitorId(Guid.NewGuid());
        await harness.Visitors.SaveAsync(new Visitor(widgetVisitorId, site, Now), CancellationToken.None);
        var widgetConversation = Conversation.Start(
            new ConversationId(Guid.NewGuid()), site, widgetVisitorId, Now);
        harness.Conversations.Seed(widgetConversation);

        var result = await harness.Handler.HandleAsync(Inbound(site), CancellationToken.None);

        Assert.NotEqual(widgetVisitorId, result.Value.VisitorId);
        Assert.NotEqual(widgetConversation.Id, result.Value.ConversationId);
        Assert.Empty(widgetConversation.Messages);
    }

    /// <summary>
    /// Two channels can legitimately issue the same raw identifier string. They are two addresses,
    /// two identities and two visitors - the channel is part of the key, not a label on it.
    /// </summary>
    [Fact]
    public async Task TheSameRawIdentifierOnTwoChannels_IsTwoVisitors()
    {
        var site = new SiteId(Guid.NewGuid());
        var harness = CreateHandler();

        var bySms = await harness.Handler.HandleAsync(
            Inbound(site, ChannelKind.Sms, sender: "12345", externalMessageId: "a"), CancellationToken.None);
        var byTelegram = await harness.Handler.HandleAsync(
            Inbound(site, ChannelKind.Telegram, sender: "12345", externalMessageId: "b"), CancellationToken.None);

        Assert.NotEqual(bySms.Value.VisitorId, byTelegram.Value.VisitorId);
        Assert.Equal(2, harness.Identities.All.Count);
    }

    /// <summary>
    /// Tenant isolation, the same rule every other table here follows (data-model.md): one phone
    /// number messaging two shops is two visitors, so one shop's console can never surface the
    /// other's history for that number.
    /// </summary>
    [Fact]
    public async Task TheSameAddressOnTwoSites_IsTwoVisitors()
    {
        var harness = CreateHandler();
        var siteA = new SiteId(Guid.NewGuid());
        var siteB = new SiteId(Guid.NewGuid());

        var atA = await harness.Handler.HandleAsync(
            Inbound(siteA, externalMessageId: "a"), CancellationToken.None);
        var atB = await harness.Handler.HandleAsync(
            Inbound(siteB, externalMessageId: "b"), CancellationToken.None);

        Assert.NotEqual(atA.Value.VisitorId, atB.Value.VisitorId);
        Assert.NotEqual(atA.Value.ConversationId, atB.Value.ConversationId);
    }

    // -----------------------------------------------------------------------------------------
    // Idempotency - CLAUDE.md rule 5
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The redelivery case, at all three levels it could duplicate. Note what makes this pass: no
    /// dedup code was written for this item at all. The identity lookup prevents a second visitor,
    /// <c>StartConversationHandler</c>'s resume prevents a second conversation, and the derived
    /// <c>ClientMessageId</c> feeds `5-07`'s existing <c>Conversation.AddMessage</c> check. The
    /// returned sequence being the <em>original</em> one is the observable proof that the third
    /// happened rather than a second write.
    /// </summary>
    [Fact]
    public async Task ARedeliveredMessage_CreatesNoSecondVisitorConversationOrMessage()
    {
        var site = new SiteId(Guid.NewGuid());
        var harness = CreateHandler();
        var inbound = Inbound(site, externalMessageId: "provider-1");

        var first = await harness.Handler.HandleAsync(inbound, CancellationToken.None);
        var redelivery = await harness.Handler.HandleAsync(inbound, CancellationToken.None);

        Assert.Equal(first.Value.VisitorId, redelivery.Value.VisitorId);
        Assert.Equal(first.Value.ConversationId, redelivery.Value.ConversationId);
        Assert.Equal(first.Value.Sequence, redelivery.Value.Sequence);

        Assert.Single(harness.Identities.All);
        var conversation = await harness.Conversations.GetByIdAsync(
            first.Value.ConversationId, CancellationToken.None);
        Assert.Single(conversation!.Messages);
        Assert.Equal(1, conversation.LastSequence);
    }

    /// <summary>The mechanism behind the test above, asserted directly so a future change that stops
    /// deriving the key fails here with a clear cause rather than only as a mysterious duplicate.</summary>
    [Fact]
    public async Task TheDerivedClientMessageId_IsWhatReachesThePipeline()
    {
        var site = new SiteId(Guid.NewGuid());
        var harness = CreateHandler();

        await harness.Handler.HandleAsync(
            Inbound(site, ChannelKind.Max, externalMessageId: "provider-1"), CancellationToken.None);

        var pending = Assert.Single(harness.Pipeline.Enqueued);
        Assert.Equal(
            new ExternalMessageId("provider-1").ToClientMessageId(ChannelKind.Max),
            pending.ClientMessageId);
    }

    // -----------------------------------------------------------------------------------------
    // Ordering - CLAUDE.md rules 6 and 11
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Sequence follows arrival, and comes from the write. The clock is deliberately wound
    /// <em>backwards</em> between the two deliveries - an external channel is exactly where clock
    /// skew shows up - and the ordering is unaffected, because nothing in this path consults a time
    /// to order anything. The command carries no provider timestamp at all
    /// (<c>ChannelPortTests.ReceiveChannelMessage_CarriesNoTimestamp</c> keeps it that way), so there
    /// is not even a value available to sort by.
    /// </summary>
    [Fact]
    public async Task SequenceFollowsArrivalOrder_EvenWhenTheClockGoesBackwards()
    {
        var site = new SiteId(Guid.NewGuid());
        var identities = new FakeChannelIdentityRepository();
        var visitors = new FakeVisitorRepository();
        var conversations = new FakeConversationRepository();
        var clock = new FakeClock(Now);
        var idGenerator = new FakeIdGenerator();
        var pipeline = new FakeApplyingMessagePipeline(conversations, clock, idGenerator);
        var handler = new ReceiveChannelMessageHandler(
            identities, visitors, new FakePendingChannelLinkRequestRepository(),
            new StartConversationHandler(visitors, conversations, clock, idGenerator),
            new SendVisitorMessageHandler(
                conversations, new FakeRateLimiter(), new MessageSendRateLimitOptions(), pipeline),
            clock, idGenerator);

        var first = await handler.HandleAsync(
            Inbound(site, externalMessageId: "a", body: "first"), CancellationToken.None);
        clock.UtcNow = Now.AddHours(-1);
        var second = await handler.HandleAsync(
            Inbound(site, externalMessageId: "b", body: "second"), CancellationToken.None);

        Assert.Equal(1, first.Value.Sequence);
        Assert.Equal(2, second.Value.Sequence);

        var conversation = await conversations.GetByIdAsync(first.Value.ConversationId, CancellationToken.None);
        Assert.Equal(["first", "second"], conversation!.Messages.Select(m => m.Body.Value));
    }

    // -----------------------------------------------------------------------------------------
    // Failure propagation
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// An inbound channel message is rate limited by exactly the same buckets a widget message is -
    /// which is the concrete reason this handler composes <c>SendVisitorMessageHandler</c> instead of
    /// calling <c>IMessagePipeline</c> itself. An SMS flood is the abuse those limits exist for, and
    /// the channel path is the one an attacker does not need a browser for.
    /// </summary>
    [Fact]
    public async Task WhenTheVisitorRateLimitRejects_TheErrorIsPropagated()
    {
        var site = new SiteId(Guid.NewGuid());
        var identities = new FakeChannelIdentityRepository();
        var visitors = new FakeVisitorRepository();
        var conversations = new FakeConversationRepository();
        var clock = new FakeClock(Now);
        var idGenerator = new FakeIdGenerator();
        var handler = new ReceiveChannelMessageHandler(
            identities, visitors, new FakePendingChannelLinkRequestRepository(),
            new StartConversationHandler(visitors, conversations, clock, idGenerator),
            new SendVisitorMessageHandler(
                conversations,
                new RateLimitedFakeRateLimiter(TimeSpan.FromSeconds(5)),
                new MessageSendRateLimitOptions(),
                new FakeApplyingMessagePipeline(conversations, clock, idGenerator)),
            clock, idGenerator);

        var result = await handler.HandleAsync(Inbound(site), CancellationToken.None);

        Assert.True(result.IsFailure);
        // The identity is still linked: being rate limited is not a reason to forget who this
        // address belongs to, and the next message must resolve to the same visitor.
        Assert.Single(identities.All);
    }
}
