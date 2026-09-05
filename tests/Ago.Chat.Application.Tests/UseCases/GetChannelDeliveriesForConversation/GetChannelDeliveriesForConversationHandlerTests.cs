using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetChannelDeliveriesForConversation;

public class GetChannelDeliveriesForConversationHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());

    private sealed record Fixture(
        Application.UseCases.GetChannelDeliveriesForConversation.GetChannelDeliveriesForConversationHandler Handler,
        FakeChannelDeliveryReadStore Deliveries,
        Conversation Conversation);

    private static Fixture CreateFixture(bool grantPermission = true, bool assignToRequester = true)
    {
        var conversations = new FakeConversationRepository();
        var deliveries = new FakeChannelDeliveryReadStore();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        }

        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now);
        if (assignToRequester)
        {
            conversation.AssignTo(OperatorId, Now);
        }
        else
        {
            conversation.AssignTo(new OperatorId(Guid.NewGuid()), Now);
        }

        conversations.Seed(conversation);

        var handler = new Application.UseCases.GetChannelDeliveriesForConversation.GetChannelDeliveriesForConversationHandler(
            conversations, deliveries, permissions);

        return new Fixture(handler, deliveries, conversation);
    }

    [Fact]
    public async Task HandleAsync_ReturnsTheConversationsDeliveryHistory()
    {
        var fixture = CreateFixture();
        var deliveryId = new ChannelDeliveryId(Guid.NewGuid());
        fixture.Deliveries.Seed(fixture.Conversation.Id, SiteId, new ChannelDeliverySummaryItem(
            deliveryId, new MessageId(Guid.NewGuid()), ChannelKind.Sms, ChannelDeliveryStatus.Delivered, "p-1", null, Now));

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetChannelDeliveriesForConversation.GetChannelDeliveriesForConversation(
                fixture.Conversation.Id, SiteId, OperatorId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var dto = Assert.Single(result.Value.Deliveries);
        Assert.Equal(deliveryId.Value, dto.Id);
        Assert.Equal("Delivered", dto.Status);
        Assert.Equal("Sms", dto.ChannelKind);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksConversationRead_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetChannelDeliveriesForConversation.GetChannelDeliveriesForConversation(
                fixture.Conversation.Id, SiteId, OperatorId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    /// <summary>The cross-tenant isolation line this handler draws: an operator with `conversation:read`
    /// on their own site cannot read another site's conversation's delivery history by guessing its id -
    /// this conversation belongs to a different operator entirely (not merely a different site), the
    /// same "assigned operator only" gate `GetConversationHistoryHandler.HandleAsOperatorAsync` already
    /// enforces.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheOperatorIsNotAssignedToTheConversation_ReturnsForbidden_AndReadsNoHistory()
    {
        var fixture = CreateFixture(assignToRequester: false);
        fixture.Deliveries.Seed(fixture.Conversation.Id, SiteId, new ChannelDeliverySummaryItem(
            new ChannelDeliveryId(Guid.NewGuid()), new MessageId(Guid.NewGuid()), ChannelKind.Sms,
            ChannelDeliveryStatus.Delivered, "p-1", null, Now));

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetChannelDeliveriesForConversation.GetChannelDeliveriesForConversation(
                fixture.Conversation.Id, SiteId, OperatorId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheConversationDoesNotExist_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetChannelDeliveriesForConversation.GetChannelDeliveriesForConversation(
                new ConversationId(Guid.NewGuid()), SiteId, OperatorId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }
}
