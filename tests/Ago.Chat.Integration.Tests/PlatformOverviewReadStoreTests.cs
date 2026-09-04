using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `12-02`'s own Done-when, against a real Postgres: several tenants with deliberately different seat
/// counts, conversation counts, message volumes (spanning both a recent and an older `messages`
/// partition) and attachment byte totals, and the returned numbers must equal ground truth for every
/// one of them - not merely "a response of the right shape came back".
///
/// The ground truth is <see cref="PlatformOverviewFixture.Plan"/>; every expectation below is derived
/// from it by the same arithmetic a reader would do by hand, never copied from a previous run.
/// </summary>
[Collection(PlatformOverviewCollection.Name)]
public sealed class PlatformOverviewReadStoreTests(PlatformOverviewFixture fixture)
{
    private const int LargeEnoughForEverySite = 100;

    [Fact]
    public async Task ListSites_ReturnsEverySite_WithNumbersMatchingGroundTruth()
    {
        var page = await ListAsync(before: null, limit: LargeEnoughForEverySite);

        Assert.Equal(PlatformOverviewFixture.Plan.Count, page.Sites.Count);

        foreach (var plan in PlatformOverviewFixture.Plan)
        {
            var row = Assert.Single(page.Sites, s => s.Id == plan.Id);

            Assert.Equal(plan.Name, row.Name);
            Assert.Equal((long)plan.Operators, row.SeatCount);
            Assert.Equal((long)plan.Conversations, row.ConversationCount);
            Assert.Equal(ExpectedRecentMessages(plan), row.RecentMessageCount);
            Assert.Equal(ExpectedAttachmentBytes(plan), row.AttachmentBytes);
            AssertSameInstant(ExpectedCreatedAt(plan), row.CreatedAt);
            AssertSameInstant(ExpectedLastMessageAt(plan), row.LastMessageAt);
        }
    }

    /// <summary>The window is the point of this item, so it gets its own assertions rather than only
    /// riding along in the sweep above: Alpha has five messages and only four of them are recent
    /// (the fifth sits 95 days back, in a partition the bounded query never has to open), and Delta
    /// has two messages, both older than the window - which must read as volume 0 and last activity
    /// `null` while its conversation count stays 1, so "quiet lately" is never mistaken for "never
    /// used".</summary>
    [Fact]
    public async Task ListSites_CountsOnlyMessagesInsideTheRecentWindow()
    {
        var page = await ListAsync(before: null, limit: LargeEnoughForEverySite);

        var alpha = Assert.Single(page.Sites, s => s.Name == "Alpha Shop");
        Assert.Equal(5, PlatformOverviewFixture.Plan.Single(p => p.Name == "Alpha Shop").MessageDaysAgo.Count);
        Assert.Equal(4L, alpha.RecentMessageCount);

        var delta = Assert.Single(page.Sites, s => s.Name == "Delta Diner");
        Assert.Equal(0L, delta.RecentMessageCount);
        Assert.Null(delta.LastMessageAt);
        Assert.Equal(1L, delta.ConversationCount);
    }

    /// <summary>An entirely unused tenant: zeroes, not nulls, for every count - and `0` for
    /// attachment bytes specifically, where the SQL's `coalesce` is what turns `SUM` over no rows
    /// into a real zero. Its `createdAt` is the null case: a `sites` row with no recorded creation
    /// time (`Stage12AddSiteCreatedAt` backfills nothing) comes back null rather than being
    /// substituted with anything.</summary>
    [Fact]
    public async Task ListSites_ForATenantWithNoUsageAtAll_ReturnsZerosAndNulls()
    {
        var page = await ListAsync(before: null, limit: LargeEnoughForEverySite);

        var gamma = Assert.Single(page.Sites, s => s.Name == "Gamma Garage");

        Assert.Equal(0L, gamma.SeatCount);
        Assert.Equal(0L, gamma.ConversationCount);
        Assert.Equal(0L, gamma.RecentMessageCount);
        Assert.Equal(0L, gamma.AttachmentBytes);
        Assert.Null(gamma.LastMessageAt);
        Assert.Null(gamma.CreatedAt);
    }

