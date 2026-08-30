using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.RequestChannelLinkFromConsole;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.RequestChannelLinkFromConsole;

public class RequestChannelLinkFromConsoleHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());

    private sealed record Fixture(
        Application.UseCases.RequestChannelLinkFromConsole.RequestChannelLinkFromConsoleHandler Handler,
        FakeConversationRepository Conversations,
        FakePendingChannelLinkRequestRepository PendingLinks,
        FakePermissionChecker Permissions);

    private static Fixture CreateFixture(bool permitted = true, TimeSpan? validFor = null)
    {
        var conversations = new FakeConversationRepository();
        conversations.Seed(Conversation.Start(ConversationId, SiteId, VisitorId, Now));
        var pendingLinks = new FakePendingChannelLinkRequestRepository();
        var permissions = new FakePermissionChecker();
        if (permitted)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationSend);
        }

        var handler = new Application.UseCases.RequestChannelLinkFromConsole.RequestChannelLinkFromConsoleHandler(
            conversations, pendingLinks, new FakePendingChannelLinkCodeGenerator("482913"), permissions,
            new PendingChannelLinkRequestOptions { ValidFor = validFor ?? TimeSpan.FromMinutes(15) },
            new FakeIdGenerator(), new FakeClock(Now));
        return new Fixture(handler, conversations, pendingLinks, permissions);
    }

    private static Application.UseCases.RequestChannelLinkFromConsole.RequestChannelLinkFromConsole Command(string kind = "telegram") =>
        new(OperatorId, SiteId, ConversationId, kind);

    [Fact]
    public async Task HandleAsync_ARealChannelKind_ReturnsTheCodeAndCreatesALiveRequest()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command("telegram"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("482913", result.Value.Code);
        Assert.Equal(ChannelKind.Telegram, result.Value.Kind);
        Assert.Equal(Now + TimeSpan.FromMinutes(15), result.Value.ExpiresAt);
        var request = Assert.Single(fixture.PendingLinks.All);
        Assert.Equal(VisitorId, request.VisitorId);
        Assert.Equal(SiteId, request.SiteId);
        Assert.Equal(OperatorId, request.RequestedByOperatorId);
    }

    [Fact]
    public async Task HandleAsync_WithoutPermission_ReturnsForbidden_AndCreatesNothing()
    {
        var fixture = CreateFixture(permitted: false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.PendingLinks.All);
    }

    [Fact]
    public async Task HandleAsync_AnInvalidChannelKind_ReturnsInvalidKind_AndCreatesNothing()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command("carrier-pigeon"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ChannelLinkRequest.InvalidKind", result.Error!.Value.Code);
        Assert.Empty(fixture.PendingLinks.All);
    }

    /// <summary>Isolates the cross-tenant guard from the permission check ahead of it: the operator
    /// holds `ConversationSend` for the *other* site too, so a `Conversation.Forbidden` here would mean
    /// the mismatch was never actually checked - only `Conversation.NotFound` proves it was.</summary>
    [Fact]
    public async Task HandleAsync_AConversationOnADifferentSite_ReturnsNotFound()
    {
        var fixture = CreateFixture();
        var otherSite = new SiteId(Guid.NewGuid());
        fixture.Permissions.Grant(OperatorId, otherSite, Permission.ConversationSend);
        var command = new Application.UseCases.RequestChannelLinkFromConsole.RequestChannelLinkFromConsole(
            OperatorId, otherSite, ConversationId, "telegram");

        var result = await fixture.Handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_ANonExistentConversation_ReturnsNotFound()
    {
        var fixture = CreateFixture();
        var command = new Application.UseCases.RequestChannelLinkFromConsole.RequestChannelLinkFromConsole(
            OperatorId, SiteId, new ConversationId(Guid.NewGuid()), "telegram");

        var result = await fixture.Handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }
}
