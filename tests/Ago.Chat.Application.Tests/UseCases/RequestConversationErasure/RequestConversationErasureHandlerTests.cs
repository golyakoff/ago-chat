using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RequestConversationErasure;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RequestConversationErasure;

public class RequestConversationErasureHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly SiteId OtherSiteId = new(Guid.NewGuid());
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(RequestConversationErasureHandler Handler, FakeErasureRequestRepository Erasures);

    private static Fixture CreateFixture(bool grantPermission = true, bool seedConversation = true)
    {
        var erasures = new FakeErasureRequestRepository();
        if (seedConversation)
        {
            erasures.SeedConversation(ConversationId, SiteId);
        }

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationErase);
        }

        var handler = new RequestConversationErasureHandler(erasures, permissions, new FakeIdGenerator(), new FakeClock(Now));
        return new Fixture(handler, erasures);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_SetsTheErasureRequestedAtFlag()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RequestConversationErasure.RequestConversationErasure(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now, fixture.Erasures.ConversationErasureRequestedAt[ConversationId]);
    }

    // `24-13`: the receipt this erasure will be provable by - minted the same call that sets the
    // flag, never a second one (IErasureRequestRepository's own remarks on why one statement).
    [Fact]
    public async Task HandleAsync_WhenPermitted_MintsAnErasureReceiptForTheRequestingOperator()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(
            new Application.UseCases.RequestConversationErasure.RequestConversationErasure(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.Equal(OperatorId, fixture.Erasures.ConversationErasureRequestedBy[ConversationId]);
        Assert.NotEqual(Guid.Empty, fixture.Erasures.ConversationErasureRecordIds[ConversationId]);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksConversationErase_ReturnsForbidden_AndSetsNoFlag()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RequestConversationErasure.RequestConversationErasure(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.Erasures.ConversationErasureRequestedAt);
    }

    [Fact]
    public async Task HandleAsync_WhenTheConversationDoesNotExist_ReturnsNotFound()
    {
        var fixture = CreateFixture(seedConversation: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.RequestConversationErasure.RequestConversationErasure(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    // The cross-tenant guard IErasureRequestRepository's own remarks describe: a conversation that
    // exists but belongs to a different site answers the identical NotFound a genuinely missing one
    // would, never Forbidden - existence is not leaked cross-tenant.
    [Fact]
    public async Task HandleAsync_WhenTheConversationBelongsToADifferentSite_ReturnsNotFound_NotForbidden()
    {
        var erasures = new FakeErasureRequestRepository();
        erasures.SeedConversation(ConversationId, OtherSiteId);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationErase);
        var handler = new RequestConversationErasureHandler(erasures, permissions, new FakeIdGenerator(), new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.RequestConversationErasure.RequestConversationErasure(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }
}
