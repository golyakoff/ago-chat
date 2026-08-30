using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.SetPreferredChannelIdentity;

/// <summary>
/// `14-13`/`adr/0079` decision 5: the one invariant this item is named for - "never an arbitrary id" -
/// plus the ordinary permission/cross-tenant/cross-conversation guards every sibling handler in this
/// ADR (`RequestChannelLinkFromConsoleHandler`, `ListChannelIdentitiesForVisitorHandler`) already proves
/// for itself. See <see cref="Application.UseCases.SetPreferredChannelIdentity.SetPreferredChannelIdentityHandler"/>'s
/// own remarks.
/// </summary>
public class SetPreferredChannelIdentityHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());

    private sealed record Fixture(
        Application.UseCases.SetPreferredChannelIdentity.SetPreferredChannelIdentityHandler Handler,
        FakeChannelIdentityRepository Identities,
        FakeVisitorRepository Visitors,
        FakePermissionChecker Permissions,
        Conversation Conversation);

    private static Fixture CreateFixture(bool permitted = true, bool assigned = true)
    {
        var conversation = Conversation.Start(ConversationId, SiteId, VisitorId, Now);
        if (assigned)
        {
            conversation.AssignTo(OperatorId, Now);
        }

        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);

        var identities = new FakeChannelIdentityRepository();
        var visitors = new FakeVisitorRepository();
        visitors.Seed(new Visitor(VisitorId, SiteId, Now));

        var permissions = new FakePermissionChecker();
        if (permitted)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationSend);
        }

        var handler = new Application.UseCases.SetPreferredChannelIdentity.SetPreferredChannelIdentityHandler(
            conversations, identities, visitors, permissions);
        return new Fixture(handler, identities, visitors, permissions, conversation);
    }

    private static ChannelIdentity LinkIdentity(FakeChannelIdentityRepository identities, VisitorId visitorId, ChannelKind kind = ChannelKind.Telegram, string address = "tg-user-1")
    {
        var identity = ChannelIdentity.Link(new ChannelIdentityId(Guid.NewGuid()), SiteId, kind, new ExternalChannelAddress(address), visitorId, Now);
        identities.SaveAsync(identity, CancellationToken.None).GetAwaiter().GetResult();
        return identity;
    }

    /// <summary>The fails-before test this item is named for: an id that is real, active, and verified -
    /// just not for *this* visitor - must be refused exactly like one that never existed at all.</summary>
    [Fact]
    public async Task HandleAsync_AnIdentityBelongingToADifferentVisitor_IsRefused()
    {
        var fixture = CreateFixture();
        var otherVisitorId = new VisitorId(Guid.NewGuid());
        var someoneElsesIdentity = LinkIdentity(fixture.Identities, otherVisitorId);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetPreferredChannelIdentity.SetPreferredChannelIdentity(
                OperatorId, SiteId, ConversationId, someoneElsesIdentity.Id),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ChannelIdentity.NotEligibleForPreference", result.Error!.Value.Code);
        var visitor = await fixture.Visitors.GetByIdAsync(VisitorId, CancellationToken.None);
        Assert.Null(visitor!.PreferredChannelIdentityId);
    }

    [Fact]
    public async Task HandleAsync_OneOfThisVisitorsOwnActiveIdentities_SetsThePreference()
    {
        var fixture = CreateFixture();
        var identity = LinkIdentity(fixture.Identities, VisitorId);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetPreferredChannelIdentity.SetPreferredChannelIdentity(
                OperatorId, SiteId, ConversationId, identity.Id),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var visitor = await fixture.Visitors.GetByIdAsync(VisitorId, CancellationToken.None);
        Assert.Equal(identity.Id, visitor!.PreferredChannelIdentityId);
    }

    [Fact]
    public async Task HandleAsync_WithNoId_ClearsAnExistingPreference()
    {
        var fixture = CreateFixture();
        var identity = LinkIdentity(fixture.Identities, VisitorId);
        await fixture.Handler.HandleAsync(
            new Application.UseCases.SetPreferredChannelIdentity.SetPreferredChannelIdentity(
                OperatorId, SiteId, ConversationId, identity.Id),
            CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetPreferredChannelIdentity.SetPreferredChannelIdentity(
                OperatorId, SiteId, ConversationId, null),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var visitor = await fixture.Visitors.GetByIdAsync(VisitorId, CancellationToken.None);
        Assert.Null(visitor!.PreferredChannelIdentityId);
    }

    [Fact]
    public async Task HandleAsync_AnUnlinkedIdentityOfThisVisitorsOwn_IsRefused()
    {
        var fixture = CreateFixture();
        var identity = LinkIdentity(fixture.Identities, VisitorId);
        identity.Unlink(Now.AddMinutes(1));
        await fixture.Identities.SaveAsync(identity, CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetPreferredChannelIdentity.SetPreferredChannelIdentity(
                OperatorId, SiteId, ConversationId, identity.Id),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ChannelIdentity.NotEligibleForPreference", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_ANonExistentIdentity_IsRefused()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetPreferredChannelIdentity.SetPreferredChannelIdentity(
                OperatorId, SiteId, ConversationId, new ChannelIdentityId(Guid.NewGuid())),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ChannelIdentity.NotEligibleForPreference", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WithoutPermission_ReturnsForbidden()
    {
        var fixture = CreateFixture(permitted: false);
        var identity = LinkIdentity(fixture.Identities, VisitorId);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetPreferredChannelIdentity.SetPreferredChannelIdentity(
                OperatorId, SiteId, ConversationId, identity.Id),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_AnOperatorNotAssignedToTheConversation_ReturnsForbidden()
    {
        var fixture = CreateFixture(assigned: false);
        var identity = LinkIdentity(fixture.Identities, VisitorId);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetPreferredChannelIdentity.SetPreferredChannelIdentity(
                OperatorId, SiteId, ConversationId, identity.Id),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_ANonExistentConversation_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetPreferredChannelIdentity.SetPreferredChannelIdentity(
                OperatorId, SiteId, new ConversationId(Guid.NewGuid()), null),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    /// <summary>An identity that exists, belongs to this visitor, and is active - but the *conversation*
    /// named belongs to a different site than the caller's own. The same "wrong tenant reads like no
    /// such row" info-hiding shape every cross-tenant guard in this ADR already uses.</summary>
    [Fact]
    public async Task HandleAsync_AConversationOnADifferentSite_ReturnsNotFound()
    {
        var fixture = CreateFixture();
        var identity = LinkIdentity(fixture.Identities, VisitorId);
        var otherSite = new SiteId(Guid.NewGuid());
        fixture.Permissions.Grant(OperatorId, otherSite, Permission.ConversationSend);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetPreferredChannelIdentity.SetPreferredChannelIdentity(
                OperatorId, otherSite, ConversationId, identity.Id),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }
}
