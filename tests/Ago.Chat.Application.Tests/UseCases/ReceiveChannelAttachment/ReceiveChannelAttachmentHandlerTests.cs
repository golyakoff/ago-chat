using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ConfirmAttachment;
using Ago.Chat.Application.UseCases.CreateAttachment;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Application.UseCases.SendMessage;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Tests.UseCases.ReceiveChannelAttachment;

/// <summary>
/// `25-161`: the end-to-end proof for both of this item's own symptoms - see
/// <see cref="Application.UseCases.ReceiveChannelAttachment.ReceiveChannelAttachmentHandler"/>'s own
/// class remarks for why it composes <see cref="CreateAttachmentHandler"/>/
/// <see cref="ConfirmAttachmentHandler"/>/<see cref="SendVisitorMessageHandler"/> rather than writing a
/// fourth attachment path, and why it is a two-phase <c>PrepareAsync</c>/<c>CompleteAsync</c> protocol.
/// What each group below defends:
/// <list type="bullet">
/// <item><b>The grant (symptom 1)</b> - an ungranted conversation is refused, with a visitor-facing
/// system message, never silently accepted.</item>
/// <item><b>Delivery (symptom 2)</b> - a granted upload actually produces a real, ready
/// <see cref="Attachment"/> linked to a real message once <c>CompleteAsync</c> runs.</item>
/// <item><b>Resolution</b> - the identical "brand-new address -&gt; mint a visitor" shape every other
/// inbound-channel handler in this codebase already establishes.</item>
/// </list>
/// </summary>
public class ReceiveChannelAttachmentHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        Application.UseCases.ReceiveChannelAttachment.ReceiveChannelAttachmentHandler Handler,
        FakeChannelIdentityRepository Identities,
        FakeVisitorRepository Visitors,
        FakeConversationRepository Conversations,
        FakeAttachmentRepository Attachments,
        FakeFileStorage FileStorage,
        FakeMessagePipeline Pipeline,
        FakeOutboxWriter Outbox);

    private static Fixture CreateFixture()
    {
        var identities = new FakeChannelIdentityRepository();
        var visitors = new FakeVisitorRepository();
        var conversations = new FakeConversationRepository();
        var attachments = new FakeAttachmentRepository();
        var fileStorage = new FakeFileStorage();
        var clock = new FakeClock(Now);
        var idGenerator = new FakeIdGenerator();
        var emojiPairs = new FakeVisitorEmojiPairGenerator();
        var outbox = new FakeOutboxWriter();
        var pipeline = new FakeMessagePipeline();

        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, $"site_{SiteId.Value:N}", ["https://example.test"], "Test Site", Now));
        var siteConfig = new GetSiteConfigByIdHandler(sites, new FakeCache());

        var restrictions = new FakeVisitorRestrictionRepository();
        var startConversation = new StartConversationHandler(
            visitors, conversations, restrictions, siteConfig,
            new FakeRateLimiter(), new ConversationCreateRateLimitOptions(), clock, idGenerator, emojiPairs,
            outbox, identities);

        var permissions = new FakePermissionChecker();
        var createAttachment = new CreateAttachmentHandler(
            conversations, attachments, fileStorage, new FakeRateLimiter(), permissions,
            new FakeConversationAttachmentBudget(), new FakeSiteAttachmentStorageBudget(), sites,
            new FakeBillingSubscriptionRepository(), new FakeUnitOfWork(), new AttachmentOptions(),
            new AttachmentRateLimitOptions(), new AttachmentStorageQuotaOptions(), idGenerator, clock);

        var confirmAttachment = new ConfirmAttachmentHandler(
            attachments, conversations, fileStorage, permissions, outbox, idGenerator, clock);

        var sendVisitorMessage = new SendVisitorMessageHandler(
            conversations, new FakeRateLimiter(), new MessageSendRateLimitOptions(), pipeline);

        var handler = new Application.UseCases.ReceiveChannelAttachment.ReceiveChannelAttachmentHandler(
            identities, visitors, conversations, startConversation, createAttachment, confirmAttachment,
            sendVisitorMessage, siteConfig, new AlwaysEntitledBillingOptionEntitlementProvider(),
            new AlwaysEntitledModuleQuantityGrantStore(), outbox, clock, idGenerator, emojiPairs);

        return new Fixture(handler, identities, visitors, conversations, attachments, fileStorage, pipeline, outbox);
    }

    /// <summary>The one conversation this fixture ever creates, resolved the same way any other caller
    /// would - <see cref="FakeConversationRepository"/> exposes no bare "every row" read (nothing in the
    /// real port needs one), so this goes through the resolved visitor, exactly as
    /// <c>IConversationRepository.GetActiveForVisitorAsync</c> is meant to be used.</summary>
    private static async Task<Conversation> GetTheOneConversationAsync(Fixture fixture)
    {
        var visitorId = Assert.Single(fixture.Identities.All).VisitorId;
        var conversation = await fixture.Conversations.GetActiveForVisitorAsync(visitorId, CancellationToken.None);
        Assert.NotNull(conversation);
        return conversation!;
    }

    // -----------------------------------------------------------------------------------------
    // `25-170`: the entitlement guard - checked before any of the grant/identity work below even
    // starts, since a site with no channel entitlement at all should not be minting visitors for it.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task PrepareAsync_WhenTheSitesChannelEntitlementHasLapsedToZero_Refuses_AndCreatesNoIdentity()
    {
        var identities = new FakeChannelIdentityRepository();
        var visitors = new FakeVisitorRepository();
        var conversations = new FakeConversationRepository();
        var attachments = new FakeAttachmentRepository();
        var fileStorage = new FakeFileStorage();
        var clock = new FakeClock(Now);
        var idGenerator = new FakeIdGenerator();
        var emojiPairs = new FakeVisitorEmojiPairGenerator();
        var outbox = new FakeOutboxWriter();
        var pipeline = new FakeMessagePipeline();

        var sites = new FakeSiteRepository();
        sites.Seed(new Site(SiteId, $"site_{SiteId.Value:N}", ["https://example.test"], "Test Site", Now));
        var siteConfig = new GetSiteConfigByIdHandler(sites, new FakeCache());
        var restrictions = new FakeVisitorRestrictionRepository();
        var startConversation = new StartConversationHandler(
            visitors, conversations, restrictions, siteConfig,
            new FakeRateLimiter(), new ConversationCreateRateLimitOptions(), clock, idGenerator, emojiPairs,
            outbox, identities);
        var permissions = new FakePermissionChecker();
        var createAttachment = new CreateAttachmentHandler(
            conversations, attachments, fileStorage, new FakeRateLimiter(), permissions,
            new FakeConversationAttachmentBudget(), new FakeSiteAttachmentStorageBudget(), sites,
            new FakeBillingSubscriptionRepository(), new FakeUnitOfWork(), new AttachmentOptions(),
            new AttachmentRateLimitOptions(), new AttachmentStorageQuotaOptions(), idGenerator, clock);
        var confirmAttachment = new ConfirmAttachmentHandler(
            attachments, conversations, fileStorage, permissions, outbox, idGenerator, clock);
        var sendVisitorMessage = new SendVisitorMessageHandler(
            conversations, new FakeRateLimiter(), new MessageSendRateLimitOptions(), pipeline);

        var entitlements = new FakeBillingOptionEntitlementProvider();
        var moduleKey = new ModuleKey("channel-max");
        entitlements.Map(ChannelEntitlementOptionKeys.For(ChannelKind.Max), moduleKey);
        var grants = new FakeModuleQuantityGrantStore();
        grants.Grants[(SiteId, moduleKey)] = 0;

        var handler = new Application.UseCases.ReceiveChannelAttachment.ReceiveChannelAttachmentHandler(
            identities, visitors, conversations, startConversation, createAttachment, confirmAttachment,
            sendVisitorMessage, siteConfig, entitlements, grants, outbox, clock, idGenerator, emojiPairs);

        var result = await handler.PrepareAsync(
            new Application.UseCases.ReceiveChannelAttachment.PrepareChannelAttachmentUpload(
                SiteId, ChannelKind.Max, new ExternalChannelAddress("max-chat-1"), "image/jpeg", 1024),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ChannelCredential.NotEntitled", result.Error!.Value.Code);
        Assert.Empty(identities.All);
    }

    // -----------------------------------------------------------------------------------------
    // The grant - this item's own symptom 1: nothing on the inbound bot-channel path ever checked
    // `23-78`'s grant at all, because nothing on that path ever created an Attachment in the first
    // place. Routing through CreateAttachmentHandler is what makes this check exist here at all.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task PrepareAsync_ForABrandNewUngrantedConversation_RefusesAndTellsTheVisitor()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.PrepareAsync(
            new Application.UseCases.ReceiveChannelAttachment.PrepareChannelAttachmentUpload(
                SiteId, ChannelKind.Max, new ExternalChannelAddress("max-chat-1"), "image/jpeg", 1024),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            Application.UseCases.ReceiveChannelAttachment.ChannelAttachmentPrepareOutcome.Refused,
            result.Value.Outcome);
        Assert.Null(result.Value.UploadUrl);
        Assert.Null(result.Value.AttachmentId);

        // No Attachment was ever created - the refusal happens before CreateAttachmentHandler's own
        // budget reservation and presign, exactly as it does for a widget visitor with no grant.
        var conversation = await GetTheOneConversationAsync(fixture);
        Assert.False(conversation.HasAttachmentUploadGrant);

        // The visitor was told, over the conversation itself - a real Ago.Chat.Domain.Message, System-
        // authored, not a silently dropped update.
        var systemMessage = Assert.Single(conversation.Messages, m => m.AuthorKind == MessageAuthorKind.System);
        Assert.Contains("enable file uploads", systemMessage.Body.Value, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(fixture.Outbox.Enqueued);
    }

    [Fact]
    public async Task PrepareAsync_ForAGrantedConversation_ReturnsReadyWithAPresignedUrl()
    {
        var fixture = CreateFixture();

        // First call mints the visitor/conversation; grant it, then prepare again the way a second
        // inbound MAX image (the ticket's own live repro: one ungranted, one after the grant) would.
        var address = new ExternalChannelAddress("max-chat-1");
        var first = await fixture.Handler.PrepareAsync(
            new Application.UseCases.ReceiveChannelAttachment.PrepareChannelAttachmentUpload(
                SiteId, ChannelKind.Max, address, "image/jpeg", 1024),
            CancellationToken.None);
        var conversation = await GetTheOneConversationAsync(fixture);
        conversation.MarkAttachmentUploadGrantedForTesting(new OperatorId(Guid.NewGuid()), Now);
        await fixture.Conversations.SaveAsync(conversation, CancellationToken.None);

        var second = await fixture.Handler.PrepareAsync(
            new Application.UseCases.ReceiveChannelAttachment.PrepareChannelAttachmentUpload(
                SiteId, ChannelKind.Max, address, "image/jpeg", 1024),
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.Equal(Application.UseCases.ReceiveChannelAttachment.ChannelAttachmentPrepareOutcome.Refused, first.Value.Outcome);
        Assert.True(second.IsSuccess);
        Assert.Equal(Application.UseCases.ReceiveChannelAttachment.ChannelAttachmentPrepareOutcome.Ready, second.Value.Outcome);
        Assert.NotNull(second.Value.UploadUrl);
        Assert.NotNull(second.Value.AttachmentId);
        var attachment = await fixture.Attachments.GetByIdAsync(second.Value.AttachmentId!.Value, CancellationToken.None);
        Assert.NotNull(attachment);
        Assert.Equal(AttachmentState.Pending, attachment!.State);
    }

    // -----------------------------------------------------------------------------------------
    // Delivery - this item's own symptom 2: even once granted, the image never arrived at all. This is
    // the full Prepare -> (caller uploads bytes) -> Complete round trip, proving a real, ready
    // Attachment ends up linked to a real message.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task CompleteAsync_AfterAGrantedPrepareAndAnUpload_ProducesAReadyAttachmentOnARealMessage()
    {
        var fixture = CreateFixture();
        var address = new ExternalChannelAddress("max-chat-1");

        await fixture.Handler.PrepareAsync(
            new Application.UseCases.ReceiveChannelAttachment.PrepareChannelAttachmentUpload(
                SiteId, ChannelKind.Max, address, "image/jpeg", 1024),
            CancellationToken.None);
        var conversation = await GetTheOneConversationAsync(fixture);
        conversation.MarkAttachmentUploadGrantedForTesting(new OperatorId(Guid.NewGuid()), Now);
        await fixture.Conversations.SaveAsync(conversation, CancellationToken.None);

        var prepared = await fixture.Handler.PrepareAsync(
            new Application.UseCases.ReceiveChannelAttachment.PrepareChannelAttachmentUpload(
                SiteId, ChannelKind.Max, address, "image/jpeg", 1024),
            CancellationToken.None);
        Assert.Equal(Application.UseCases.ReceiveChannelAttachment.ChannelAttachmentPrepareOutcome.Ready, prepared.Value.Outcome);

        // Simulates the calling adapter's own bare-HttpClient PUT against the presigned URL - see
        // MaxInboundAttachmentDispatch's own remarks (Ago.Chat.Infrastructure.MaxBot) for the real one.
        var attachment = await fixture.Attachments.GetByIdAsync(prepared.Value.AttachmentId!.Value, CancellationToken.None);
        fixture.FileStorage.SetMetadata(new ObjectKey(attachment!.ObjectKey), new ObjectMetadata(1024, "image/jpeg"));

        var externalMessageId = new ExternalMessageId("photo-mid-1");
        var completed = await fixture.Handler.CompleteAsync(
            new Application.UseCases.ReceiveChannelAttachment.CompleteChannelAttachmentUpload(
                prepared.Value.AttachmentId!.Value, prepared.Value.VisitorId!.Value, prepared.Value.ConversationId!.Value,
                externalMessageId, ChannelKind.Max),
            CancellationToken.None);

        Assert.True(completed.IsSuccess);

        var confirmed = await fixture.Attachments.GetByIdAsync(prepared.Value.AttachmentId!.Value, CancellationToken.None);
        Assert.Equal(AttachmentState.Ready, confirmed!.State);

        var pending = Assert.Single(fixture.Pipeline.Enqueued);
        Assert.Equal(prepared.Value.AttachmentId!.Value, pending.AttachmentId);
        Assert.Equal(externalMessageId.ToClientMessageId(ChannelKind.Max), pending.ClientMessageId);
    }

    [Fact]
    public async Task CompleteAsync_WhenTheUploadNeverActuallyLanded_FailsVerificationAndSendsNoMessage()
    {
        var fixture = CreateFixture();
        var address = new ExternalChannelAddress("max-chat-1");

        await fixture.Handler.PrepareAsync(
            new Application.UseCases.ReceiveChannelAttachment.PrepareChannelAttachmentUpload(
                SiteId, ChannelKind.Max, address, "image/jpeg", 1024),
            CancellationToken.None);
        var conversation = await GetTheOneConversationAsync(fixture);
        conversation.MarkAttachmentUploadGrantedForTesting(new OperatorId(Guid.NewGuid()), Now);
        await fixture.Conversations.SaveAsync(conversation, CancellationToken.None);

        var prepared = await fixture.Handler.PrepareAsync(
            new Application.UseCases.ReceiveChannelAttachment.PrepareChannelAttachmentUpload(
                SiteId, ChannelKind.Max, address, "image/jpeg", 1024),
            CancellationToken.None);

        // No SetMetadata call this time - the presigned PUT this test pretends never happened, exactly
        // the case MaxInboundAttachmentDispatch's own remarks describe for a storage-side rejection.
        var completed = await fixture.Handler.CompleteAsync(
            new Application.UseCases.ReceiveChannelAttachment.CompleteChannelAttachmentUpload(
                prepared.Value.AttachmentId!.Value, prepared.Value.VisitorId!.Value, prepared.Value.ConversationId!.Value,
                new ExternalMessageId("photo-mid-2"), ChannelKind.Max),
            CancellationToken.None);

        Assert.True(completed.IsFailure);
        Assert.Empty(fixture.Pipeline.Enqueued);
    }

    // -----------------------------------------------------------------------------------------
    // Resolution - the identical "brand-new address -> mint a visitor, existing -> resolve" shape
    // RecordChannelVisitorContactHandler/ReceiveChannelMessageHandler already establish.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task PrepareAsync_ForAnAddressWithAnExistingIdentity_ResolvesToTheSameVisitorAndConversation()
    {
        var fixture = CreateFixture();
        var address = new ExternalChannelAddress("max-chat-1");

        var first = await fixture.Handler.PrepareAsync(
            new Application.UseCases.ReceiveChannelAttachment.PrepareChannelAttachmentUpload(
                SiteId, ChannelKind.Max, address, "image/jpeg", 1024),
            CancellationToken.None);
        var second = await fixture.Handler.PrepareAsync(
            new Application.UseCases.ReceiveChannelAttachment.PrepareChannelAttachmentUpload(
                SiteId, ChannelKind.Max, address, "image/jpeg", 1024),
            CancellationToken.None);

        Assert.Single(fixture.Identities.All);
        await GetTheOneConversationAsync(fixture);
    }
}
