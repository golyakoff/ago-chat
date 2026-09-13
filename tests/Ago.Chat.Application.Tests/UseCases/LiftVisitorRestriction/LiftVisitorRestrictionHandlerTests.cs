using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.LiftVisitorRestriction;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.LiftVisitorRestriction;

/// <summary>Both items' own reversibility requirement, and the per-kind permission split
/// <see cref="LiftVisitorRestrictionHandler"/>'s own remarks explain in full.</summary>
public class LiftVisitorRestrictionHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsync_WhenAVisitorCarriesAnActiveSpamMute_AndTheCallerHoldsConversationMarkSpam_Lifts()
    {
        var restrictions = new FakeVisitorRestrictionRepository();
        await restrictions.RestrictAsync(
            SiteId, VisitorId, new OperatorId(Guid.NewGuid()), VisitorRestrictionKind.Spam, Now.AddHours(24),
            new ConversationId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationMarkSpam);
        var handler = new LiftVisitorRestrictionHandler(restrictions, permissions, new FakeClock(Now.AddMinutes(1)));

        var result = await handler.HandleAsync(
            new Application.UseCases.LiftVisitorRestriction.LiftVisitorRestriction(VisitorId, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(await restrictions.IsActiveAsync(SiteId, VisitorId, Now.AddMinutes(2), CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_WhenAVisitorCarriesAnActiveSpamMute_ButTheCallerOnlyHoldsConversationBlock_Forbidden()
    {
        // `24-10`'s own permission does not automatically cover lifting a mute it never created - the
        // handler's own per-kind gate, not a hierarchy.
        var restrictions = new FakeVisitorRestrictionRepository();
        await restrictions.RestrictAsync(
            SiteId, VisitorId, new OperatorId(Guid.NewGuid()), VisitorRestrictionKind.Spam, Now.AddHours(24),
            new ConversationId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationBlock);
        var handler = new LiftVisitorRestrictionHandler(restrictions, permissions, new FakeClock(Now.AddMinutes(1)));

        var result = await handler.HandleAsync(
            new Application.UseCases.LiftVisitorRestriction.LiftVisitorRestriction(VisitorId, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.True(await restrictions.IsActiveAsync(SiteId, VisitorId, Now.AddMinutes(2), CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_WhenAVisitorCarriesAnIndefiniteBlock_AndTheCallerHoldsConversationBlock_Lifts()
    {
        var restrictions = new FakeVisitorRestrictionRepository();
        await restrictions.RestrictAsync(
            SiteId, VisitorId, new OperatorId(Guid.NewGuid()), VisitorRestrictionKind.Block, expiresAt: null,
            new ConversationId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationBlock);
        var handler = new LiftVisitorRestrictionHandler(restrictions, permissions, new FakeClock(Now.AddMinutes(1)));

        var result = await handler.HandleAsync(
            new Application.UseCases.LiftVisitorRestriction.LiftVisitorRestriction(VisitorId, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(await restrictions.IsActiveAsync(SiteId, VisitorId, Now.AddYears(50), CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_WhenTheVisitorIsNotCurrentlyRestricted_ReturnsVisitorNotRestricted()
    {
        var restrictions = new FakeVisitorRestrictionRepository();
        var permissions = new FakePermissionChecker();
        var handler = new LiftVisitorRestrictionHandler(restrictions, permissions, new FakeClock(Now));

        var result = await handler.HandleAsync(
            new Application.UseCases.LiftVisitorRestriction.LiftVisitorRestriction(VisitorId, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Visitor.NotRestricted", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenARestrictionAlreadyNaturallyExpired_ReturnsVisitorNotRestricted()
    {
        var restrictions = new FakeVisitorRestrictionRepository();
        await restrictions.RestrictAsync(
            SiteId, VisitorId, new OperatorId(Guid.NewGuid()), VisitorRestrictionKind.Spam, Now.AddHours(24),
            new ConversationId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationMarkSpam);
        var handler = new LiftVisitorRestrictionHandler(restrictions, permissions, new FakeClock(Now.AddHours(25)));

        var result = await handler.HandleAsync(
            new Application.UseCases.LiftVisitorRestriction.LiftVisitorRestriction(VisitorId, OperatorId, SiteId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Visitor.NotRestricted", result.Error!.Value.Code);
    }
}
