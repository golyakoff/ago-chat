using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetVisitorHistory;
using Ago.Chat.Application.UseCases.GetVisitorSummary;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetVisitorSummary;

public class GetVisitorSummaryHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId AssignedOperatorId = new(Guid.NewGuid());
    private static readonly OperatorId OtherOperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FirstSeenAt = new(2025, 3, 14, 9, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        GetVisitorSummaryHandler Handler,
        GetVisitorHistoryHandler HistoryHandler,
        FakeConversationRepository Conversations,
        FakeConversationReadStore ReadStore,
        FakePermissionChecker Permissions,
        Conversation CurrentConversation);

    private static Fixture CreateFixture(bool grantConversationRead = true)
    {
        var conversations = new FakeConversationRepository();
        var readStore = new FakeConversationReadStore();
        var permissions = new FakePermissionChecker();
        var accessRecords = new FakeAccessRecordRepository();
        if (grantConversationRead)
        {
            permissions.Grant(AssignedOperatorId, SiteId, Permission.ConversationRead);
            permissions.Grant(OtherOperatorId, SiteId, Permission.ConversationRead);
        }

        readStore.SeedVisitorFirstSeenAt(VisitorId, FirstSeenAt);

        var current = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        // `25-221`: a brand-new conversation starts Pending, not Waiting - graduate it with the
        // visitor's own real first message before AssignTo, which still only accepts Waiting.
        current.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        current.AssignTo(AssignedOperatorId, Now);
        conversations.Seed(current);
        readStore.Seed(current);

        var handler = new GetVisitorSummaryHandler(conversations, readStore, permissions);
        var historyHandler = new GetVisitorHistoryHandler(
            conversations, readStore, permissions, accessRecords, new FakeClock(Now), new FakeIdGenerator());
        return new Fixture(handler, historyHandler, conversations, readStore, permissions, current);
    }

    /// <summary>`26-114`'s own Done-when: a widget-only visitor (no channel identity anywhere in this
    /// fixture - `adr/0182` removed that gate entirely) with several conversations gets every one of
    /// them through <see cref="GetVisitorHistoryHandler"/>'s own list, and
    /// <see cref="GetVisitorSummaryHandler"/>'s count agrees with it exactly: the list excludes the
    /// current conversation, the count includes it, so <c>count == list.Count + 1</c> - the same
    /// identity <c>Contracts.VisitorSummaryResponse</c>'s own remarks state.</summary>
    [Fact]
    public async Task HandleAsOperatorAsync_ForAWidgetOnlyVisitorWithMultipleConversations_CountMatchesTheHistoryListPlusTheCurrentOne()
    {
        var fixture = CreateFixture();

        var older = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now.AddDays(-2));
        older.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("older visit"), Now.AddDays(-2));
        older.Close(Now.AddDays(-2).AddHours(1));

        var newer = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now.AddDays(-1));
        newer.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("newer visit"), Now.AddDays(-1));
        newer.Close(Now.AddDays(-1).AddHours(1));

        fixture.Conversations.Seed(older);
        fixture.Conversations.Seed(newer);
        fixture.ReadStore.Seed(older);
        fixture.ReadStore.Seed(newer);

        var summaryResult = await fixture.Handler.HandleAsOperatorAsync(
            new Application.UseCases.GetVisitorSummary.GetVisitorSummary(fixture.CurrentConversation.Id, AssignedOperatorId, SiteId),
            CancellationToken.None);

        var historyResult = await fixture.HistoryHandler.HandleAsOperatorAsync(
            new Application.UseCases.GetVisitorHistory.GetVisitorHistory(
                fixture.CurrentConversation.Id, AssignedOperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(summaryResult.IsSuccess);
        Assert.True(historyResult.IsSuccess);
        Assert.Equal(FirstSeenAt, summaryResult.Value.VisitorFirstSeenAt);
        // The list ("this visitor's other conversations") excludes the current one; the count
        // ("this visitor's conversations, including the current one") does not - so it always reads
        // exactly one higher than the list's own length.
        Assert.Equal(2, historyResult.Value.Conversations.Count);
        Assert.Equal(3, summaryResult.Value.ConversationCount);
        Assert.Equal(historyResult.Value.Conversations.Count + 1, summaryResult.Value.ConversationCount);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_ForAVisitorWithOnlyTheCurrentConversation_ReturnsCountOne()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new Application.UseCases.GetVisitorSummary.GetVisitorSummary(fixture.CurrentConversation.Id, AssignedOperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value.ConversationCount);
        Assert.Equal(FirstSeenAt, result.Value.VisitorFirstSeenAt);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WhenTheOperatorIsNotAssignedToTheConversation_ReturnsForbidden_EvenThoughTheyHoldConversationReadAtTheSameSite()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new Application.UseCases.GetVisitorSummary.GetVisitorSummary(fixture.CurrentConversation.Id, OtherOperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WithoutConversationReadPermission_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantConversationRead: false);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new Application.UseCases.GetVisitorSummary.GetVisitorSummary(fixture.CurrentConversation.Id, AssignedOperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_ForAnUnknownConversation_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new Application.UseCases.GetVisitorSummary.GetVisitorSummary(new ConversationId(Guid.NewGuid()), AssignedOperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    // `24-10`: the same "anchor conversation blocked -> unreachable" rule GetVisitorHistoryHandlerTests
    // already proves for the list applies to this header read too - it sits above the same panel.
    [Fact]
    public async Task HandleAsOperatorAsync_WhenTheCurrentConversationIsBlocked_ReturnsNotFound()
    {
        var fixture = CreateFixture();
        fixture.CurrentConversation.MarkBlockedForTesting(new OperatorId(Guid.NewGuid()), Now);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new Application.UseCases.GetVisitorSummary.GetVisitorSummary(fixture.CurrentConversation.Id, AssignedOperatorId, SiteId),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }
}
