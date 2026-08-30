using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `18-14`'s own Done-when: a real Postgres, real <see cref="Conversation"/> aggregates persisted
/// through EF (never a hand-written `INSERT`), and every number checked against ground truth worked
/// out by hand - the same bar `OperatorAnalyticsReadStoreTests` sets for `18-08`'s sibling read.
///
/// <para>`module_tasks` carries no partition (unlike `messages`), so unlike that file's sibling tests
/// there is no `EnsurePartitionsAsync` call needed here - a plain, ungeneration-partitioned table, per
/// `Stage20AddModuleTaskingTables`'s own migration.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class ModuleFlowReadStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset From = Now.AddDays(-14);
    private static readonly DateTimeOffset To = Now;

    // Test code lives outside `src/`, the Roslyn literal guard's own scan root
    // (`Ago.Chat.Architecture.Tests.SourceTreeLocator.FindSrcDirectory`) - a literal module key here is
    // the same "seed real domain data by hand" precedent `RouteConversationToModuleHandlerTests`/
    // `ModuleTaskGatewayIntegrationTests` already set for `20-07`'s own tests.
    private static readonly ModuleKey BookingModuleKey = new("calendar");
    private static readonly ModuleKey OtherModuleKey = new("taxi");

    private ModuleFlowReadStore Store => new(fixture.DataSource);

    private async Task<SiteId> SeedSiteAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        await db.SaveChangesAsync();
        return siteId;
    }

    private async Task<VisitorId> SeedVisitorAsync(SiteId siteId, DateTimeOffset createdAt)
    {
        var visitorId = new VisitorId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Visitors.Add(new Visitor(visitorId, siteId, createdAt));
        await db.SaveChangesAsync();
        return visitorId;
    }

    /// <summary>Starts a conversation and, unless <paramref name="withTask"/> is <see langword="null"/>,
    /// one module task on it - closing it immediately when <paramref name="closeAt"/> is given. Returns
    /// the new conversation's id, useful for the "no task at all" scenario where a caller wants proof
    /// this conversation exists but contributes nothing.</summary>
    private async Task<ConversationId> SeedConversationAsync(
        SiteId siteId, DateTimeOffset createdAt,
        (ModuleKey ModuleKey, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt)? withTask = null,
        IReadOnlyList<(ModuleKey ModuleKey, DateTimeOffset OpenedAt, DateTimeOffset? ClosedAt)>? withTasks = null)
    {
        var visitorId = await SeedVisitorAsync(siteId, createdAt);
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, createdAt);

        var tasks = withTasks ?? (withTask is { } single ? [single] : []);
        foreach (var task in tasks)
        {
            conversation.StartModuleTask(
                new ModuleTaskId(Guid.NewGuid()), task.ModuleKey, $"external-{Guid.NewGuid():N}", task.OpenedAt,
                stepKind: null, stepPayload: null, stepActions: []);
            if (task.ClosedAt is { } closedAt)
            {
                conversation.CloseModuleTask(closedAt);
            }
        }

        await using var db = fixture.CreateDbContext();
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation.Id;
    }

    /// <summary>The report's own central claim, proven against hand-built data: three conversations
    /// start a `calendar` task inside the window (opened at -10d, -8d, -5d); two of those close (at
    /// -9d and -6d), one is still `Open` at report time. Ground truth: started = 3, closed = 2.</summary>
    [Fact]
    public async Task GetSiteModuleFlowReportAsync_CountsStartedAndClosedTasks_MatchingHandCalculatedGroundTruth()
    {
        var siteId = await SeedSiteAsync();

        await SeedConversationAsync(siteId, Now.AddDays(-10), (BookingModuleKey, Now.AddDays(-10), Now.AddDays(-9)));
        await SeedConversationAsync(siteId, Now.AddDays(-8), (BookingModuleKey, Now.AddDays(-8), Now.AddDays(-6)));
        await SeedConversationAsync(siteId, Now.AddDays(-5), (BookingModuleKey, Now.AddDays(-5), null));

        var result = await Store.GetSiteModuleFlowReportAsync(siteId, BookingModuleKey, From, To, CancellationToken.None);

        Assert.Equal(3, result.FlowsStarted);
        Assert.Equal(2, result.FlowsClosed);
    }

    /// <summary>The item's own explicitly named edge case: a conversation with no module task at all
    /// must not appear as a zero-row false positive. A task-centric `count(*)` query never manufactures
    /// a row for a conversation that never started one, so the honest answer for a site with only this
    /// kind of conversation is a real 0/0, not a query failure or a phantom row.</summary>
    [Fact]
    public async Task GetSiteModuleFlowReportAsync_AConversationWithNoModuleTask_DoesNotAppearAsAFalsePositive()
    {
        var siteId = await SeedSiteAsync();
        await SeedConversationAsync(siteId, Now.AddDays(-3));

        var result = await Store.GetSiteModuleFlowReportAsync(siteId, BookingModuleKey, From, To, CancellationToken.None);

        Assert.Equal(0, result.FlowsStarted);
        Assert.Equal(0, result.FlowsClosed);
    }

    /// <summary>A task still <see cref="ModuleTaskState.Open"/> at report time counts toward
    /// <c>FlowsStarted</c> only - the identical "not yet resolved either way" treatment
    /// `IOperatorAnalyticsReadStore`'s own `MissedCount` gives a conversation still
    /// `Waiting`/`Assigned`.</summary>
    [Fact]
    public async Task GetSiteModuleFlowReportAsync_AStillOpenTask_CountsAsStartedButNotClosed()
    {
        var siteId = await SeedSiteAsync();
        await SeedConversationAsync(siteId, Now.AddDays(-2), (BookingModuleKey, Now.AddDays(-2), null));

        var result = await Store.GetSiteModuleFlowReportAsync(siteId, BookingModuleKey, From, To, CancellationToken.None);

        Assert.Equal(1, result.FlowsStarted);
        Assert.Equal(0, result.FlowsClosed);
    }

    /// <summary>The window is a real filter on `module_tasks.opened_at`, not merely echoed back - a
    /// task opened three weeks before <see cref="From"/> must not appear in either count.</summary>
    [Fact]
    public async Task GetSiteModuleFlowReportAsync_ExcludesTasksOpenedBeforeTheWindow()
    {
        var siteId = await SeedSiteAsync();
        await SeedConversationAsync(
            siteId, Now.AddDays(-21), (BookingModuleKey, Now.AddDays(-21), Now.AddDays(-20)));

        var result = await Store.GetSiteModuleFlowReportAsync(siteId, BookingModuleKey, From, To, CancellationToken.None);

        Assert.Equal(0, result.FlowsStarted);
        Assert.Equal(0, result.FlowsClosed);
    }

    /// <summary>`17-01`'s own bar: two real sites, one task each, and asking for one site's report must
    /// never surface the other's - proven against real seeded rows in the same database, not assumed
    /// from the `WHERE conversations.site_id = @SiteId` predicate alone.</summary>
    [Fact]
    public async Task GetSiteModuleFlowReportAsync_NeverReturnsAnotherSitesTasks()
    {
        var siteA = await SeedSiteAsync();
        var siteB = await SeedSiteAsync();
        await SeedConversationAsync(siteA, Now.AddDays(-3), (BookingModuleKey, Now.AddDays(-3), Now.AddDays(-2)));
        await SeedConversationAsync(siteB, Now.AddDays(-3), (BookingModuleKey, Now.AddDays(-3), null));

        var resultA = await Store.GetSiteModuleFlowReportAsync(siteA, BookingModuleKey, From, To, CancellationToken.None);
        var resultB = await Store.GetSiteModuleFlowReportAsync(siteB, BookingModuleKey, From, To, CancellationToken.None);

        Assert.Equal(1, resultA.FlowsStarted);
        Assert.Equal(1, resultA.FlowsClosed);
        Assert.Equal(1, resultB.FlowsStarted);
        Assert.Equal(0, resultB.FlowsClosed);
    }

    /// <summary>The item's own explicitly named cross-module-isolation requirement, proven with a real
    /// counter-example rather than trusted from the `module_key = @ModuleKey` predicate alone: a site
    /// with a task under a *different* enabled module (`"taxi"` - `adr/0065`'s own hypothetical second
    /// module, never actually built) must not have that task appear in a report scoped to
    /// <see cref="BookingModuleKey"/>.</summary>
    [Fact]
    public async Task GetSiteModuleFlowReportAsync_ExcludesTasksForADifferentModuleKey()
    {
        var siteId = await SeedSiteAsync();
        await SeedConversationAsync(siteId, Now.AddDays(-3), (BookingModuleKey, Now.AddDays(-3), Now.AddDays(-2)));
        await SeedConversationAsync(siteId, Now.AddDays(-3), (OtherModuleKey, Now.AddDays(-3), Now.AddDays(-2)));

        var result = await Store.GetSiteModuleFlowReportAsync(siteId, BookingModuleKey, From, To, CancellationToken.None);

        Assert.Equal(1, result.FlowsStarted);
        Assert.Equal(1, result.FlowsClosed);

        // The inverse view of the same counter-example: asking for the other module's own report on
        // this identical site must see only its own task, never the calendar one.
        var otherResult = await Store.GetSiteModuleFlowReportAsync(siteId, OtherModuleKey, From, To, CancellationToken.None);
        Assert.Equal(1, otherResult.FlowsStarted);
        Assert.Equal(1, otherResult.FlowsClosed);
    }

    /// <summary>
    /// The item's own open question, resolved and proven here rather than left implicit:
    /// <see cref="Conversation.StartModuleTask"/> only rejects a second *concurrent* active task
    /// (`ActiveModuleTask is { }`) - it does not reject starting a new one once the first has closed.
    /// One conversation here starts, closes, and restarts a `calendar` flow - two real, sequential
    /// attempts - and this report's own task-level denominator must count both, exactly as it would
    /// count two different conversations each starting one. Counting conversations instead would report
    /// 1 started here, silently losing the fact that the visitor tried twice - the reasoning
    /// `IModuleFlowReadStore`'s own remarks record for choosing tasks over conversations.
    /// </summary>
    [Fact]
    public async Task GetSiteModuleFlowReportAsync_ASingleConversationRestartingTheFlow_CountsBothTasks()
    {
        var siteId = await SeedSiteAsync();
        await SeedConversationAsync(
            siteId,
            Now.AddDays(-9),
            withTasks:
            [
                (BookingModuleKey, Now.AddDays(-9), Now.AddDays(-8)),
                (BookingModuleKey, Now.AddDays(-7), null),
            ]);

        var result = await Store.GetSiteModuleFlowReportAsync(siteId, BookingModuleKey, From, To, CancellationToken.None);

        Assert.Equal(2, result.FlowsStarted);
        Assert.Equal(1, result.FlowsClosed);
    }

    /// <summary>A site with no conversations at all: no `GROUPING SETS` collapse to worry about here
    /// (`ModuleFlowReadStore`'s own class remarks on why not) - a bare `count(*)` over zero matching
    /// rows already returns one row of zeros, proven directly rather than assumed from the SQL's own
    /// shape.</summary>
    [Fact]
    public async Task GetSiteModuleFlowReportAsync_ForASiteWithNoConversationsAtAll_ReturnsZeros()
    {
        var siteId = await SeedSiteAsync();

        var result = await Store.GetSiteModuleFlowReportAsync(siteId, BookingModuleKey, From, To, CancellationToken.None);

        Assert.Equal(0, result.FlowsStarted);
        Assert.Equal(0, result.FlowsClosed);
    }
}
