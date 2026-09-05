using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RecordVisitorContactDetail;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RecordVisitorContactDetail;

public class RecordVisitorContactDetailHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        RecordVisitorContactDetailHandler Handler, FakeVisitorContactDetailRepository ContactDetails, ConversationId ConversationId);

    private static Fixture CreateFixture(
        bool grantPermission = true, SiteId? conversationSiteId = null, Ago.Platform.Abstractions.IRateLimiter? rateLimiter = null)
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), conversationSiteId ?? SiteId, VisitorId, Now);
        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationSend);
        }

        var contactDetails = new FakeVisitorContactDetailRepository();
        var handler = new RecordVisitorContactDetailHandler(
            conversations, contactDetails, permissions, rateLimiter ?? new FakeRateLimiter(),
            new ContactDetailRateLimitOptions(), new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, contactDetails, conversation.Id);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WhenPermitted_SavesTheDetailWithVisitorAndTimestamp()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new RecordVisitorContactDetailAsOperator(OperatorId, SiteId, fixture.ConversationId, "Phone", "+1 555 0100"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(fixture.ContactDetails.All);
        Assert.Equal(VisitorId, saved.VisitorId);
        Assert.Equal(VisitorContactDetailKind.Phone, saved.Kind);
        Assert.Equal("+1 555 0100", saved.Value);
        Assert.Equal(OperatorId, saved.RecordedByOperatorId);
        Assert.Equal(VisitorContactDetailSource.Operator, saved.Source);
        Assert.False(saved.Verified);
        Assert.Equal(Now, saved.RecordedAt);
        Assert.Equal(saved.Id.Value, result.Value.Id);
        Assert.Equal("Operator", result.Value.Source);
        Assert.False(result.Value.Verified);
        Assert.Equal(OperatorId.Value, result.Value.RecordedByOperatorId);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_OperatorWithoutPermission_ReturnsForbidden_AndSavesNothing()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new RecordVisitorContactDetailAsOperator(OperatorId, SiteId, fixture.ConversationId, "Phone", "+1 555 0100"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.ContactDetails.All);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_UnknownConversation_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new RecordVisitorContactDetailAsOperator(OperatorId, SiteId, new ConversationId(Guid.NewGuid()), "Phone", "+1 555 0100"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    /// <summary>Cross-site isolation: a conversation that is real but belongs to a *different* site
    /// than the requester's own must read exactly like no such conversation - the same info-hiding
    /// shape `RequestChannelLinkFromConsoleHandler`'s own cross-tenant guard already proves for
    /// itself. Nothing about the fact that the row exists on another tenant leaks through the error
    /// code, and no contact detail is recorded against it.</summary>
    [Fact]
    public async Task HandleAsOperatorAsync_ConversationBelongsToADifferentSite_ReturnsNotFound_AndSavesNothing()
    {
        var otherSiteId = new SiteId(Guid.NewGuid());
        var fixture = CreateFixture(conversationSiteId: otherSiteId);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new RecordVisitorContactDetailAsOperator(OperatorId, SiteId, fixture.ConversationId, "Phone", "+1 555 0100"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
        Assert.Empty(fixture.ContactDetails.All);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_EmptyValue_ReturnsInvalid()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new RecordVisitorContactDetailAsOperator(OperatorId, SiteId, fixture.ConversationId, "Phone", "   "),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.Invalid", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_UnrecognisedKind_ReturnsInvalidKind()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new RecordVisitorContactDetailAsOperator(OperatorId, SiteId, fixture.ConversationId, "Fax", "+1 555 0100"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.InvalidKind", result.Error!.Value.Code);
    }

    // ------------------------------------------------------------------------------------------
    // `23-09`: the visitor path. A genuinely different authorization shape from the operator path
    // above - no permission check, a participant comparison instead (this class's own remarks).
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task HandleAsVisitorAsync_WhenParticipant_SavesAnUnverifiedVisitorSourcedDetail_WithNoOperator()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(fixture.ConversationId, VisitorId, "Phone", "+1 555 0177"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(fixture.ContactDetails.All);
        Assert.Equal(VisitorId, saved.VisitorId);
        Assert.Equal("+1 555 0177", saved.Value);
        Assert.Equal(VisitorContactDetailSource.Visitor, saved.Source);
        Assert.False(saved.Verified);
        Assert.Null(saved.RecordedByOperatorId);
        Assert.Equal("Visitor", result.Value.Source);
        Assert.False(result.Value.Verified);
        Assert.Null(result.Value.RecordedByOperatorId);
    }

    /// <summary>Done-when: "the visitor cannot write a contact detail onto a conversation that is
    /// not theirs." A real conversation, a real visitor, but not *this* visitor - the participant
    /// check must reject it, the identical shape `CreateAttachmentHandler`'s own visitor-path guard
    /// already proves for attachments.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_NotTheConversationsVisitor_ReturnsForbidden_AndSavesNothing()
    {
        var fixture = CreateFixture();
        var someoneElse = new VisitorId(Guid.NewGuid());

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(fixture.ConversationId, someoneElse, "Phone", "+1 555 0177"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.ContactDetails.All);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_UnknownConversation_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(new ConversationId(Guid.NewGuid()), VisitorId, "Phone", "+1 555 0177"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_EmptyValue_ReturnsInvalid_AndSavesNothing()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(fixture.ConversationId, VisitorId, "Phone", "   "),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.Invalid", result.Error!.Value.Code);
        Assert.Empty(fixture.ContactDetails.All);
    }

    [Fact]
    public async Task HandleAsVisitorAsync_UnrecognisedKind_ReturnsInvalidKind()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(fixture.ConversationId, VisitorId, "Fax", "+1 555 0177"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.InvalidKind", result.Error!.Value.Code);
    }

    /// <summary>Done-when: "a visitor cannot set the verified flag, by any request they can
    /// construct." There is no field on <c>RecordVisitorContactDetailAsVisitor</c> a caller could set
    /// to request verification at all - the strongest form of this guarantee, proven here by
    /// asserting the outcome rather than merely noting the command's own shape.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_NeverProducesAVerifiedDetail()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(fixture.ConversationId, VisitorId, "Phone", "+1 555 0177"),
            CancellationToken.None);

        Assert.All(fixture.ContactDetails.All, d => Assert.False(d.Verified));
    }

    [Fact]
    public async Task HandleAsVisitorAsync_RateLimited_ReturnsRateLimited_AndSavesNothing()
    {
        var fixture = CreateFixture(rateLimiter: new RateLimitedFakeRateLimiter(TimeSpan.FromSeconds(30)));

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(fixture.ConversationId, VisitorId, "Phone", "+1 555 0177"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Message.RateLimited", result.Error!.Value.Code);
        Assert.Empty(fixture.ContactDetails.All);
    }

    /// <summary>Proves the *site* bucket is consulted too, not just the visitor one - the same
    /// `SelectiveFakeRateLimiter` technique `CreateAttachmentHandlerTests`'s own site-bucket test
    /// uses.</summary>
    [Fact]
    public async Task HandleAsVisitorAsync_SiteRateLimited_ReturnsRateLimited()
    {
        var fixture = CreateFixture(rateLimiter: new SelectiveFakeRateLimiter("contact-detail:site:", TimeSpan.FromSeconds(45)));

        var result = await fixture.Handler.HandleAsVisitorAsync(
            new RecordVisitorContactDetailAsVisitor(fixture.ConversationId, VisitorId, "Phone", "+1 555 0177"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Message.RateLimited", result.Error!.Value.Code);
        Assert.Empty(fixture.ContactDetails.All);
    }
}
