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

    private static Fixture CreateFixture(bool grantPermission = true, SiteId? conversationSiteId = null)
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
            conversations, contactDetails, permissions, new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, contactDetails, conversation.Id);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_SavesTheDetailWithVisitorAndTimestamp()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordVisitorContactDetail.RecordVisitorContactDetail(
                OperatorId, SiteId, fixture.ConversationId, "Phone", "+1 555 0100"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var saved = Assert.Single(fixture.ContactDetails.All);
        Assert.Equal(VisitorId, saved.VisitorId);
        Assert.Equal(VisitorContactDetailKind.Phone, saved.Kind);
        Assert.Equal("+1 555 0100", saved.Value);
        Assert.Equal(OperatorId, saved.RecordedByOperatorId);
        Assert.Equal(Now, saved.RecordedAt);
        Assert.Equal(saved.Id.Value, result.Value.Id);
    }

    [Fact]
    public async Task HandleAsync_OperatorWithoutPermission_ReturnsForbidden_AndSavesNothing()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordVisitorContactDetail.RecordVisitorContactDetail(
                OperatorId, SiteId, fixture.ConversationId, "Phone", "+1 555 0100"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.ContactDetails.All);
    }

    [Fact]
    public async Task HandleAsync_UnknownConversation_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordVisitorContactDetail.RecordVisitorContactDetail(
                OperatorId, SiteId, new ConversationId(Guid.NewGuid()), "Phone", "+1 555 0100"),
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
    public async Task HandleAsync_ConversationBelongsToADifferentSite_ReturnsNotFound_AndSavesNothing()
    {
        var otherSiteId = new SiteId(Guid.NewGuid());
        var fixture = CreateFixture(conversationSiteId: otherSiteId);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordVisitorContactDetail.RecordVisitorContactDetail(
                OperatorId, SiteId, fixture.ConversationId, "Phone", "+1 555 0100"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
        Assert.Empty(fixture.ContactDetails.All);
    }

    [Fact]
    public async Task HandleAsync_EmptyValue_ReturnsInvalid()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordVisitorContactDetail.RecordVisitorContactDetail(
                OperatorId, SiteId, fixture.ConversationId, "Phone", "   "),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.Invalid", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_UnrecognisedKind_ReturnsInvalidKind()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RecordVisitorContactDetail.RecordVisitorContactDetail(
                OperatorId, SiteId, fixture.ConversationId, "Fax", "+1 555 0100"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.InvalidKind", result.Error!.Value.Code);
    }
}
