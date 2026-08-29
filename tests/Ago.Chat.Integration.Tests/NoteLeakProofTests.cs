using Ago.Chat.Application.UseCases.GetConversationHistory;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `18-04`'s own most important test, per the backlog item's own words: proves a
/// <see cref="ConversationNote"/> can never reach a visitor through
/// <see cref="GetConversationHistoryHandler.HandleAsVisitorAsync"/> - the real handler, the real
/// Postgres-backed <see cref="ConversationRepository"/>/<see cref="ConversationReadStore"/>, a real
/// container, not a hand-simplified stand-in that only happens to agree with the real query
/// (this item's own "where this is likely to go wrong" warning #1).
///
/// <para><b>Fails-before, actually run.</b> With <see cref="ConversationNote"/> stored as its own
/// table reached only through <see cref="NoteRepository"/>, this test cannot fail by construction -
/// there is no code path from <see cref="ConversationReadStore.GetHistoryAsync"/> to a note at all, so
/// proving the test can catch the defect class needed the defect actually present once. That was done
/// by temporarily changing <c>ConversationReadStore.GetHistoryAsync</c>'s <c>Sql</c> constant to
/// <c>union all</c> a second branch selecting from <c>conversation_notes</c>, reshaped to look like a
/// `messages` row (simulating the tempting-but-wrong "note as a `messages` row with a `Kind`
/// discriminator" design this item's own backlog item explicitly rejects), then re-running this test
/// against the real container. It failed exactly where a real leak would surface:
/// <c>Assert.Single() Failure: The collection contained 2 items - Collection: [MessageHistoryItem {
/// ..., AuthorKind = Operator, Body = "INTERNAL: this visitor threatened a chargeback, watch for
/// repeat orders." }, MessageHistoryItem { ..., AuthorKind = Visitor, Body = "Hi, I need help with my
/// order." }]</c> - the note's own body, verbatim, in what the handler was about to hand back to the
/// visitor. The mutation was reverted immediately after (never committed); see this item's own
/// commit-prep notes for the exact diff exercised.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class NoteLeakProofTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task HandleAsVisitorAsync_ConversationCarriesANote_NoteNeverAppearsInTheVisitorsHistoryPage()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var authorOperatorId = new OperatorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("Hi, I need help with my order."), Now);

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
        }

        // A note only an operator should ever see - deliberately written so a leak is unmistakable in
        // the assertion output rather than a bland string a passing test could coincide with by luck.
        const string noteBody = "INTERNAL: this visitor threatened a chargeback, watch for repeat orders.";
        await using (var db = fixture.CreateDbContext())
        {
            var noteRepository = new NoteRepository(db);
            var note = ConversationNote.Write(
                new ConversationNoteId(Guid.NewGuid()), conversation.Id, authorOperatorId, noteBody, Now);
            await noteRepository.SaveAsync(note, CancellationToken.None);
        }

        // The real handler, the real write-side repository, the real Dapper-backed read store - not a
        // fake standing in for any of the three. IPermissionChecker is constructed but never invoked on
        // this path (the visitor entry point is gated by conversation.VisitorId alone,
        // TenantScopeExemptions' own entry for this method), so any real implementation satisfies it.
        var handler = new GetConversationHistoryHandler(
            new ConversationRepository(fixture.CreateDbContext()),
            new ConversationReadStore(fixture.DataSource),
            new PermissionChecker(fixture.CreateDbContext()));

        var result = await handler.HandleAsVisitorAsync(
            new GetConversationHistoryAsVisitor(conversation.Id, visitorId, BeforeSequence: null, PageSize: 50),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        var page = result.Value;

        // The one real message is there - a leak-proof test that returned an empty page would prove
        // nothing about leakage, only that the query is broken.
        var message = Assert.Single(page.Messages);
        Assert.Equal("Hi, I need help with my order.", message.Body);

        // The actual guarantee: nothing in what the visitor sees carries the note's text or author -
        // checked as a body substring rather than only an item count, so a future change that pads the
        // page with an unrelated extra row would not silently mask a real leak.
        Assert.DoesNotContain(page.Messages, m => m.Body.Contains("chargeback", StringComparison.Ordinal));
        Assert.DoesNotContain(page.Messages, m => m.AuthorId == authorOperatorId.Value);
    }

    [Fact]
    public async Task GetDeltaAsync_ConversationCarriesANote_NoteNeverAppearsInTheVisitorsDelta()
    {
        // `3-03`'s reconnect path - GetConversationHistoryHandler.HandleDeltaAsVisitorAsync shares the
        // identical IConversationReadStore.GetDeltaAsync call the operator path uses
        // (GetConversationHistoryHandler's own remarks: "one handler, two entry points... share
        // everything after that"), so this is the second of the two real read methods a note could
        // otherwise ride through.
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var authorOperatorId = new OperatorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("First message."), Now);

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Visitors.Add(new Visitor(visitorId, siteId, Now));
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync();
        }

        const string noteBody = "INTERNAL: escalate to billing if this visitor writes again.";
        await using (var db = fixture.CreateDbContext())
        {
            var note = ConversationNote.Write(
                new ConversationNoteId(Guid.NewGuid()), conversation.Id, authorOperatorId, noteBody, Now);
            await new NoteRepository(db).SaveAsync(note, CancellationToken.None);
        }

        var handler = new GetConversationHistoryHandler(
            new ConversationRepository(fixture.CreateDbContext()),
            new ConversationReadStore(fixture.DataSource),
            new PermissionChecker(fixture.CreateDbContext()));

        var result = await handler.HandleDeltaAsVisitorAsync(
            new GetConversationDeltaAsVisitor(conversation.Id, visitorId, AfterSequence: 0),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(result.Value, m => m.Body.Contains("billing", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Value, m => m.AuthorId == authorOperatorId.Value);
    }
}
