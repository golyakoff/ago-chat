using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetVisitorHistory;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.GetVisitorHistory;

public class GetVisitorHistoryHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly VisitorId VisitorId = new(Guid.NewGuid());
    private static readonly OperatorId AssignedOperatorId = new(Guid.NewGuid());
    private static readonly OperatorId OtherOperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed record Fixture(
        GetVisitorHistoryHandler Handler,
        FakeConversationRepository Conversations,
        FakeConversationReadStore ReadStore,
        FakePermissionChecker Permissions,
        FakeAccessRecordRepository AccessRecords,
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

        var current = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now);
        // `25-221`: a brand-new conversation starts Pending, not Waiting - graduate it with the
        // visitor's own real first message before AssignTo, which still only accepts Waiting.
        current.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("hi"), Now);
        current.AssignTo(AssignedOperatorId, Now);
        conversations.Seed(current);
        readStore.Seed(current);

        var handler = new GetVisitorHistoryHandler(
            conversations, readStore, permissions, accessRecords, new FakeClock(Now), new FakeIdGenerator());
        return new Fixture(handler, conversations, readStore, permissions, accessRecords, current);
    }

    /// <summary>`26-114`/`adr/0182`: no channel identity is linked anywhere in this file any more - the
    /// whole point of the widening this item makes is that <see cref="GetVisitorHistoryHandler.HandleAsOperatorAsync"/>
    /// no longer cares whether one exists. Every test below exercises a widget-only visitor by
    /// construction.</summary>
    [Fact]
    public async Task HandleAsOperatorAsync_ForAWidgetOnlyVisitorWithNoChannelIdentity_ReturnsPriorConversations_MostRecentFirst_ExcludingTheCurrentOne()
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

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new Application.UseCases.GetVisitorHistory.GetVisitorHistory(
                fixture.CurrentConversation.Id, AssignedOperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(result.Value.Conversations, c => c.ConversationId == fixture.CurrentConversation.Id.Value);
        // The fake's own "most recent first" ordering sorts by raw conversation-id byte order, the
        // same cursor production's real uuid v7 ids give for free (IIdGenerator's own remarks) - the
        // test double's ids come from a plain Guid.NewGuid() (FakeIdGenerator, deliberately not
        // time-ordered), so asserting a specific order here would be testing the fake's random id
        // generator, not this handler. The real "newest first" guarantee over real, time-ordered ids
        // is ConversationReadStoreTests.GetVisitorHistoryAsync_ReturnsConversationsNewestFirst's job,
        // against a real Postgres. Here: both priors came back, and only both.
        Assert.Equal(2, result.Value.Conversations.Count);
        Assert.Contains(result.Value.Conversations, c => c.ConversationId == newer.Id.Value);
        Assert.Contains(result.Value.Conversations, c => c.ConversationId == older.Id.Value);
    }

    /// <summary>`26-114`/`adr/0182`: the case the old channel-identity gate used to refuse outright -
    /// this widget-only visitor now gets an ordinary, empty-but-real list, the same "nothing to show
    /// yet" shape a channel-identified visitor with no priors already got before this item.</summary>
    [Fact]
    public async Task HandleAsOperatorAsync_ForAWidgetOnlyVisitorWithNoPriorConversations_ReturnsAnEmptyList()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new Application.UseCases.GetVisitorHistory.GetVisitorHistory(
                fixture.CurrentConversation.Id, AssignedOperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Conversations);
        Assert.Null(result.Value.NextBeforeId);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WhenTheOperatorIsNotAssignedToTheConversation_ReturnsForbidden_EvenThoughTheyHoldConversationReadAtTheSameSite()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new Application.UseCases.GetVisitorHistory.GetVisitorHistory(
                fixture.CurrentConversation.Id, OtherOperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_WithoutConversationReadPermission_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantConversationRead: false);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new Application.UseCases.GetVisitorHistory.GetVisitorHistory(
                fixture.CurrentConversation.Id, AssignedOperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsOperatorAsync_ForAnUnknownConversation_ReturnsNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new Application.UseCases.GetVisitorHistory.GetVisitorHistory(
                new ConversationId(Guid.NewGuid()), AssignedOperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleHistoricalConversationAsOperatorAsync_ForAPastConversationOfTheSameVisitor_ReturnsItsRealHistory_EvenThoughADifferentOperatorOriginallyHeldIt()
    {
        var fixture = CreateFixture();
        var historical = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now.AddDays(-1));
        // `25-221`: a real visitor message must exist before AssignTo is legal at all - this
        // conversation's own real history is now this graduating message plus the operator's reply,
        // not the operator's reply alone.
        historical.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("anyone there?"), Now.AddDays(-1));
        historical.AssignTo(OtherOperatorId, Now.AddDays(-1));
        historical.AddOperatorMessage(OtherOperatorId, new MessageId(Guid.NewGuid()), new MessageBody("handled by someone else"), Now.AddDays(-1));
        historical.Close(Now.AddDays(-1).AddHours(1));
        fixture.Conversations.Seed(historical);
        fixture.ReadStore.Seed(historical);

        var result = await fixture.Handler.HandleHistoricalConversationAsOperatorAsync(
            new Application.UseCases.GetVisitorHistory.GetVisitorHistoryConversation(
                fixture.CurrentConversation.Id, historical.Id, AssignedOperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value.Messages.Count);
        Assert.Contains(result.Value.Messages, m => m.Body == "handled by someone else");

        // `24-12`'s own Done-when: this boundary-crossing read leaves exactly one access_records row,
        // naming the operator who read it, the historical conversation that was opened, and the site -
        // never a copy of the message body it just returned.
        var recorded = Assert.Single(fixture.AccessRecords.Recorded);
        Assert.Equal(AccessRecordKind.CrossConversationHistoryRead, recorded.AccessKind);
        Assert.Equal(SiteId, recorded.SiteId);
        Assert.Equal(AccessRecordActorKind.Operator, recorded.ActorKind);
        Assert.Equal(AssignedOperatorId.Value.ToString(), recorded.ActorId);
        Assert.Equal(AccessRecordResourceKind.Conversation, recorded.ResourceKind);
        Assert.Equal(historical.Id.Value, recorded.ResourceId);
    }

    [Fact]
    public async Task HandleHistoricalConversationAsOperatorAsync_ForAConversationOfADifferentVisitor_ReturnsForbidden_AndRecordsNothing()
    {
        var fixture = CreateFixture();
        var otherVisitorConversation = Conversation.Start(
            new ConversationId(Guid.NewGuid()), SiteId, new VisitorId(Guid.NewGuid()), Now.AddDays(-1));
        otherVisitorConversation.AddVisitorMessage(
            otherVisitorConversation.VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("not this visitor"), Now.AddDays(-1));
        fixture.Conversations.Seed(otherVisitorConversation);
        fixture.ReadStore.Seed(otherVisitorConversation);

        var result = await fixture.Handler.HandleHistoricalConversationAsOperatorAsync(
            new Application.UseCases.GetVisitorHistory.GetVisitorHistoryConversation(
                fixture.CurrentConversation.Id, otherVisitorConversation.Id, AssignedOperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        // `24-12`'s own "a read that fails authorisation is not an access" - nothing was read, so
        // nothing is recorded.
        Assert.Empty(fixture.AccessRecords.Recorded);
    }

    [Fact]
    public async Task HandleHistoricalConversationAsOperatorAsync_WhenTheCallerIsNotAssignedToTheirOwnStandingConversation_ReturnsForbidden_AndRecordsNothing()
    {
        var fixture = CreateFixture();
        var historical = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now.AddDays(-1));
        historical.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("past message"), Now.AddDays(-1));
        fixture.Conversations.Seed(historical);
        fixture.ReadStore.Seed(historical);

        var result = await fixture.Handler.HandleHistoricalConversationAsOperatorAsync(
            new Application.UseCases.GetVisitorHistory.GetVisitorHistoryConversation(
                fixture.CurrentConversation.Id, historical.Id, OtherOperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
        Assert.Empty(fixture.AccessRecords.Recorded);
    }

    [Fact]
    public async Task HandleHistoricalConversationAsOperatorAsync_ForAnUnknownHistoricalConversation_ReturnsNotFound_AndRecordsNothing()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleHistoricalConversationAsOperatorAsync(
            new Application.UseCases.GetVisitorHistory.GetVisitorHistoryConversation(
                fixture.CurrentConversation.Id, new ConversationId(Guid.NewGuid()), AssignedOperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
        Assert.Empty(fixture.AccessRecords.Recorded);
    }

    /// <summary>`24-12`'s own scoping decision, locked in by a test: the summary-list read
    /// (<see cref="GetVisitorHistoryHandler.HandleAsOperatorAsync"/>) never writes an access record,
    /// only opening one historical conversation does - see that handler's own remarks for why.</summary>
    [Fact]
    public async Task HandleAsOperatorAsync_ForAWidgetOnlyVisitor_RecordsNothing()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new Application.UseCases.GetVisitorHistory.GetVisitorHistory(
                fixture.CurrentConversation.Id, AssignedOperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Empty(fixture.AccessRecords.Recorded);
    }

    // `24-10`: the visitor-history panel is anchored on its own current conversation - if that one is
    // blocked, the whole panel is unreachable, the same "unreachable, not merely hidden" rule this
    // codebase now gives every operator-facing read of a blocked conversation.
    [Fact]
    public async Task HandleAsOperatorAsync_WhenTheCurrentConversationIsBlocked_ReturnsNotFound()
    {
        var fixture = CreateFixture();
        fixture.CurrentConversation.MarkBlockedForTesting(new OperatorId(Guid.NewGuid()), Now);

        var result = await fixture.Handler.HandleAsOperatorAsync(
            new Application.UseCases.GetVisitorHistory.GetVisitorHistory(
                fixture.CurrentConversation.Id, AssignedOperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
    }

    // `24-10`: opening a blocked historical conversation is unreachable too - proven separately from
    // the panel-anchor case above, per this item's own "asserted per path" rule.
    [Fact]
    public async Task HandleHistoricalConversationAsOperatorAsync_WhenTheHistoricalConversationIsBlocked_ReturnsNotFound_AndRecordsNothing()
    {
        var fixture = CreateFixture();
        var historical = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, VisitorId, Now.AddDays(-1));
        // `25-221`: a brand-new conversation starts Pending, not Waiting - graduate it with the
        // visitor's own real first message before AssignTo, which still only accepts Waiting.
        historical.AddVisitorMessage(VisitorId, new MessageId(Guid.NewGuid()), new MessageBody("anyone there?"), Now.AddDays(-1));
        historical.AssignTo(OtherOperatorId, Now.AddDays(-1));
        historical.AddOperatorMessage(OtherOperatorId, new MessageId(Guid.NewGuid()), new MessageBody("handled by someone else"), Now.AddDays(-1));
        historical.Close(Now.AddDays(-1).AddHours(1));
        historical.MarkBlockedForTesting(new OperatorId(Guid.NewGuid()), Now);
        fixture.Conversations.Seed(historical);
        fixture.ReadStore.Seed(historical);

        var result = await fixture.Handler.HandleHistoricalConversationAsOperatorAsync(
            new Application.UseCases.GetVisitorHistory.GetVisitorHistoryConversation(
                fixture.CurrentConversation.Id, historical.Id, AssignedOperatorId, SiteId, null, 50),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.NotFound", result.Error!.Value.Code);
        Assert.Empty(fixture.AccessRecords.Recorded);
    }
}
