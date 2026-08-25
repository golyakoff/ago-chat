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
    /// The partitioning claim the whole bounded-window design rests on, checked against a real
    /// planner instead of asserted in a comment: with the window predicate in place, Postgres does
    /// not read the partitions that fall entirely outside it. The fixture seeds messages 95, 60, 45
    /// and 40 days back specifically so that older partitions exist to be skipped.
    ///
    /// <para>This is a <i>structural</i> check, not a performance claim - it says which partitions
    /// appear in the plan, and deliberately measures no timing (`CLAUDE.md`: measure or stay silent).
    /// The same `EXPLAIN` without the predicate is run alongside it, so the test fails just as loudly
    /// if the old partitions were never in the plan to begin with, which would make the first half
    /// prove nothing.</para>
    /// </summary>
    [Fact]
    public async Task TheRecentWindowPredicate_KeepsOlderMessagePartitionsOutOfThePlan()
    {
        var oldPartition = $"messages_{fixture.Now.AddDays(-95):yyyy_MM}";

        var boundedPlan = await ExplainMessageScanAsync(withWindow: true);
        var unboundedPlan = await ExplainMessageScanAsync(withWindow: false);

        Assert.Contains(oldPartition, unboundedPlan, StringComparison.Ordinal);
        Assert.DoesNotContain(oldPartition, boundedPlan, StringComparison.Ordinal);
    }

    private async Task<string> ExplainMessageScanAsync(bool withWindow)
    {
        var window = withWindow ? "and m.created_at >= @RecentSince" : string.Empty;
        var sql = $"""
            explain
            select count(*), max(m.created_at)
            from conversations c
            join messages m on m.conversation_id = c.id
            where c.site_id = @SiteId {window}
            """;

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("SiteId", PlatformOverviewFixture.Plan[0].Id.Value);
        command.Parameters.AddWithValue("RecentSince", fixture.RecentSince);

        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join('\n', lines);
    }

    private Task<SiteOverviewPage> ListAsync(Guid? before, int limit) =>
        new PlatformOverviewReadStore(fixture.DataSource)
            .ListSitesAsync(fixture.RecentSince, before, limit, CancellationToken.None);

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
