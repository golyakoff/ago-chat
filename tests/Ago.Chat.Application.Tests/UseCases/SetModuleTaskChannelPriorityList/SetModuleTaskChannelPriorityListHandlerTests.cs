using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.SetModuleTaskChannelPriorityList;

/// <summary>
/// `20-11`: the write side of the per-booking priority list - the same "never an arbitrary id" and
/// permission/cross-tenant/cross-conversation guards `SetPreferredChannelIdentityHandlerTests` already
/// proves for `14-13`'s sibling, plus this item's own two new axes: scoping to
/// <see cref="Conversation.ActiveModuleTask"/> and whole-list replace semantics.
/// </summary>
public class SetModuleTaskChannelPriorityListHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());

    private sealed record Fixture(
        Application.UseCases.SetModuleTaskChannelPriorityList.SetModuleTaskChannelPriorityListHandler Handler,
        FakeChannelIdentityRepository Identities,
        FakeModuleTaskChannelPreferenceRepository Preferences,
        FakePermissionChecker Permissions,
        Conversation Conversation,
        ModuleTaskId? ActiveModuleTaskId);

    private static Fixture CreateFixture(bool permitted = true, bool assigned = true, bool withActiveTask = true)
    {
        var conversation = Conversation.Start(ConversationId, SiteId, VisitorId, Now);
        if (assigned)
        {
            conversation.AssignTo(OperatorId, Now);
        }

        ModuleTaskId? taskId = null;
        if (withActiveTask)
        {
            var task = conversation.StartModuleTask(
                new ModuleTaskId(Guid.NewGuid()), new ModuleKey("booking-flow"), "ext-task-1", Now, null, null, []);
            taskId = task.Id;
        }

        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);

        var identities = new FakeChannelIdentityRepository();
        var preferences = new FakeModuleTaskChannelPreferenceRepository();

        var permissions = new FakePermissionChecker();
        if (permitted)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationSend);
        }

        var handler = new Application.UseCases.SetModuleTaskChannelPriorityList.SetModuleTaskChannelPriorityListHandler(
            conversations, identities, preferences, permissions, new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, identities, preferences, permissions, conversation, taskId);
    }

    private static ChannelIdentity LinkIdentity(
        FakeChannelIdentityRepository identities, VisitorId visitorId, ChannelKind kind = ChannelKind.Telegram, string address = "tg-user-1")
    {
        var identity = ChannelIdentity.Link(new ChannelIdentityId(Guid.NewGuid()), SiteId, kind, new ExternalChannelAddress(address), visitorId, Now);
        identities.SaveAsync(identity, CancellationToken.None).GetAwaiter().GetResult();
        return identity;
    }

    /// <summary>The happy path: two of this visitor's own active identities, submitted in order, are
    /// stored with sequential 1-based priority matching that order.</summary>
    [Fact]
    public async Task HandleAsync_TwoActiveIdentitiesInOrder_StoresThemWithSequentialPriority()
    {
        var fixture = CreateFixture();
        var first = LinkIdentity(fixture.Identities, VisitorId, ChannelKind.Telegram, "tg-1");
        var second = LinkIdentity(fixture.Identities, VisitorId, ChannelKind.Max, "max-1");

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetModuleTaskChannelPriorityList.SetModuleTaskChannelPriorityList(
                OperatorId, SiteId, ConversationId, [first.Id, second.Id]),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var rows = await fixture.Preferences.ListForModuleTaskAsync(fixture.ActiveModuleTaskId!.Value, CancellationToken.None);
        Assert.Equal(2, rows.Count);
        Assert.Equal(first.Id, rows[0].ChannelIdentityId);
        Assert.Equal(1, rows[0].Priority);
        Assert.Equal(second.Id, rows[1].ChannelIdentityId);
        Assert.Equal(2, rows[1].Priority);
    }

    /// <summary>The fails-before test this item is named for: a channel identity that was never
    /// independently verified (no <see cref="ChannelIdentity"/> row exists for it at all) is refused a
    /// place in the list - "a visitor typing 'also message me here' is not evidence."</summary>
    [Fact]
    public async Task HandleAsync_ANonExistentChannelIdentity_IsRefused_AndNothingIsStored()
    {
        var fixture = CreateFixture();
        var real = LinkIdentity(fixture.Identities, VisitorId);
        var neverVerified = new ChannelIdentityId(Guid.NewGuid());

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetModuleTaskChannelPriorityList.SetModuleTaskChannelPriorityList(
                OperatorId, SiteId, ConversationId, [real.Id, neverVerified]),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ModuleTaskChannelPriority.ChannelNotEligible", result.Error!.Value.Code);
        Assert.Empty(await fixture.Preferences.ListForModuleTaskAsync(fixture.ActiveModuleTaskId!.Value, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_AnIdentityBelongingToADifferentVisitor_IsRefused()
    {
        var fixture = CreateFixture();
        var otherVisitorId = new VisitorId(Guid.NewGuid());
        var someoneElsesIdentity = LinkIdentity(fixture.Identities, otherVisitorId);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetModuleTaskChannelPriorityList.SetModuleTaskChannelPriorityList(
                OperatorId, SiteId, ConversationId, [someoneElsesIdentity.Id]),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ModuleTaskChannelPriority.ChannelNotEligible", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_AnUnlinkedIdentityOfThisVisitorsOwn_IsRefused()
    {
        var fixture = CreateFixture();
        var identity = LinkIdentity(fixture.Identities, VisitorId);
        identity.Unlink(Now.AddMinutes(1));
        await fixture.Identities.SaveAsync(identity, CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetModuleTaskChannelPriorityList.SetModuleTaskChannelPriorityList(
                OperatorId, SiteId, ConversationId, [identity.Id]),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ModuleTaskChannelPriority.ChannelNotEligible", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_TheSameIdentityListedTwice_IsRefused()
    {
        var fixture = CreateFixture();
        var identity = LinkIdentity(fixture.Identities, VisitorId);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetModuleTaskChannelPriorityList.SetModuleTaskChannelPriorityList(
                OperatorId, SiteId, ConversationId, [identity.Id, identity.Id]),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ModuleTaskChannelPriority.DuplicateEntry", result.Error!.Value.Code);
    }

    /// <summary>`20-11`'s own scoping choice: the list is keyed to the conversation's *active* module
    /// task - with none running, there is nothing to attach a priority list to.</summary>
    [Fact]
    public async Task HandleAsync_WithNoActiveModuleTask_ReturnsModuleTaskChannelPriorityNoActiveTask()
    {
        var fixture = CreateFixture(withActiveTask: false);
        var identity = LinkIdentity(fixture.Identities, VisitorId);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetModuleTaskChannelPriorityList.SetModuleTaskChannelPriorityList(
                OperatorId, SiteId, ConversationId, [identity.Id]),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("ModuleTaskChannelPriority.NoActiveTask", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_AnEmptyList_ClearsAnExistingList()
    {
        var fixture = CreateFixture();
        var identity = LinkIdentity(fixture.Identities, VisitorId);
        await fixture.Handler.HandleAsync(
            new Application.UseCases.SetModuleTaskChannelPriorityList.SetModuleTaskChannelPriorityList(
                OperatorId, SiteId, ConversationId, [identity.Id]),
            CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetModuleTaskChannelPriorityList.SetModuleTaskChannelPriorityList(
                OperatorId, SiteId, ConversationId, []),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(await fixture.Preferences.ListForModuleTaskAsync(fixture.ActiveModuleTaskId!.Value, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_WithoutPermission_ReturnsForbidden()
    {
        var fixture = CreateFixture(permitted: false);
        var identity = LinkIdentity(fixture.Identities, VisitorId);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetModuleTaskChannelPriorityList.SetModuleTaskChannelPriorityList(
                OperatorId, SiteId, ConversationId, [identity.Id]),
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
            new Application.UseCases.SetModuleTaskChannelPriorityList.SetModuleTaskChannelPriorityList(
                OperatorId, SiteId, ConversationId, [identity.Id]),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_ANonExistentConversation_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.SetModuleTaskChannelPriorityList.SetModuleTaskChannelPriorityList(
                OperatorId, SiteId, new ConversationId(Guid.NewGuid()), []),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }
}
