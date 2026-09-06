using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetTeamMessageHistory;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetTeamMessageHistory;

public class GetTeamMessageHistoryHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly SiteId OtherSiteId = new(Guid.NewGuid());
    private static readonly OperatorId Author = new(Guid.NewGuid());

    private static TeamMessageHistoryItem Item(int sequence, string body) =>
        new(new TeamMessageId(Guid.NewGuid()), sequence, Author, "Author", null, false, body, DateTimeOffset.UtcNow, null);

    [Fact]
    public async Task HandleAsync_ReturnsOnlyTheCallersOwnSitesMessages()
    {
        var readStore = new FakeTeamMessageReadStore();
        readStore.Seed(SiteId, Item(1, "ours"));
        readStore.Seed(OtherSiteId, Item(1, "theirs"));
        var handler = new GetTeamMessageHistoryHandler(readStore);

        var page = await handler.HandleAsync(new Application.UseCases.GetTeamMessageHistory.GetTeamMessageHistory(SiteId, null, 50), CancellationToken.None);

        var item = Assert.Single(page.Messages);
        Assert.Equal("ours", item.Body);
    }

    [Fact]
    public async Task HandleDeltaAsync_ReturnsOnlyMessagesAfterTheGivenSequence()
    {
        var readStore = new FakeTeamMessageReadStore();
        readStore.Seed(SiteId, Item(1, "old"), Item(2, "new"));
        var handler = new GetTeamMessageHistoryHandler(readStore);

        var delta = await handler.HandleDeltaAsync(new GetTeamMessageDelta(SiteId, 1), CancellationToken.None);

        var item = Assert.Single(delta);
        Assert.Equal("new", item.Body);
    }
}
