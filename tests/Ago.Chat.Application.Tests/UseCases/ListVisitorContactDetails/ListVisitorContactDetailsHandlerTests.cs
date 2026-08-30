using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ListVisitorContactDetails;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ListVisitorContactDetails;

public class ListVisitorContactDetailsHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());

    private sealed record Fixture(
        ListVisitorContactDetailsHandler Handler, FakeVisitorContactDetailRepository ContactDetails, ConversationId ConversationId);

    private static Fixture CreateFixture(bool permitted = true, SiteId? conversationSiteId = null)
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), conversationSiteId ?? SiteId, VisitorId, Now);
        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);

        var contactDetails = new FakeVisitorContactDetailRepository();
        var permissions = new FakePermissionChecker();
        if (permitted)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        }

        var handler = new ListVisitorContactDetailsHandler(conversations, contactDetails, permissions);
        return new Fixture(handler, contactDetails, conversation.Id);
    }

    private static Application.UseCases.ListVisitorContactDetails.ListVisitorContactDetails Query(ConversationId conversationId) =>
        new(conversationId, OperatorId, SiteId);

    [Fact]
    public async Task HandleAsync_ListsTheVisitorsRecordedDetails_OldestFirst()
    {
        var fixture = CreateFixture();
        var second = VisitorContactDetail.Record(
            new VisitorContactDetailId(Guid.NewGuid()), VisitorId, VisitorContactDetailKind.Email, "later@example.com",
            OperatorId, Now.AddMinutes(5));
        var first = VisitorContactDetail.Record(
            new VisitorContactDetailId(Guid.NewGuid()), VisitorId, VisitorContactDetailKind.Phone, "+1 555 0100",
            OperatorId, Now);
        await fixture.ContactDetails.SaveAsync(second, CancellationToken.None);
        await fixture.ContactDetails.SaveAsync(first, CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(Query(fixture.ConversationId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Count);
        Assert.Equal(first.Id.Value, result.Value[0].Id);
        Assert.Equal(second.Id.Value, result.Value[1].Id);
    }

    [Fact]
    public async Task HandleAsync_WithoutPermission_ReturnsForbidden()
    {
        var fixture = CreateFixture(permitted: false);

        var result = await fixture.Handler.HandleAsync(Query(fixture.ConversationId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_UnknownConversation_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Query(new ConversationId(Guid.NewGuid())), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    /// <summary>Cross-site isolation: a conversation from a different site reads like no such
    /// conversation, and its visitor's own contact details are never listed for this requester.</summary>
    [Fact]
    public async Task HandleAsync_ConversationBelongsToADifferentSite_ReturnsNotFound()
    {
        var otherSiteId = new SiteId(Guid.NewGuid());
        var fixture = CreateFixture(conversationSiteId: otherSiteId);
        var detail = VisitorContactDetail.Record(
            new VisitorContactDetailId(Guid.NewGuid()), VisitorId, VisitorContactDetailKind.Phone, "+1 555 0100", OperatorId, Now);
        await fixture.ContactDetails.SaveAsync(detail, CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(Query(fixture.ConversationId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }
}
