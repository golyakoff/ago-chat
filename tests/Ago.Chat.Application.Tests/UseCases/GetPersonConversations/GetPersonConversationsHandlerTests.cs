using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.GetPersonConversations;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Tests.UseCases.GetPersonConversations;

/// <summary>`26-269`: the client-detail hub's "which dialog do I open for this person" read - unit
/// tests against the fakes, mirroring <c>GetPersonNotesHandlerTests</c>'s own gating/tenant-isolation
/// shape and <c>GetVisitorHistoryHandlerTests</c>'s own conversation-seeding shape. The real ordering
/// SQL (`ConversationReadStore.GetConversationsForPersonAsync`) is proven against a real Postgres in
/// <c>PersonConversationsReadStoreTests</c>; these tests prove the handler's own gating, tenant
/// isolation and empty-is-not-an-error degradation, using <see cref="FakeConversationReadStore"/>'s
/// mirrored ordering as "good enough".</summary>
public class GetPersonConversationsHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly SiteId OtherSiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // `26-269`: the ordering tests below need two `Closed` conversations to compare "newer" against
    // "older" by id - a bare `Guid.NewGuid()` (`FakeIdGenerator`'s own choice, deliberately not
    // time-ordered per `GetVisitorHistoryHandlerTests`'s own remarks) would make that comparison
    // meaningless. Real uuid v7 ids make the fake's `id desc` tiebreaker mean the same "newest first"
    // the production SQL's own `id desc` means (`PersonConversationsReadStoreTests` proves the real
    // query against Postgres; this only needs ids that are actually time-ordered).
    private static readonly IIdGenerator IdGenerator = new UuidV7Generator();

    private sealed record Fixture(
        GetPersonConversationsHandler Handler, FakeVisitorRepository Visitors, FakeConversationReadStore ReadStore);

    private static Fixture CreateFixture(bool grantPermission = true)
    {
        var visitors = new FakeVisitorRepository();
        var readStore = new FakeConversationReadStore();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.ConversationRead);
        }

        var handler = new GetPersonConversationsHandler(visitors, readStore, permissions);
        return new Fixture(handler, visitors, readStore);
    }

    private static Visitor APerson(FakeVisitorRepository visitors, SiteId siteId)
    {
        var visitor = new Visitor(new VisitorId(Guid.NewGuid()), siteId, Now);
        visitors.Seed(visitor);
        return visitor;
    }

    [Fact]
    public async Task HandleAsync_OperatorWithoutPermission_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);
        var person = APerson(fixture.Visitors, SiteId);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetPersonConversations.GetPersonConversations(person.Id, SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_UnknownPersonId_ReturnsPersonNotFound()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetPersonConversations.GetPersonConversations(new VisitorId(Guid.NewGuid()), SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Person.NotFound", result.Error!.Value.Code);
    }

    /// <summary>Tenant isolation by construction, the same shape <c>GetPersonNotesHandlerTests</c>
    /// already proves for the sibling read: a person who belongs to another account reads exactly like
    /// an unknown id - never a narrower code that would confirm the id exists elsewhere.</summary>
    [Fact]
    public async Task HandleAsync_PersonFromAnotherSite_ReturnsPersonNotFound()
    {
        var fixture = CreateFixture();
        var theirs = APerson(fixture.Visitors, OtherSiteId);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetPersonConversations.GetPersonConversations(theirs.Id, SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Person.NotFound", result.Error!.Value.Code);
    }

    /// <summary>`adr/0184` decision 4's own "no dialog to open yet" - a person with zero conversations
    /// (a `26-268` manual client, for one) degrades to an empty list, never an error.</summary>
    [Fact]
    public async Task HandleAsync_PersonWithNoConversations_ReturnsAnEmptyList_NotAnError()
    {
        var fixture = CreateFixture();
        var person = APerson(fixture.Visitors, SiteId);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetPersonConversations.GetPersonConversations(person.Id, SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(result.Value);
    }

    /// <summary>The one row the client-detail hub actually needs first: the active conversation, even
    /// when older closed ones exist - the identical "at most one non-Closed conversation per visitor"
    /// invariant <c>IConversationRepository.GetActiveForVisitorAsync</c> relies on in production
    /// code.</summary>
    [Fact]
    public async Task HandleAsync_WhenAnActiveConversationExists_ItSortsFirst_RegardlessOfAge()
    {
        var fixture = CreateFixture();
        var person = APerson(fixture.Visitors, SiteId);

        var closed = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, person.Id, Now.AddDays(-1));
        closed.AddVisitorMessage(person.Id, new MessageId(Guid.NewGuid()), new MessageBody("past visit"), Now.AddDays(-1));
        closed.Close(Now.AddDays(-1).AddHours(1));
        fixture.ReadStore.Seed(closed);

        var active = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, person.Id, Now.AddDays(-10));
        active.AddVisitorMessage(person.Id, new MessageId(Guid.NewGuid()), new MessageBody("still open"), Now.AddDays(-10));
        fixture.ReadStore.Seed(active);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetPersonConversations.GetPersonConversations(person.Id, SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, result.Value.Count);
        var first = result.Value[0];
        Assert.Equal(active.Id.Value, first.ConversationId);
        Assert.True(first.IsActive);
        Assert.Equal(nameof(ConversationState.Waiting), first.State);
        Assert.Null(first.ClosedAt);

        var second = result.Value[1];
        Assert.Equal(closed.Id.Value, second.ConversationId);
        Assert.False(second.IsActive);
        Assert.Equal(nameof(ConversationState.Closed), second.State);
        Assert.NotNull(second.ClosedAt);
    }

    /// <summary>No active conversation at all - "else the most recent" resolves to the newest closed
    /// one, first in the list.</summary>
    [Fact]
    public async Task HandleAsync_WithNoActiveConversation_TheMostRecentClosedOneSortsFirst()
    {
        var fixture = CreateFixture();
        var person = APerson(fixture.Visitors, SiteId);

        var older = Conversation.Start(new ConversationId(IdGenerator.NewId(Now.AddDays(-5))), SiteId, person.Id, Now.AddDays(-5));
        older.AddVisitorMessage(person.Id, new MessageId(Guid.NewGuid()), new MessageBody("older"), Now.AddDays(-5));
        older.Close(Now.AddDays(-5).AddHours(1));
        fixture.ReadStore.Seed(older);

        var newer = Conversation.Start(new ConversationId(IdGenerator.NewId(Now.AddDays(-1))), SiteId, person.Id, Now.AddDays(-1));
        newer.AddVisitorMessage(person.Id, new MessageId(Guid.NewGuid()), new MessageBody("newer"), Now.AddDays(-1));
        newer.Close(Now.AddDays(-1).AddHours(1));
        fixture.ReadStore.Seed(newer);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetPersonConversations.GetPersonConversations(person.Id, SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(newer.Id.Value, result.Value[0].ConversationId);
        Assert.Equal(older.Id.Value, result.Value[1].ConversationId);
    }

    /// <summary>Never another person's conversations, even one on the same site - the same isolation
    /// <see cref="FakeConversationReadStore.GetConversationsForPersonAsync"/> mirrors from the real
    /// store's `where c.visitor_id = @PersonId` predicate.</summary>
    [Fact]
    public async Task HandleAsync_NeverReturnsAnotherPersonsConversations()
    {
        var fixture = CreateFixture();
        var person = APerson(fixture.Visitors, SiteId);
        var someoneElse = APerson(fixture.Visitors, SiteId);

        var theirs = Conversation.Start(new ConversationId(Guid.NewGuid()), SiteId, someoneElse.Id, Now);
        theirs.AddVisitorMessage(someoneElse.Id, new MessageId(Guid.NewGuid()), new MessageBody("not mine"), Now);
        fixture.ReadStore.Seed(theirs);

        var result = await fixture.Handler.HandleAsync(
            new Application.UseCases.GetPersonConversations.GetPersonConversations(person.Id, SiteId, OperatorId), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(result.Value);
    }
}