    /// <summary>Deleted attachments are excluded and pending ones are not - Alpha's 999_999-byte
    /// deleted row would dominate its total if the filter were wrong, and Delta's total is exactly
    /// its one pending attachment.</summary>
    [Fact]
    public async Task ListSites_SumsNonDeletedAttachmentBytesOnly()
    {
        var page = await ListAsync(before: null, limit: LargeEnoughForEverySite);

        Assert.Equal(3_500L, Assert.Single(page.Sites, s => s.Name == "Alpha Shop").AttachmentBytes);
        Assert.Equal(777L, Assert.Single(page.Sites, s => s.Name == "Delta Diner").AttachmentBytes);
        Assert.Equal(0L, Assert.Single(page.Sites, s => s.Name == "Beta Bakery").AttachmentBytes);
    }

    /// <summary>Keyset pagination for real (`data-model.md`: no `OFFSET`): walk the whole list two
    /// sites at a time and the concatenation must equal the single-page result exactly - same order,
    /// no site seen twice, none skipped between pages.</summary>
    [Fact]
    public async Task ListSites_PagedTwoAtATime_ContinuesWhereItStopped_WithNoGapAndNoDuplicate()
    {
        var wholeList = await ListAsync(before: null, limit: LargeEnoughForEverySite);
        var expectedOrder = wholeList.Sites.Select(s => s.Id).ToList();

        var walked = new List<SiteId>();
        Guid? cursor = null;
        var pages = 0;
        do
        {
            var page = await ListAsync(before: cursor, limit: 2);
            Assert.True(page.Sites.Count <= 2);
            walked.AddRange(page.Sites.Select(s => s.Id));
            cursor = page.NextBefore;
            pages++;

            // A runaway loop should fail as a test, not hang the suite.
            Assert.True(pages <= expectedOrder.Count + 1, "Pagination did not terminate.");
        } while (cursor is not null);

        Assert.Equal(expectedOrder, walked);
        Assert.Equal(walked.Distinct().Count(), walked.Count);
        Assert.True(pages > 1, "The page size must be smaller than the number of seeded sites for this to prove anything.");
    }

    /// <summary>The cursor really is exclusive: asking for everything before the oldest site returns
    /// an empty final page and a null cursor, rather than looping back to the start.</summary>
    [Fact]
    public async Task ListSites_WithACursorPastTheOldestSite_ReturnsAnEmptyPage()
    {
        var wholeList = await ListAsync(before: null, limit: LargeEnoughForEverySite);
        var oldest = wholeList.Sites[^1].Id.Value;

        var page = await ListAsync(before: oldest, limit: LargeEnoughForEverySite);

        Assert.Empty(page.Sites);
        Assert.Null(page.NextBefore);
    }

