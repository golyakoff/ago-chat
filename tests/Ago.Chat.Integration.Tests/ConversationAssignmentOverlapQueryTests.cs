using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-03`'s own Done-when: "An overlap query answers 'how many did this operator hold at instant T'
/// against a fixture with a known answer. It is written in this item even though no screen calls it
/// yet, because it is the only proof the rows are shaped for their purpose." This is that proof -
/// <see cref="ConversationAssignmentOverlapQuery"/>'s own remarks on why it has no port and no
/// Application-layer caller.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ConversationAssignmentOverlapQueryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Four intervals for the operator under test, deliberately chosen so every boundary condition in
    /// <see cref="ConversationAssignmentOverlapQuery.CountHeldAtAsync"/>'s own predicate is exercised at
    /// least once - a closed interval whose end the query must respect, an interval still open at query
    /// time, a closed interval whose window the query instant falls strictly inside and strictly after,
    /// and an interval that has not started yet. A fifth interval belongs to a *different* operator,
    /// overlapping the exact same window, to prove the query is scoped by operator and not merely by
    /// time.
    /// </summary>
    [Fact]
    public async Task CountHeldAtAsync_AgainstAKnownFixture_MatchesTheHandComputedAnswer()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var otherOperatorId = new OperatorId(Guid.NewGuid());

        // A: [T0, T0+10) - closed, spans the early and middle instants below.
        // B: [T0+2, open) - never closes within this test's own window.
        // C: [T0+5, T0+7) - closed, the shortest-lived of the four.
        // D: [T0+20, open) - starts after every instant below except the last.
        // O: [T0+2, open) - identical window to B, but a *different* operator - must never be counted.
        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
            db.Operators.Add(new Operator(otherOperatorId, siteId, OperatorStatus.Online, capacity: 5));

            AddClosedInterval(db, siteId, operatorId, T0, T0.AddMinutes(10));
            AddOpenInterval(db, siteId, operatorId, T0.AddMinutes(2));
            AddClosedInterval(db, siteId, operatorId, T0.AddMinutes(5), T0.AddMinutes(7));
            AddOpenInterval(db, siteId, operatorId, T0.AddMinutes(20));
            AddOpenInterval(db, siteId, otherOperatorId, T0.AddMinutes(2));

            await db.SaveChangesAsync();
        }

        // Before anything started.
        Assert.Equal(0, await ConversationAssignmentOverlapQuery.CountHeldAtAsync(
            fixture.DataSource, operatorId, T0.AddMinutes(-1), CancellationToken.None));

        // Only A has started (started_at <= instant), and it has not ended yet.
        Assert.Equal(1, await ConversationAssignmentOverlapQuery.CountHeldAtAsync(
            fixture.DataSource, operatorId, T0, CancellationToken.None));

        // A and B both hold; C has not started yet.
        Assert.Equal(2, await ConversationAssignmentOverlapQuery.CountHeldAtAsync(
            fixture.DataSource, operatorId, T0.AddMinutes(3), CancellationToken.None));

        // A, B and C all hold - the instant this test exists to prove is not miscounted: strictly
        // inside C's own [5, 7) window.
        Assert.Equal(3, await ConversationAssignmentOverlapQuery.CountHeldAtAsync(
            fixture.DataSource, operatorId, T0.AddMinutes(6), CancellationToken.None));

        // C has ended (ended_at is exclusive - an interval that ends exactly at the instant does not
        // count), so only A and B remain.
        Assert.Equal(2, await ConversationAssignmentOverlapQuery.CountHeldAtAsync(
            fixture.DataSource, operatorId, T0.AddMinutes(7), CancellationToken.None));

        // A has ended too by now; only the still-open B remains, until D starts.
        Assert.Equal(1, await ConversationAssignmentOverlapQuery.CountHeldAtAsync(
            fixture.DataSource, operatorId, T0.AddMinutes(15), CancellationToken.None));

        // B and D both hold; A and C are long closed.
        Assert.Equal(2, await ConversationAssignmentOverlapQuery.CountHeldAtAsync(
            fixture.DataSource, operatorId, T0.AddMinutes(25), CancellationToken.None));

        // The other operator's identically-timed interval never counts against this operator, at any
        // instant it was open.
        Assert.Equal(1, await ConversationAssignmentOverlapQuery.CountHeldAtAsync(
            fixture.DataSource, otherOperatorId, T0.AddMinutes(3), CancellationToken.None));
    }

    private static void AddClosedInterval(
        AgoChatDbContext db, SiteId siteId, OperatorId operatorId, DateTimeOffset startedAt, DateTimeOffset endedAt)
    {
        var interval = ConversationAssignmentInterval.Open(
            new ConversationAssignmentId(Guid.NewGuid()), siteId, new ConversationId(Guid.NewGuid()), operatorId,
            ConversationAssignmentSource.Assigned, startedAt);
        interval.Close(endedAt);
        db.ConversationAssignments.Add(interval);
    }

    private static void AddOpenInterval(
        AgoChatDbContext db, SiteId siteId, OperatorId operatorId, DateTimeOffset startedAt) =>
        db.ConversationAssignments.Add(ConversationAssignmentInterval.Open(
            new ConversationAssignmentId(Guid.NewGuid()), siteId, new ConversationId(Guid.NewGuid()), operatorId,
            ConversationAssignmentSource.Assigned, startedAt));
}
