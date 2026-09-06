using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ResolveTeamMessageDelivery;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ResolveTeamMessageDelivery;

/// <summary>
/// `23-32`: "every operator of the site, including the sender" - see the handler's own remarks for
/// why that is the correct recipient list, unlike an ordinary conversation message's two participants.
/// </summary>
public class ResolveTeamMessageDeliveryTargetsHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly TeamMessageId MessageId = new(Guid.NewGuid());
    private static readonly OperatorId Sender = new(Guid.NewGuid());
    private static readonly OperatorId Colleague = new(Guid.NewGuid());

    private static (ResolveTeamMessageDeliveryTargetsHandler Handler, FakeTeamMessageReadStore ReadStore, FakeOperatorTeamReadStore Team, FakeNodeFanoutPublisher Fanout)
        CreateHandler()
    {
        var readStore = new FakeTeamMessageReadStore();
        var team = new FakeOperatorTeamReadStore();
        var fanout = new FakeNodeFanoutPublisher();
        var handler = new ResolveTeamMessageDeliveryTargetsHandler(readStore, team, fanout);
        return (handler, readStore, team, fanout);
    }

    [Fact]
    public async Task HandleAsync_PublishesToEveryOperatorOfTheSite_IncludingTheSender()
    {
        var (handler, readStore, team, fanout) = CreateHandler();
        readStore.Seed(SiteId, new TeamMessageHistoryItem(MessageId, 1, Sender, "Sender", null, false, "hi team", DateTimeOffset.UtcNow, null));
        team.Seed(
            SiteId,
            new OperatorTeamMemberItem(Sender, "Sender", null, HoldsSeat: true),
            new OperatorTeamMemberItem(Colleague, "Colleague", null, HoldsSeat: true));

        var correlationId = Guid.NewGuid();
        var result = await handler.HandleAsync(
            new ResolveTeamMessageDeliveryTargets(SiteId, 1, correlationId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var call = Assert.Single(fanout.Calls);
        Assert.Equal("TeamMessageReceived", call.Method);
        Assert.Equal(correlationId, call.CorrelationId);
        Assert.Equal(
            [PrincipalKeys.ForOperator(Sender), PrincipalKeys.ForOperator(Colleague)],
            call.Recipients);
    }

    [Fact]
    public async Task HandleAsync_WhenTheMessageDoesNotExist_ReturnsNotFound_WithoutPublishing()
    {
        var (handler, _, team, fanout) = CreateHandler();
        team.Seed(SiteId, new OperatorTeamMemberItem(Sender, "Sender", null, HoldsSeat: true));

        var result = await handler.HandleAsync(
            new ResolveTeamMessageDeliveryTargets(SiteId, 999, Guid.NewGuid()), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("TeamChat.NotFound", result.Error!.Value.Code);
        Assert.Empty(fanout.Calls);
    }
}