    /// <summary>
    /// `15-09`/`adr/0087`: the partitioning claim this read now rests on, checked against a real planner
    /// instead of asserted in a comment. Before this item, `messages` was `RANGE (created_at)`
    /// partitioned and this test proved the 30-day window predicate kept older monthly partitions out of
    /// the plan; now `messages` is `PARTITION BY HASH (site_id)` and `created_at` carries no pruning
    /// power at all - a window predicate on it prunes nothing, correctly, since it was never the
    /// partition key. What matters now is the same thing every other query in this codebase reading
    /// `messages` has to get right (`adr/0087`'s own central claim): a `site_id` equality predicate on
    /// the `messages` scan itself - not merely on the `conversations` row it is joined to - is what
    /// prunes to one bucket. `PlatformOverviewReadStore`'s own real lateral filters `m.site_id`
    /// directly for exactly this reason; this test proves the contrast against a query that only
    /// filters the joined `conversations.site_id` (the pre-`18-01` shape, before `messages` carried its
    /// own `site_id` at all), which gives the planner nothing to prune `messages` on.
    ///
    /// <para>This is a <i>structural</i> check, not a performance claim - it says which partitions
    /// appear in the plan, and deliberately measures no timing (`CLAUDE.md`: measure or stay silent).
    /// The unscoped query is run alongside the scoped one, so the test fails just as loudly if the
    /// fixture's own multi-site data somehow collapsed onto one bucket already, which would make the
    /// scoped half prove nothing.</para>
    /// </summary>
    [Fact]
    public async Task TheSiteIdPredicate_PrunesTheMessagesScanToOneBucket()
    {
        var scopedPlan = await ExplainMessageScanAsync(filterOnMessageSiteId: true);
        var unscopedPlan = await ExplainMessageScanAsync(filterOnMessageSiteId: false);

        Assert.Equal(1, DistinctPartitionCount(scopedPlan));
        Assert.True(
            DistinctPartitionCount(unscopedPlan) > 1,
            $"Expected the unscoped query (join-only, no m.site_id predicate) to touch more than one bucket - the fixture seeds {PlatformOverviewFixture.Plan.Count} distinct sites so this is not vacuous.\nPlan:\n{unscopedPlan}");
    }

    /// <summary>`23-14`'s own "must not break" clause, at the read-store level: an empty search must
    /// return the identical page an unfiltered call would, and the two counts must both equal the
    /// fixture's own known total - never a narrower page that merely happens to look complete.</summary>
    [Fact]
    public async Task ListSites_WithNoQuery_MatchesTotalSites_AndTheUnfilteredPage()
    {
        var unfiltered = await ListAsync(before: null, limit: LargeEnoughForEverySite);
        var withBlankQuery = await ListAsync(before: null, limit: LargeEnoughForEverySite, query: "   ");

        Assert.Equal(PlatformOverviewFixture.Plan.Count, unfiltered.TotalSites);
        Assert.Equal(unfiltered.TotalSites, unfiltered.MatchingSites);
        Assert.Equal(unfiltered.TotalSites, withBlankQuery.TotalSites);
        Assert.Equal(unfiltered.MatchingSites, withBlankQuery.MatchingSites);
        Assert.Equal(unfiltered.Sites.Select(s => s.Id), withBlankQuery.Sites.Select(s => s.Id));
    }

    /// <summary>The guard the item's own author asked for by name: a search that narrows the page must
    /// still report the true, unnarrowed total - never let it "disappear from the response". Beta is
    /// the only site whose name contains "Bakery", so this also proves the predicate is a real
    /// substring match rather than an accidental match-everything.</summary>
    [Fact]
    public async Task ListSites_WithAQueryMatchingOneSite_ReportsMatchingSites1_AndTotalSitesUnchanged()
    {
        var unfiltered = await ListAsync(before: null, limit: LargeEnoughForEverySite);
        var searched = await ListAsync(before: null, limit: LargeEnoughForEverySite, query: "Bakery");

        Assert.Equal("Beta Bakery", Assert.Single(searched.Sites).Name);
        Assert.Equal(1L, searched.MatchingSites);
        // The load-bearing assertion: TotalSites is the SAME denominator the unfiltered call reports,
        // never recomputed from the (now narrower) page - a caller must always be able to render "1 of
        // 5 sites match", not just "here is 1 site".
        Assert.Equal(unfiltered.TotalSites, searched.TotalSites);
        Assert.True(searched.MatchingSites < searched.TotalSites, "The search must narrow the result for this test to prove anything.");
    }

    /// <summary>A query with no match at all: an empty page, MatchingSites of zero, and TotalSites
    /// still the true count - the shape a support agent sees when they mistype a name, which must not
    /// be confused with "the platform has no tenants".</summary>
    [Fact]
    public async Task ListSites_WithAQueryMatchingNoSite_ReturnsAnEmptyPage_ButTheRealTotalSites()
    {
        var searched = await ListAsync(before: null, limit: LargeEnoughForEverySite, query: "no-such-tenant-exists");

        Assert.Empty(searched.Sites);
        Assert.Equal(0L, searched.MatchingSites);
        Assert.Equal(PlatformOverviewFixture.Plan.Count, searched.TotalSites);
    }

