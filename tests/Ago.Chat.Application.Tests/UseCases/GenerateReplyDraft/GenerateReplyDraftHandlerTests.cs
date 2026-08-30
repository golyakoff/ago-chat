using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GenerateReplyDraft;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Tests.UseCases.GenerateReplyDraft;

/// <summary>
/// `19-01`: the fakes-based half of this item's own proofs - `CreateAttachmentHandlerTests`'s own
/// shape (access checks, then rate limiting, then the real work), plus two proofs this item's own
/// Done-when specifically asks for: the rate cap actually rejecting a caller who exceeds it, and the
/// handler never reaching past *this* conversation's own history for what it hands to the generator.
/// The complementary proof - that the wire payload YandexGptReplyDraftClient actually sends carries
/// nothing beyond that - is `YandexGptReplyDraftClientTests` (`Ago.Chat.Integration.Tests`), which this
/// class cannot reach (`Ago.Chat.Infrastructure.YandexGpt` is not visible from here, the same layering
/// `ResilientInboundChannelAdapterTests`' own remarks describe for `Ago.Chat.Module`).
/// </summary>
public class GenerateReplyDraftHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        GenerateReplyDraftHandler Handler, FakeReplyDraftGenerator Generator, Conversation Conversation);

    private static Fixture CreateFixture(
        IRateLimiter? rateLimiter = null,
        bool grantPermission = true,
        bool assignOperator = true,
        Action<Conversation>? seedMessages = null)
    {
        var conversations = new FakeConversationRepository();
        var readStore = new FakeConversationReadStore();
        var permissions = new FakePermissionChecker();
        var generator = new FakeReplyDraftGenerator();

        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        if (assignOperator)
        {
            conversation.AssignTo(OperatorId, Now);
        }

        (seedMessages ?? DefaultMessages)(conversation);

        conversations.Seed(conversation);
        readStore.Seed(conversation);
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationSend);
        }

        var handler = new GenerateReplyDraftHandler(
            conversations,
            readStore,
            permissions,
            rateLimiter ?? new FakeRateLimiter(),
            generator,
            new ReplyDraftOptions(),
            new ReplyDraftRateLimitOptions());

        return new Fixture(handler, generator, conversation);
    }

    private static void DefaultMessages(Conversation conversation)
    {
        conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("do you ship to Kazan?"), Now);
        // Only once assigned - AddOperatorMessage refuses a Waiting conversation, and
        // HandleAsync_WhenOperatorIsNotAssignedToTheConversation_ReturnsForbidden deliberately builds
        // its fixture with assignOperator: false.
        if (conversation.State == ConversationState.Assigned)
        {
            conversation.AddOperatorMessage(OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("yes, 3-5 days"), Now);
        }
    }

    [Fact]
    public async Task HandleAsync_WhenAllowedAndAssigned_ReturnsTheGeneratedDraft()
    {
        var fixture = CreateFixture();
        fixture.Generator.NextResult = new ReplyDraftGenerationResult.Success("Yes, we ship to Kazan within 3-5 days.");

        var result = await fixture.Handler.HandleAsync(
            new GenerateReplyDraftAsOperator(fixture.Conversation.Id, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Yes, we ship to Kazan within 3-5 days.", result.Value.DraftText);
    }

    [Fact]
    public async Task HandleAsync_WhenOperatorLacksPermission_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new GenerateReplyDraftAsOperator(fixture.Conversation.Id, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Null(fixture.Generator.LastRequest);
    }

    [Fact]
    public async Task HandleAsync_WhenOperatorIsNotAssignedToTheConversation_ReturnsForbidden()
    {
        var fixture = CreateFixture(assignOperator: false);

        var result = await fixture.Handler.HandleAsync(
            new GenerateReplyDraftAsOperator(fixture.Conversation.Id, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Null(fixture.Generator.LastRequest);
    }

    [Fact]
    public async Task HandleAsync_WhenTheConversationDoesNotExist_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new GenerateReplyDraftAsOperator(new ConversationId(Guid.NewGuid()), OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    /// <summary>`19-01`'s own Done-when: "a rate/cost cap exists and is enforced... proven by a test
    /// that exceeds it and confirms the expected rejection, not just that a config value exists."
    /// <see cref="RateLimitedFakeRateLimiter"/> denies every check, standing in for an operator who has
    /// already spent this hour's budget - the handler must never reach the (real-money) provider call
    /// once that happens, which <see cref="FakeReplyDraftGenerator.LastRequest"/> being null proves.</summary>
    [Fact]
    public async Task HandleAsync_WhenThePerOperatorRateLimitIsExceeded_ReturnsRateLimited_AndNeverCallsTheProvider()
    {
        var fixture = CreateFixture(rateLimiter: new RateLimitedFakeRateLimiter(TimeSpan.FromMinutes(7)));

        var result = await fixture.Handler.HandleAsync(
            new GenerateReplyDraftAsOperator(fixture.Conversation.Id, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ReplyDraft.RateLimited", result.Error!.Value.Code);
        Assert.Contains("420.0s", result.Error!.Value.Message);
        Assert.Null(fixture.Generator.LastRequest);
    }

    /// <summary>The per-site bucket is checked too, independently of the per-operator one -
    /// <see cref="SelectiveFakeRateLimiter"/> denies only the `reply-draft:site:` key, proving this is
    /// a real second check and not the same bucket asked twice.</summary>
    [Fact]
    public async Task HandleAsync_WhenThePerSiteRateLimitIsExceeded_ReturnsRateLimited()
    {
        var fixture = CreateFixture(
            rateLimiter: new SelectiveFakeRateLimiter("reply-draft:site:", TimeSpan.FromMinutes(2)));

        var result = await fixture.Handler.HandleAsync(
            new GenerateReplyDraftAsOperator(fixture.Conversation.Id, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ReplyDraft.RateLimited", result.Error!.Value.Code);
        Assert.Null(fixture.Generator.LastRequest);
    }

    [Fact]
    public async Task HandleAsync_WhenTheGeneratorDegradesToUnavailable_ReturnsReplyDraftUnavailable()
    {
        var fixture = CreateFixture();
        fixture.Generator.NextResult = new ReplyDraftGenerationResult.Unavailable("provider timed out");

        var result = await fixture.Handler.HandleAsync(
            new GenerateReplyDraftAsOperator(fixture.Conversation.Id, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ReplyDraft.Unavailable", result.Error!.Value.Code);
        Assert.Equal("provider timed out", result.Error!.Value.Message);
    }

    /// <summary>`19-01`'s own context-minimalism Done-when, the Application-layer half: the handler
    /// must never read or forward anything beyond the requested conversation's own recent history. Two
    /// conversations are seeded with deliberately distinguishable bodies; a draft is requested for only
    /// one, and the captured request is asserted to contain that one's text and specifically not the
    /// other's.</summary>
    [Fact]
    public async Task HandleAsync_OnlySendsThisConversationsOwnMessages_NeverAnotherConversations()
    {
        var conversations = new FakeConversationRepository();
        var readStore = new FakeConversationReadStore();
        var permissions = new FakePermissionChecker();
        var generator = new FakeReplyDraftGenerator();

        var target = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        target.AssignTo(OperatorId, Now);
        target.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("TARGET-secret-order-12345"), Now);
        conversations.Seed(target);
        readStore.Seed(target);

        var otherVisitor = new VisitorId(Guid.NewGuid());
        var otherOperator = new OperatorId(Guid.NewGuid());
        var decoy = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, otherVisitor, Now);
        decoy.AssignTo(otherOperator, Now);
        decoy.AddVisitorMessage(otherVisitor, new MessageId(Guid.NewGuid()), new MessageBody("DECOY-unrelated-medical-question"), Now);
        conversations.Seed(decoy);
        readStore.Seed(decoy);

        permissions.Grant(OperatorId, SiteId, Permission.ConversationSend);

        var handler = new GenerateReplyDraftHandler(
            conversations, readStore, permissions, new FakeRateLimiter(), generator, new ReplyDraftOptions(), new ReplyDraftRateLimitOptions());

        await handler.HandleAsync(new GenerateReplyDraftAsOperator(target.Id, OperatorId, SiteId), CancellationToken.None);

        Assert.NotNull(generator.LastRequest);
        Assert.Contains(generator.LastRequest!.RecentMessages, m => m.Body == "TARGET-secret-order-12345");
        Assert.DoesNotContain(generator.LastRequest!.RecentMessages, m => m.Body == "DECOY-unrelated-medical-question");
        Assert.Single(generator.LastRequest!.RecentMessages);
    }

    /// <summary>Oldest-first for the provider (the read store's own page is newest-first,
    /// `ConversationHistoryPage`'s own remarks), and a structured (`14-06`) message is dropped rather
    /// than forwarded - `adr/0061`'s "AGO Chat must not interpret a module's payload" applied to an LLM
    /// prompt, where forwarding an opaque payload would be actively worse than dropping it.</summary>
    [Fact]
    public async Task HandleAsync_OrdersMessagesChronologically_AndDropsStructuredContent()
    {
        var fixture = CreateFixture(seedMessages: conversation =>
        {
            conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("first"), Now);
            conversation.AddOperatorMessage(
                OperatorId, new MessageId(Guid.NewGuid()), new MessageBody("a booking card"), Now.AddSeconds(1),
                content: MessageContent.Create(new MessageContentKind("booking_card"), new MessagePayload("{\"price\":100}")));
            conversation.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("second"), Now.AddSeconds(2));
        });

        await fixture.Handler.HandleAsync(
            new GenerateReplyDraftAsOperator(fixture.Conversation.Id, OperatorId, SiteId), CancellationToken.None);

        var sent = fixture.Generator.LastRequest!.RecentMessages;
        Assert.Equal(2, sent.Count);
        Assert.Equal("first", sent[0].Body);
        Assert.Equal(ReplyDraftAuthorKind.Visitor, sent[0].AuthorKind);
        Assert.Equal("second", sent[1].Body);
        Assert.Equal(ReplyDraftAuthorKind.Visitor, sent[1].AuthorKind);
    }
}
