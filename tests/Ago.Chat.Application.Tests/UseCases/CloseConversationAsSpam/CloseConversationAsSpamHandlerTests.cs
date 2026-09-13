using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.CloseConversationAsSpam;
using Microsoft.Extensions.Logging.Abstractions;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.CloseConversationAsSpam;

/// <summary>`23-69`'s own Done-when: "one act" that both closes and writes a time-windowed
/// <c>visitor_restrictions</c> row for the visitor - the same fixture shape <c>CloseConversationHandlerTests</c>
/// already establishes for the plain close this handler duplicates, extended with
/// <see cref="FakeVisitorRestrictionRepository"/>.</summary>
public class CloseConversationAsSpamHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MuteDuration = TimeSpan.FromHours(24);

    private sealed record Fixture(
        CloseConversationAsSpamHandler Handler,
        FakeConversationRepository Conversations,
        FakePermissionChecker Permissions,
        FakeVisitorRestrictionRepository Restrictions,
        FakeOutboxWriter Outbox,
        Conversation Conversation);

    private static Fixture CreateHandlerWithAssignedConversation(bool grantPermission = true)
    {
        var conversations = new FakeConversationRepository();
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        conversation.AssignTo(OperatorId, Now);
        conversation.ClearDomainEvents();
        conversations.Seed(conversation);

        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationMarkSpam);
        }

        var restrictions = new FakeVisitorRestrictionRepository();
        var outbox = new FakeOutboxWriter();
        var handler = new CloseConversationAsSpamHandler(
            conversations, new FakeConversationAssignmentLog(), restrictions, permissions, new FakeOperatorCapacity(), outbox,
            new FakeIdGenerator(), new FakeClock(Now), new ConversationSpamMuteOptions { DefaultDuration = MuteDuration },
            NullLogger<CloseConversationAsSpamHandler>.Instance);
        return new Fixture(handler, conversations, permissions, restrictions, outbox, conversation);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_ClosesTheConversation_AndMutesTheVisitorForTheConfiguredWindow()
    {
        var fixture = CreateHandlerWithAssignedConversation();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CloseConversationAsSpam.CloseConversationAsSpam(fixture.Conversation.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(Now.Add(MuteDuration), result.Value.MutedUntil);
        Assert.Equal(ConversationState.Closed, fixture.Conversation.State);
        Assert.Single(fixture.Outbox.Enqueued);

        Assert.True(await fixture.Restrictions.IsActiveAsync(SiteId, VisitorId, Now.AddMinutes(1), CancellationToken.None));
        var restriction = Assert.Single(fixture.Restrictions.Restrictions);
        Assert.Equal(VisitorRestrictionKind.Spam, restriction.Kind);
        Assert.Equal(Now.Add(MuteDuration), restriction.ExpiresAt);
    }

    [Fact]
    public async Task HandleAsync_WhenNotPermitted_ReturnsForbidden_ClosesNothing_MutesNobody()
    {
        var fixture = CreateHandlerWithAssignedConversation(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CloseConversationAsSpam.CloseConversationAsSpam(fixture.Conversation.Id, OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Equal(ConversationState.Assigned, fixture.Conversation.State);
        Assert.Empty(fixture.Restrictions.Restrictions);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorIsNotAssignedToThisConversation_ReturnsForbidden_MutesNobody()
    {
        var fixture = CreateHandlerWithAssignedConversation();
        var someoneElse = new OperatorId(Guid.NewGuid());
        fixture.Permissions.Grant(someoneElse, SiteId, Permission.ConversationMarkSpam);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.CloseConversationAsSpam.CloseConversationAsSpam(fixture.Conversation.Id, someoneElse, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.Restrictions.Restrictions);
    }

    [Fact]
    public async Task HandleAsync_WhenAlreadyClosed_ReturnsInvalidState_MutesNobodyASecondTime()
    {
        var fixture = CreateHandlerWithAssignedConversation();
        var command = new Application.UseCases.CloseConversationAsSpam.CloseConversationAsSpam(fixture.Conversation.Id, OperatorId, SiteId);
        Assert.True((await fixture.Handler.HandleAsync(command, CancellationToken.None)).IsSuccess);

        var result = await fixture.Handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.InvalidState", result.Error!.Value.Code);
        Assert.Single(fixture.Restrictions.Restrictions);
    }

    [Fact]
    public async Task HandleAsync_WhenConversationDoesNotExist_ReturnsNotFound()
    {
        var conversations = new FakeConversationRepository();
        var permissions = new FakePermissionChecker();
        permissions.Grant(OperatorId, SiteId, Permission.ConversationMarkSpam);
        var handler = new CloseConversationAsSpamHandler(
            conversations, new FakeConversationAssignmentLog(), new FakeVisitorRestrictionRepository(), permissions,
            new FakeOperatorCapacity(), new FakeOutboxWriter(), new FakeIdGenerator(), new FakeClock(Now),
            new ConversationSpamMuteOptions(), NullLogger<CloseConversationAsSpamHandler>.Instance);

        var result = await handler.HandleAsync(
            new Application.UseCases.CloseConversationAsSpam.CloseConversationAsSpam(new ConversationId(Guid.NewGuid()), OperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }
}