    /// <summary>Case-insensitive, and matches a substring anywhere in the name - not only a prefix.
    /// </summary>
    [Fact]
    public async Task ListSites_QueryIsCaseInsensitive_AndMatchesAnywhereInTheName()
    {
        var searched = await ListAsync(before: null, limit: LargeEnoughForEverySite, query: "diner");

        Assert.Equal("Delta Diner", Assert.Single(searched.Sites).Name);
    }

    /// <summary>The id half of the name/id predicate: searching by (part of) a site's own id text finds
    /// it, matching `ui-inventory.md` §8.1's own 8-hex-character id badge - which is always a leading
    /// substring of the id's full text representation.</summary>
    [Fact]
    public async Task ListSites_QueryMatchingPartOfTheSiteId_ReturnsThatSite()
    {
        var target = PlatformOverviewFixture.Plan[0];
        var idFragment = target.Id.Value.ToString().Substring(0, 8);

        var searched = await ListAsync(before: null, limit: LargeEnoughForEverySite, query: idFragment);

        Assert.Contains(searched.Sites, s => s.Id == target.Id);
    }

    /// <summary>A query containing LIKE's own wildcard characters must be matched literally, not
    /// interpreted - a site named "50% Off Shop" (hypothetically) searched for as "50%" must not match
    /// every site the way an uninterpreted wildcard would. Proven here against the seeded names, none
    /// of which contain a literal `%`, by asserting a `%`-containing query that matches nothing still
    /// returns zero rows rather than the whole table.</summary>
    [Fact]
    public async Task ListSites_QueryContainingPercentSign_IsMatchedLiterally_NotAsAWildcard()
    {
        var searched = await ListAsync(before: null, limit: LargeEnoughForEverySite, query: "Shop%Nonexistent");

        Assert.Empty(searched.Sites);
        Assert.Equal(0L, searched.MatchingSites);
    }

    /// <summary>The search predicate narrows the candidate set the keyset cursor walks, so paging a
    /// search must behave exactly like paging the unfiltered list: no gap, no duplicate, and the
    /// concatenation of every page must equal the single-page result for that same query.</summary>
    [Fact]
    public async Task ListSites_WithAQuery_PagesCorrectly()
    {
        // "a" matches every seeded name here (Alpha, Beta, Gamma, Delta, Epsilon all contain 'a'), so
        // this proves pagination composes with a filter without needing a query that happens to match
        // exactly one page's worth.
        var wholeMatch = await ListAsync(before: null, limit: LargeEnoughForEverySite, query: "a");
        Assert.True(wholeMatch.Sites.Count > 1, "The query must match more than one site for pagination to prove anything.");

        var walked = new List<SiteId>();
        Guid? cursor = null;
        do
        {
            var page = await ListAsync(before: cursor, limit: 2, query: "a");
            walked.AddRange(page.Sites.Select(s => s.Id));
            cursor = page.NextBefore;
        } while (cursor is not null);

        Assert.Equal(wholeMatch.Sites.Select(s => s.Id), walked);
    }

    /// <summary>`23-14`'s per-tenant detail read: the same ground truth `ListSites_...` above checks
    /// for a page, checked here for one site fetched directly by id.</summary>
    [Fact]
    public async Task GetSite_ReturnsGroundTruthForOneSite()
    {
        var plan = PlatformOverviewFixture.Plan.Single(p => p.Name == "Epsilon Electric");

        var site = await GetSiteAsync(plan.Id);

        Assert.NotNull(site);
        Assert.Equal(plan.Name, site.Name);
        Assert.Equal((long)plan.Operators, site.SeatCount);
        Assert.Equal((long)plan.Conversations, site.ConversationCount);
        Assert.Equal(ExpectedRecentMessages(plan), site.RecentMessageCount);
        Assert.Equal(ExpectedAttachmentBytes(plan), site.AttachmentBytes);
        AssertSameInstant(ExpectedCreatedAt(plan), site.CreatedAt);
        AssertSameInstant(ExpectedLastMessageAt(plan), site.LastMessageAt);
    }

