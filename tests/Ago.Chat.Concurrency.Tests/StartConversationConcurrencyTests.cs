using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GetSiteConfigById;
using Ago.Chat.Application.UseCases.StartConversation;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>
/// Found live 2026-09-12: an operator's own console listed the same visitor twice - two
/// <see cref="Domain.Conversation"/> rows, same site, same visitor, created microseconds apart, both
/// already <c>Assigned</c> to the same operator. Two browser tabs sharing one persisted
/// <c>visitor_id</c>, or a widget reconnect racing its own prior connection, both call
/// <see cref="Ago.Chat.Application.UseCases.StartConversation.StartConversationHandler.HandleAsync"/>
/// before either has committed - <see cref="IConversationRepository.GetActiveForVisitorAsync"/> is a
/// plain, non-locking read, so both see nothing and both insert.
///
/// <para>Arranged, not hoped for - the identical "real concurrent write, injected at the exact moment
/// of the loser's own <c>SaveAsync</c>" shape <c>MarkConversationReadConcurrencyTests</c>'s own
/// <c>RacingConversationRepository</c> already established, reused here rather than duplicated.</para>
/// </summary>
[Collection(ConcurrencyCollection.Name)]
public sealed class StartConversationConcurrencyTests(ConcurrencyTestFixture fixture)
{
    /// <summary>
    /// The exact live shape: the winner's `StartConversation` call is injected to run - and fully
    /// commit, on its own connection - right as the loser's own handler is about to insert its own
    /// brand-new row for the identical visitor. Before the fix this produced two rows; after it, the
    /// loser's own `SaveAsync` hits `ix_conversations_one_open_per_visitor`, and the handler returns the
    /// winner's own conversation instead of a second one.
    /// </summary>
    [Fact]
    public async Task TwoConcurrentStartsForTheSameVisitor_RacedMidSave_TheLoserReturnsTheWinnersConversation()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());

        await using (var seedDb = fixture.CreateDbContext())
        {
            seedDb.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await seedDb.SaveChangesAsync(CancellationToken.None);
        }

        ConversationId? winnerId = null;
        await using var db = fixture.CreateDbContext();
        var racing = new RacingConversationRepository(
            new ConversationRepository(db),
            maxInjections: 1,
            async () =>
            {
                var winner = await StartAsync(siteId, visitorId);
                Assert.True(winner.IsSuccess);
                Assert.True(winner.Value.IsNew);
                winnerId = winner.Value.ConversationId;
            });

        var handler = new StartConversationHandler(
            new VisitorRepository(db), racing,
            new GetSiteConfigByIdHandler(new SiteRepository(db), new NoOpCache()),
            new SystemClock(), new UuidV7Generator(), new FixedVisitorEmojiPairGenerator());

        var loserResult = await handler.HandleAsync(new StartConversation(siteId, visitorId), CancellationToken.None);

        Assert.True(loserResult.IsSuccess, loserResult.IsFailure ? loserResult.Error!.Value.Message : string.Empty);
        Assert.NotNull(winnerId);
        // The loser did not create a second conversation - it reports the winner's own id, and that it
        // did not itself start a new one.
        Assert.Equal(winnerId!.Value, loserResult.Value.ConversationId);
        Assert.False(loserResult.Value.IsNew);
        // Exactly one attempt on the loser's own repository: the first (and only) SaveAsync call lost
        // the unique-violation race - there is no second SaveAsync, only a re-read.
        Assert.Equal(1, racing.SaveAttempts);

        var conversationIds = await db.Conversations.AsNoTracking()
            .Where(c => c.VisitorId == visitorId)
            .Select(c => c.Id)
            .ToListAsync(CancellationToken.None);
        var single = Assert.Single(conversationIds);
        Assert.Equal(winnerId!.Value, single);
    }

    /// <summary>The mirror of the item's own live evidence: many concurrent starts for one visitor -
    /// what several tabs, or a flapping connection retrying, would produce - must all agree on exactly
    /// one conversation, never a scattering of new ones. The visitor is seeded ahead of time,
    /// deliberately - the live incident this item fixes was a *returning* visitor
    /// (<c>Visitor.Touch</c>'s own branch, not <c>new Visitor(...)</c>'s), and a brand-new visitor
    /// racing on its own first-contact insert is a second, different unique-key race
    /// (<c>PK_visitors</c>) this item does not scope in - filed separately.</summary>
    [Fact]
    public async Task ManyConcurrentStartsForTheSameVisitor_AllAgreeOnExactlyOneConversation()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        await using (var seedDb = fixture.CreateDbContext())
        {
            seedDb.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            seedDb.Visitors.Add(new Visitor(visitorId, siteId, now));
            await seedDb.SaveChangesAsync(CancellationToken.None);
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starters = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () =>
            {
                await gate.Task;
                return await StartAsync(siteId, visitorId);
            }))
            .ToArray();

        gate.SetResult();
        var results = await Task.WhenAll(starters);

        Assert.All(results, r => Assert.True(r.IsSuccess));
        var distinctConversationIds = results.Select(r => r.Value.ConversationId).Distinct().ToList();
        Assert.Single(distinctConversationIds);
        // Exactly one of the eight actually created it; the other seven found it already there.
        Assert.Single(results, r => r.Value.IsNew);

        await using var db = fixture.CreateDbContext();
        var count = await db.Conversations.AsNoTracking().CountAsync(c => c.VisitorId == visitorId, CancellationToken.None);
        Assert.Equal(1, count);
    }

    /// <summary>Never a source of confusion between two different visitors - the index is scoped to
    /// `visitor_id`, not shared across a site, so two visitors starting at the same instant must both
    /// get their own conversation with no interaction.</summary>
    [Fact]
    public async Task ConcurrentStartsForTwoDifferentVisitors_EachGetsItsOwnConversation()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorA = new VisitorId(Guid.NewGuid());
        var visitorB = new VisitorId(Guid.NewGuid());

        await using (var seedDb = fixture.CreateDbContext())
        {
            seedDb.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await seedDb.SaveChangesAsync(CancellationToken.None);
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startingA = Task.Run(async () => { await gate.Task; return await StartAsync(siteId, visitorA); });
        var startingB = Task.Run(async () => { await gate.Task; return await StartAsync(siteId, visitorB); });

        gate.SetResult();
        var results = await Task.WhenAll(startingA, startingB);

        Assert.All(results, r =>
        {
            Assert.True(r.IsSuccess);
            Assert.True(r.Value.IsNew);
        });
        Assert.NotEqual(results[0].Value.ConversationId, results[1].Value.ConversationId);
    }

    private sealed class FixedVisitorEmojiPairGenerator : IVisitorEmojiPairGenerator
    {
        public (string Creature, string Food) NextPair() => ("🐳", "🍇");
    }

    /// <summary>`6-08`'s seam, reused verbatim from <c>MarkConversationReadConcurrencyTests</c> - every
    /// read goes to the real repository untouched, and each of the first <paramref name="maxInjections"/>
    /// saves runs a real, fully-committed concurrent write first.</summary>
    private sealed class RacingConversationRepository(
        IConversationRepository inner, int maxInjections, Func<Task> injectConcurrentWriteAsync) : IConversationRepository
    {
        public int SaveAttempts { get; private set; }

        public Task<Conversation?> GetByIdAsync(ConversationId id, CancellationToken cancellationToken) =>
            inner.GetByIdAsync(id, cancellationToken);

        public Task<Conversation?> GetActiveForVisitorAsync(VisitorId visitorId, CancellationToken cancellationToken) =>
            inner.GetActiveForVisitorAsync(visitorId, cancellationToken);

        public Task<IReadOnlyList<Conversation>> GetAssignedToOperatorAsync(OperatorId operatorId, CancellationToken cancellationToken) =>
            inner.GetAssignedToOperatorAsync(operatorId, cancellationToken);

        public Task<IReadOnlyList<Conversation>> GetWaitingForSiteAsync(SiteId siteId, CancellationToken cancellationToken) =>
            inner.GetWaitingForSiteAsync(siteId, cancellationToken);

        public async Task SaveAsync(Conversation conversation, CancellationToken cancellationToken)
        {
            SaveAttempts++;
            if (SaveAttempts <= maxInjections)
            {
                await injectConcurrentWriteAsync();
            }

            await inner.SaveAsync(conversation, cancellationToken);
        }
    }

    private async Task<Result<StartConversationResult>> StartAsync(SiteId siteId, VisitorId visitorId)
    {
        await using var db = fixture.CreateDbContext();
        var handler = new StartConversationHandler(
            new VisitorRepository(db), new ConversationRepository(db),
            new GetSiteConfigByIdHandler(new SiteRepository(db), new NoOpCache()),
            new SystemClock(), new UuidV7Generator(), new FixedVisitorEmojiPairGenerator());
        return await handler.HandleAsync(new StartConversation(siteId, visitorId), CancellationToken.None);
    }
}
