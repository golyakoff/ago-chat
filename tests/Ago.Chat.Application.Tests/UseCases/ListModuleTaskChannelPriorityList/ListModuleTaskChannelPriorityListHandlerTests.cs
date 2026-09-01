using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ListModuleTaskChannelPriorityList;

/// <summary>`20-11`: the read side - "the priority order a visitor sets is stored and retrievable."</summary>
public class ListModuleTaskChannelPriorityListHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly ConversationId ConversationId = new(Guid.NewGuid());

    [Fact]
    public async Task HandleAsync_AStoredList_IsReturnedInPriorityOrder()
    {
        var conversation = Conversation.Start(ConversationId, SiteId, VisitorId, Now);
        conversation.AssignTo(OperatorId, Now);
        var task = conversation.StartModuleTask(new ModuleTaskId(Guid.NewGuid()), new ModuleKey("booking-flow"), "ext-1", Now, null, null, []);

        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);

        var identities = new FakeChannelIdentityRepository();
        var first = ChannelIdentity.Link(new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Telegram, new ExternalChannelAddress("tg-1"), VisitorId, Now);
        var second = ChannelIdentity.Link(new ChannelIdentityId(Guid.NewGuid()), SiteId, ChannelKind.Max, new ExternalChannelAddress("max-1"), VisitorId, Now);
        await identities.SaveAsync(first, CancellationToken.None);
        await identities.SaveAsync(second, CancellationToken.None);

        var preferences = new FakeModuleTaskChannelPreferenceRepository();
        preferences.Seed(ModuleTaskChannelPreference.Add(
            new ModuleTaskChannelPreferenceId(Guid.NewGuid()), SiteId, task.Id, VisitorId, second.Id, priority: 1, Now));
        preferences.Seed(ModuleTaskChannelPreference.Add(
            new ModuleTaskChannelPreferenceId(Guid.NewGuid()), SiteId, task.Id, VisitorId, first.Id, priority: 2, Now));

        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);

        var handler = new Application.UseCases.ListModuleTaskChannelPriorityList.ListModuleTaskChannelPriorityListHandler(
            conversations, preferences, identities, permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.ListModuleTaskChannelPriorityList.ListModuleTaskChannelPriorityList(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Count);
        Assert.Equal(second.Id.Value, result.Value[0].ChannelIdentityId);
        Assert.Equal(1, result.Value[0].Priority);
        Assert.Equal(first.Id.Value, result.Value[1].ChannelIdentityId);
        Assert.Equal(2, result.Value[1].Priority);
    }

    [Fact]
    public async Task HandleAsync_NoActiveModuleTask_ReturnsAnEmptyList()
    {
        var conversation = Conversation.Start(ConversationId, SiteId, VisitorId, Now);
        conversation.AssignTo(OperatorId, Now);

        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);

        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);

        var handler = new Application.UseCases.ListModuleTaskChannelPriorityList.ListModuleTaskChannelPriorityListHandler(
            conversations, new FakeModuleTaskChannelPreferenceRepository(), new FakeChannelIdentityRepository(), permissions);

        var result = await handler.HandleAsync(
            new Application.UseCases.ListModuleTaskChannelPriorityList.ListModuleTaskChannelPriorityList(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task HandleAsync_WithoutPermission_ReturnsForbidden()
    {
        var conversation = Conversation.Start(ConversationId, SiteId, VisitorId, Now);
        conversation.AssignTo(OperatorId, Now);
        var conversations = new FakeConversationRepository();
        conversations.Seed(conversation);

        var handler = new Application.UseCases.ListModuleTaskChannelPriorityList.ListModuleTaskChannelPriorityListHandler(
            conversations, new FakeModuleTaskChannelPreferenceRepository(), new FakeChannelIdentityRepository(), new FakePermissionChecker());

        var result = await handler.HandleAsync(
            new Application.UseCases.ListModuleTaskChannelPriorityList.ListModuleTaskChannelPriorityList(ConversationId, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }
}