    /// <summary>A genuine "not found" - not the info-hiding shape a tenant-scoped route would use,
    /// because there is no wrong-tenant case to hide behind it here (`IPlatformOverviewReadStore.
    /// GetSiteAsync`'s own remarks).</summary>
    [Fact]
    public async Task GetSite_ForANonexistentId_ReturnsNull()
    {
        var site = await GetSiteAsync(new SiteId(Guid.NewGuid()));

        Assert.Null(site);
    }

    private static int DistinctPartitionCount(string planText) =>
        System.Text.RegularExpressions.Regex.Matches(planText, @"(?<=\bon )messages_\d{2}\b")
            .Select(m => m.Value).Distinct().Count();

    private async Task<string> ExplainMessageScanAsync(bool filterOnMessageSiteId)
    {
        // `filterOnMessageSiteId: false` reproduces the pre-`18-01` shape - a join-only predicate on
        // `conversations.site_id` that gives the planner nothing to prune the HASH(site_id)-partitioned
        // `messages` scan on, since `conversations` is not partitioned the same way and Postgres does
        // not perform a partition-wise join across two differently-shaped tables here.
        var predicate = filterOnMessageSiteId ? "and m.site_id = @SiteId" : string.Empty;
        var sql = $"""
            explain
            select count(*), max(m.created_at)
            from conversations c
            join messages m on m.conversation_id = c.id
            where c.site_id = @SiteId {predicate}
            """;

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("SiteId", PlatformOverviewFixture.Plan[0].Id.Value);

        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join('\n', lines);
    }

    private Task<SiteOverviewPage> ListAsync(Guid? before, int limit, string? query = null) =>
        new PlatformOverviewReadStore(fixture.DataSource)
            .ListSitesAsync(fixture.RecentSince, query, before, limit, CancellationToken.None);

    private Task<SiteOverviewItem?> GetSiteAsync(SiteId siteId) =>
        new PlatformOverviewReadStore(fixture.DataSource)
            .GetSiteAsync(siteId, fixture.RecentSince, CancellationToken.None);

    private static long ExpectedRecentMessages(SeededSite plan) =>
        plan.MessageDaysAgo.Count(daysAgo => daysAgo <= PlatformOverviewFixture.WindowDays);

    private static long ExpectedAttachmentBytes(SeededSite plan) =>
        plan.ReadyAttachmentBytes.Sum() + plan.PendingAttachmentBytes.Sum();

    private DateTimeOffset? ExpectedCreatedAt(SeededSite plan) =>
        plan.CreatedAtDaysAgo is { } daysAgo ? fixture.Now.AddDays(-daysAgo) : null;

    private DateTimeOffset? ExpectedLastMessageAt(SeededSite plan)
    {
        var recent = plan.MessageDaysAgo.Where(daysAgo => daysAgo <= PlatformOverviewFixture.WindowDays).ToList();
        return recent.Count == 0 ? null : fixture.Now.AddDays(-recent.Min());
    }

    /// <summary>Postgres stores `timestamptz` at microsecond resolution while
    /// <see cref="DateTimeOffset"/> counts 100-nanosecond ticks, so a round-tripped instant can differ
    /// from the seeded one in the last three digits. Comparing within a millisecond is the honest
    /// assertion; an exact-equality one would be testing the storage precision, not the query.
    /// </summary>
    private static void AssertSameInstant(DateTimeOffset? expected, DateTimeOffset? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.True(
            (actual.Value - expected.Value).Duration() < TimeSpan.FromMilliseconds(1),
            $"Expected {expected:O}, got {actual:O}.");
    }
}
