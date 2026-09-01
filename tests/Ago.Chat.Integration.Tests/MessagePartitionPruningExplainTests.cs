using System.Text.RegularExpressions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `15-09`/`adr/0087`'s own central claim, proven by `EXPLAIN` against a real Postgres rather than
/// asserted: a conversation-history read and a tenant search both prune `messages` to exactly one of its
/// 64 `HASH (site_id)` buckets. A green suite alone proves nothing about whether this item achieved
/// anything - `adr/0087`'s whole argument is that the *old* scheme (`RANGE (created_at)`, then
/// `LIST (retention_class)` on top of it) let both of these queries return correct results while
/// touching every partition, silently. This file is what tells the two apart.
///
/// <para><b>Why this reads `ConversationReadStore.Sql`/`ConversationSearchStore.Sql` directly
/// (`internal`, exposed via this project's own `InternalsVisibleTo`) instead of re-typing the query.</b>
/// A hand-copied approximation of the production SQL could drift from what actually ships and this test
/// would keep passing regardless - the whole point is to prove the query Postgres actually plans for a
/// real caller, not a caller this test invented.</para>
///
/// <para><b>How "exactly one partition" is read off `EXPLAIN`'s own output.</b> Each partition Postgres
/// did not prune shows up as its own scan node naming the leaf table, `messages_NN`. Postgres performs
/// this pruning at plan time for a literal-valued equality predicate on the partition key - no special
/// `Subplans Removed` runtime-pruning notice is needed for a plan built directly against a bound
/// parameter value the way `NpgsqlCommand.ExecuteReaderAsync` supplies here. Counting the *distinct* set
/// of `messages_NN` names line-by-line in the plan text is therefore a direct, mechanical count of how
/// many partitions the plan actually touches.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class MessagePartitionPruningExplainTests(PostgresFixture fixture)
{
    // Matches only the *real* relation name a scan node names - immediately after "on " in EXPLAIN's
    // text format ("Index Scan ... using ix_name on messages_07 messages_1"). Deliberately not a bare
    // `messages_\d{2}` search: Postgres disambiguates repeated child aliases across a many-partition
    // Append/Merge Append by inventing alias text of its own (e.g. "messages_1", "messages_64") that can
    // itself coincidentally match a two-digit bucket-name pattern without naming a real partition at
    // all - found by this test's own negative control initially over-counting by exactly one for that
    // reason.
    private static readonly Regex PartitionNamePattern = new(@"(?<=\bon )messages_\d{2}\b", RegexOptions.Compiled);

    [Fact]
    public async Task ConversationHistoryQuery_TouchesExactlyOnePartition()
    {
        var (siteId, conversationId) = await SeedConversationWithMessagesAsync(messageCount: 3);

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("EXPLAIN (FORMAT TEXT) " + ConversationReadStore.Sql, connection);
        command.Parameters.AddWithValue("ConversationId", conversationId.Value);
        command.Parameters.AddWithValue("SiteId", siteId.Value);
        command.Parameters.Add(new NpgsqlParameter("BeforeSequence", NpgsqlDbType.Integer) { Value = DBNull.Value });
        command.Parameters.AddWithValue("PageSize", 10);

        var plan = await ReadPlanTextAsync(command);
        var touchedPartitions = DistinctPartitionNames(plan);

        Assert.True(
            touchedPartitions.Count == 1,
            $"Expected GetHistoryAsync's query to touch exactly one messages_NN partition, touched {touchedPartitions.Count}: [{string.Join(", ", touchedPartitions)}].\nPlan:\n{plan}");
    }

    [Fact]
    public async Task ConversationSearchQuery_TouchesExactlyOnePartition()
    {
        var siteId = await SeedSiteWithSearchableMessagesAsync();

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("EXPLAIN (FORMAT TEXT) " + ConversationSearchStore.Sql, connection);
        command.Parameters.AddWithValue("SiteId", siteId.Value);
        command.Parameters.AddWithValue("Phrase", "hello");
        command.Parameters.AddWithValue("From", new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
        command.Parameters.AddWithValue("To", new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        command.Parameters.Add(new NpgsqlParameter("BeforeMessageId", NpgsqlDbType.Uuid) { Value = DBNull.Value });
        command.Parameters.AddWithValue("PageSize", 10);

        var plan = await ReadPlanTextAsync(command);
        var touchedPartitions = DistinctPartitionNames(plan);

        Assert.True(
            touchedPartitions.Count == 1,
            $"Expected SearchAsync's query to touch exactly one messages_NN partition, touched {touchedPartitions.Count}: [{string.Join(", ", touchedPartitions)}].\nPlan:\n{plan}");
    }

    /// <summary>The negative control for both tests above: a query with no `site_id` predicate at all -
    /// what `GetHistoryAsync`/`GetDeltaAsync` looked like before `15-09` - genuinely touches every
    /// bucket, proving the assertion above is not vacuously true (a broken `EXPLAIN` parse or an
    /// accidentally-empty table would also report "one partition" for the wrong reason). This is this
    /// item's own fails-before evidence, reproduced live on the *current* schema rather than only
    /// reported from a separate manual run against the pre-`15-09` code.</summary>
    [Fact]
    public async Task AQueryWithNoSiteIdPredicate_TouchesEveryPartition()
    {
        await SeedConversationWithMessagesAsync(messageCount: 1);

        const string unscopedSql = """
            select id, sequence
            from messages
            where conversation_id = @ConversationId
            order by sequence desc
            limit 10
            """;

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("EXPLAIN (FORMAT TEXT) " + unscopedSql, connection);
        command.Parameters.AddWithValue("ConversationId", Guid.NewGuid());

        var plan = await ReadPlanTextAsync(command);
        var touchedPartitions = DistinctPartitionNames(plan);

        Assert.True(
            touchedPartitions.Count == MessagePartitionNames.BucketCount,
            $"Expected the unscoped query to touch all {MessagePartitionNames.BucketCount} buckets (the pre-15-09 failure mode), touched {touchedPartitions.Count}.\nPlan:\n{plan}");
    }

    private async Task<(SiteId SiteId, ConversationId ConversationId)> SeedConversationWithMessagesAsync(int messageCount)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, now);
        for (var i = 0; i < messageCount; i++)
        {
            conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody($"message {i}"), now);
        }

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, now));
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        return (siteId, conversation.Id);
    }

    private async Task<SiteId> SeedSiteWithSearchableMessagesAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, now);
        conversation.AddVisitorMessage(visitorId, new MessageId(Guid.NewGuid()), new MessageBody("hello there"), now);

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, now));
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        return siteId;
    }

    private static async Task<string> ReadPlanTextAsync(NpgsqlCommand command)
    {
        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join('\n', lines);
    }

    private static IReadOnlyCollection<string> DistinctPartitionNames(string planText) =>
        PartitionNamePattern.Matches(planText).Select(m => m.Value).Distinct().ToList();
}
