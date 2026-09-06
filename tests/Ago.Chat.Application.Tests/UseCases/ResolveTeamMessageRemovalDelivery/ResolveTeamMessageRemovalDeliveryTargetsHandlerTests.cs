using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Realtime;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.ResolveTeamMessageRemovalDelivery;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.ResolveTeamMessageRemovalDelivery;

/// <summary>
/// `23-33`: the removal fan-out's own recipient list - the direct proof that a tombstone reaches
/// every operator of the site, not only the one who removed the message, the same "every operator,
/// including the sender" shape `ResolveTeamMessageDeliveryTargetsHandlerTests` already proves for an
/// ordinary post. Pushed under `TeamMessageRemoved`, never `TeamMessageReceived` - see
/// `Ago.Chat.Contracts.TeamMessageRemoved`'s own remarks for why reusing the post event's push method
/// would silently vanish behind the console's own transport-level dedup.
/// </summary>
public class ResolveTeamMessageRemovalDeliveryTargetsHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly TeamMessageId MessageId = new(Guid.NewGuid());
    private static readonly OperatorId Author = new(Guid.NewGuid());
    private static readonly OperatorId Remover = new(Guid.NewGuid());
    private static readonly OperatorId Bystander = new(Guid.NewGuid());

    private static (ResolveTeamMessageRemovalDeliveryTargetsHandler Handler, FakeTeamMessageReadStore ReadStore, FakeOperatorTeamReadStore Team, FakeNodeFanoutPublisher Fanout)
        CreateHandler()
    {
        var readStore = new FakeTeamMessageReadStore();
        var team = new FakeOperatorTeamReadStore();
        var fanout = new FakeNodeFanoutPublisher();
        var handler = new ResolveTeamMessageRemovalDeliveryTargetsHandler(readStore, team, fanout);
        return (handler, readStore, team, fanout);
    }

    [Fact]
    public async Task HandleAsync_PublishesTheTombstoneToEveryOperatorOfTheSite_IncludingABystanderWhoNeverRemovedAnything()
    {
        var (handler, readStore, team, fanout) = CreateHandler();
        // The read side's own redaction already applied - Body null, RemovedAt set - exactly what
        // TeamMessageReadStore hands back once a message is removed (its own remarks).
        readStore.Seed(
            SiteId,
            new TeamMessageHistoryItem(
                MessageId, 1, Author, "Author", null, false, Body: null, DateTimeOffset.UtcNow, null,
                RemovedAt: DateTimeOffset.UtcNow));
        team.Seed(
            SiteId,
            new OperatorTeamMemberItem(Author, "Author", null, HoldsSeat: true),
            new OperatorTeamMemberItem(Remover, "Remover", null, HoldsSeat: true),
            new OperatorTeamMemberItem(Bystander, "Bystander", null, HoldsSeat: true));

        var correlationId = Guid.NewGuid();
        var result = await handler.HandleAsync(
            new ResolveTeamMessageRemovalDeliveryTargets(SiteId, 1, correlationId), CancellationToken.None);

        Assert.True(result.IsSuccess);
        var call = Assert.Single(fanout.Calls);
        Assert.Equal("TeamMessageRemoved", call.Method);
        Assert.Equal(correlationId, call.CorrelationId);
        // The direct proof this item's own brief asks for: the push reaches every connected operator
        // of the site - here, a bystander who took no part in the removal at all - not only the
        // remover.
        Assert.Equal(
            [PrincipalKeys.ForOperator(Author), PrincipalKeys.ForOperator(Remover), PrincipalKeys.ForOperator(Bystander)],
            call.Recipients);
        Assert.Contains(PrincipalKeys.ForOperator(Bystander), call.Recipients);
    }

    [Fact]
    public async Task HandleAsync_WhenTheMessageDoesNotExist_ReturnsNotFound_WithoutPublishing()
    {
        var (handler, _, team, fanout) = CreateHandler();
        team.Seed(SiteId, new OperatorTeamMemberItem(Author, "Author", null, HoldsSeat: true));

        var result = await handler.HandleAsync(
            new ResolveTeamMessageRemovalDeliveryTargets(SiteId, 999, Guid.NewGuid()), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("TeamChat.NotFound", result.Error!.Value.Code);
        Assert.Empty(fanout.Calls);
    }
}
