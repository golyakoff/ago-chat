using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.DeleteVisitorContactDetail;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.DeleteVisitorContactDetail;

public class DeleteVisitorContactDetailHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());

    private sealed record Fixture(
        DeleteVisitorContactDetailHandler Handler,
        FakeVisitorContactDetailRepository ContactDetails,
        ConversationId ConversationId,
        VisitorContactDetailId DetailId);

    private static async Task<Fixture> CreateFixtureAsync(
        bool grantPermission = true, SiteId? conversationSiteId = null, VisitorId? detailVisitorId = null)
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
        var detail = VisitorContactDetail.Record(
            new VisitorContactDetailId(Guid.NewGuid()), detailVisitorId ?? VisitorId, VisitorContactDetailKind.Phone,
            "+1 555 0100", OperatorId, Now);
        await contactDetails.SaveAsync(detail, CancellationToken.None);

        var handler = new DeleteVisitorContactDetailHandler(conversations, contactDetails, permissions);
        return new Fixture(handler, contactDetails, conversation.Id, detail.Id);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_DeletesTheDetail()
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.DeleteVisitorContactDetail.DeleteVisitorContactDetail(
                OperatorId, SiteId, fixture.ConversationId, fixture.DetailId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(fixture.ContactDetails.All);
    }

    [Fact]
    public async Task HandleAsync_OperatorWithoutPermission_ReturnsForbidden_AndKeepsTheDetail()
    {
        var fixture = await CreateFixtureAsync(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.DeleteVisitorContactDetail.DeleteVisitorContactDetail(
                OperatorId, SiteId, fixture.ConversationId, fixture.DetailId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Single(fixture.ContactDetails.All);
    }

    [Fact]
    public async Task HandleAsync_UnknownConversation_ReturnsNotFound()
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.DeleteVisitorContactDetail.DeleteVisitorContactDetail(
                OperatorId, SiteId, new ConversationId(Guid.NewGuid()), fixture.DetailId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
        Assert.Single(fixture.ContactDetails.All);
    }

    /// <summary>Cross-site isolation: a conversation from a different site reads like no such
    /// conversation, and nothing is deleted.</summary>
    [Fact]
    public async Task HandleAsync_ConversationBelongsToADifferentSite_ReturnsNotFound_AndKeepsTheDetail()
    {
        var otherSiteId = new SiteId(Guid.NewGuid());
        var fixture = await CreateFixtureAsync(conversationSiteId: otherSiteId);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.DeleteVisitorContactDetail.DeleteVisitorContactDetail(
                OperatorId, SiteId, fixture.ConversationId, fixture.DetailId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
        Assert.Single(fixture.ContactDetails.All);
    }

    /// <summary>A real row that exists but belongs to a *different visitor* than the conversation being
    /// used to reach it - the same "wrong visitor reads like no row" info-hiding guard this handler's
    /// own remarks describe, exercised directly rather than only through the site check above.</summary>
    [Fact]
    public async Task HandleAsync_DetailBelongsToADifferentVisitor_ReturnsNotFound_AndKeepsTheDetail()
    {
        var fixture = await CreateFixtureAsync(detailVisitorId: new VisitorId(Guid.NewGuid()));

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.DeleteVisitorContactDetail.DeleteVisitorContactDetail(
                OperatorId, SiteId, fixture.ConversationId, fixture.DetailId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.NotFound", result.Error!.Value.Code);
        Assert.Single(fixture.ContactDetails.All);
    }

    [Fact]
    public async Task HandleAsync_UnknownContactDetailId_ReturnsNotFound()
    {
        var fixture = await CreateFixtureAsync();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.DeleteVisitorContactDetail.DeleteVisitorContactDetail(
                OperatorId, SiteId, fixture.ConversationId, new VisitorContactDetailId(Guid.NewGuid())),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("VisitorContactDetail.NotFound", result.Error!.Value.Code);
    }
}
