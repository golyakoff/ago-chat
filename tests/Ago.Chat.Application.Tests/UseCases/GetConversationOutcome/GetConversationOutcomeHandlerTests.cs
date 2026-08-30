using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetConversationOutcome;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetConversationOutcome;

public class GetConversationOutcomeHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(GetConversationOutcomeHandler Handler, ConversationId ConversationId);

    private static Fixture CreateFixture(ConversationOutcome outcome, bool grantPermission = true)
    {
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        if (outcome != ConversationOutcome.Unset)
        {
            conversation.SetOutcome(outcome);
        }

        var readStore = new FakeConversationReadStore();
        readStore.Seed(conversation);

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        }

        return new Fixture(new GetConversationOutcomeHandler(readStore, permissions), conversation.Id);
    }

    [Fact]
    public async Task HandleAsync_ANewConversation_ReturnsUnset()
    {
        var fixture = CreateFixture(ConversationOutcome.Unset);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetConversationOutcome.GetConversationOutcome(fixture.ConversationId, SiteId, OperatorId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Unset", result.Value);
    }

    [Fact]
    public async Task HandleAsync_AConversationWithARecordedOutcome_ReturnsIt()
    {
        var fixture = CreateFixture(ConversationOutcome.Converted);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetConversationOutcome.GetConversationOutcome(fixture.ConversationId, SiteId, OperatorId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("Converted", result.Value);
    }

    [Fact]
    public async Task HandleAsync_OperatorWithoutPermission_ReturnsForbidden()
    {
        var fixture = CreateFixture(ConversationOutcome.Unset, grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetConversationOutcome.GetConversationOutcome(fixture.ConversationId, SiteId, OperatorId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_UnknownConversation_ReturnsNotFound()
    {
        var fixture = CreateFixture(ConversationOutcome.Unset);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetConversationOutcome.GetConversationOutcome(new ConversationId(Guid.NewGuid()), SiteId, OperatorId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }
}
