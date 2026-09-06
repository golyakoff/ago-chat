using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Dapper;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `24-10`'s own core mechanism, against a real Postgres: <see cref="ConversationBlockRepository"/>'s
/// atomic block/unblock, its three-way outcome, and the append-only <c>conversation_block_records</c>
/// audit trail - "reversible, and recorded: who blocked, when, on what request" (the backlog item's own
/// Scope), proven against real rows rather than the SQL text looking right.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ConversationBlockRepositoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private ConversationBlockRepository Repository => new(fixture.DataSource);

    private async Task<(SiteId SiteId, ConversationId ConversationId)> SeedConversation()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var visitorId = new VisitorId(Guid.NewGuid());
        var conversation = Conversation.Start(new ConversationId(Guid.NewGuid()), siteId, visitorId, Now);

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Visitors.Add(new Visitor(visitorId, siteId, Now));
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        return (siteId, conversation.Id);
    }

    private async Task<int> CountBlockRecordsAsync(ConversationId conversationId, ConversationBlockRecordKind kind)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.ExecuteScalarAsync<int>(
            "select count(*) from conversation_block_records where conversation_id = @id and kind = @kind",
            new { id = conversationId.Value, kind = kind.ToString() });
    }

    private async Task<(DateTimeOffset? BlockedAt, Guid? BlockedBy)> ReadCurrentStateAsync(ConversationId conversationId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var row = await connection.QuerySingleAsync<(DateTimeOffset? blocked_at, Guid? blocked_by)>(
            "select blocked_at, blocked_by from conversations where id = @id", new { id = conversationId.Value });
        return (row.blocked_at, row.blocked_by);
    }

    [Fact]
    public async Task BlockAsync_OnAnExistingUnblockedConversation_SetsBlockedAtAndBlockedBy_AndReturnsApplied()
    {
        var (siteId, conversationId) = await SeedConversation();
        var blockedBy = new OperatorId(Guid.NewGuid());
        var recordId = Guid.NewGuid();

        var outcome = await Repository.BlockAsync(conversationId, siteId, blockedBy, recordId, Now, CancellationToken.None);

        Assert.Equal(ConversationBlockOutcome.Applied, outcome);
        var (blockedAt, blockedByColumn) = await ReadCurrentStateAsync(conversationId);
        Assert.Equal(Now, blockedAt);
        Assert.Equal(blockedBy.Value, blockedByColumn);
    }

    // `24-10`'s own Done-when: "both acts are recorded" - the block itself is an act with its own row,
    // not only a flag flip.
    [Fact]
    public async Task BlockAsync_WritesOneConversationBlockRecord_OfKindBlocked()
    {
        var (siteId, conversationId) = await SeedConversation();
        var blockedBy = new OperatorId(Guid.NewGuid());

        await Repository.BlockAsync(conversationId, siteId, blockedBy, Guid.NewGuid(), Now, CancellationToken.None);

        Assert.Equal(1, await CountBlockRecordsAsync(conversationId, ConversationBlockRecordKind.Blocked));
        Assert.Equal(0, await CountBlockRecordsAsync(conversationId, ConversationBlockRecordKind.Unblocked));
    }

    [Fact]
    public async Task BlockAsync_OnAConversationThatIsAlreadyBlocked_ReturnsAlreadyInState_AndChangesNothing()
    {
        var (siteId, conversationId) = await SeedConversation();
        var firstBlockedBy = new OperatorId(Guid.NewGuid());
        await Repository.BlockAsync(conversationId, siteId, firstBlockedBy, Guid.NewGuid(), Now, CancellationToken.None);

        var secondOutcome = await Repository.BlockAsync(
            conversationId, siteId, new OperatorId(Guid.NewGuid()), Guid.NewGuid(), Now.AddMinutes(5), CancellationToken.None);

        Assert.Equal(ConversationBlockOutcome.AlreadyInState, secondOutcome);
        var (blockedAt, blockedByColumn) = await ReadCurrentStateAsync(conversationId);
        // The original block wins - a second, redundant request neither overwrites the timestamp nor
        // reassigns responsibility to whoever happened to ask twice.
        Assert.Equal(Now, blockedAt);
        Assert.Equal(firstBlockedBy.Value, blockedByColumn);
        Assert.Equal(1, await CountBlockRecordsAsync(conversationId, ConversationBlockRecordKind.Blocked));
    }

    [Fact]
    public async Task BlockAsync_OnAConversationThatDoesNotExist_ReturnsNotFound()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var missingConversationId = new ConversationId(Guid.NewGuid());

        var outcome = await Repository.BlockAsync(
            missingConversationId, siteId, new OperatorId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);

        Assert.Equal(ConversationBlockOutcome.NotFound, outcome);
    }

    // The same not-found-not-forbidden cross-tenant guard IErasureRequestRepository's own remarks
    // establish - existence is never leaked cross-tenant.
    [Fact]
    public async Task BlockAsync_WhenTheConversationBelongsToADifferentSite_ReturnsNotFound()
    {
        var (_, conversationId) = await SeedConversation();
        var otherSiteId = new SiteId(Guid.NewGuid());

        var outcome = await Repository.BlockAsync(
            conversationId, otherSiteId, new OperatorId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);

        Assert.Equal(ConversationBlockOutcome.NotFound, outcome);
    }

    [Fact]
    public async Task UnblockAsync_OnABlockedConversation_ClearsBlockedAtAndBlockedBy_AndReturnsApplied()
    {
        var (siteId, conversationId) = await SeedConversation();
        await Repository.BlockAsync(conversationId, siteId, new OperatorId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);

        var outcome = await Repository.UnblockAsync(
            conversationId, siteId, new OperatorId(Guid.NewGuid()), Guid.NewGuid(), Now.AddHours(1), CancellationToken.None);

        Assert.Equal(ConversationBlockOutcome.Applied, outcome);
        var (blockedAt, blockedByColumn) = await ReadCurrentStateAsync(conversationId);
        Assert.Null(blockedAt);
        Assert.Null(blockedByColumn);
    }

    [Fact]
    public async Task UnblockAsync_WritesOneConversationBlockRecord_OfKindUnblocked_AlongsideTheOriginalBlockedOne()
    {
        var (siteId, conversationId) = await SeedConversation();
        await Repository.BlockAsync(conversationId, siteId, new OperatorId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);

        await Repository.UnblockAsync(
            conversationId, siteId, new OperatorId(Guid.NewGuid()), Guid.NewGuid(), Now.AddHours(1), CancellationToken.None);

        // Both acts stand, side by side - unblocking does not overwrite or delete the record of having
        // been blocked in the first place (ConversationBlockRecordKind's own remarks: "one row per act,
        // never per state").
        Assert.Equal(1, await CountBlockRecordsAsync(conversationId, ConversationBlockRecordKind.Blocked));
        Assert.Equal(1, await CountBlockRecordsAsync(conversationId, ConversationBlockRecordKind.Unblocked));
    }

    [Fact]
    public async Task UnblockAsync_OnAConversationThatIsNotBlocked_ReturnsAlreadyInState_AndWritesNoRecord()
    {
        var (siteId, conversationId) = await SeedConversation();

        var outcome = await Repository.UnblockAsync(
            conversationId, siteId, new OperatorId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);

        Assert.Equal(ConversationBlockOutcome.AlreadyInState, outcome);
        Assert.Equal(0, await CountBlockRecordsAsync(conversationId, ConversationBlockRecordKind.Unblocked));
    }

    [Fact]
    public async Task UnblockAsync_OnAConversationThatDoesNotExist_ReturnsNotFound()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var missingConversationId = new ConversationId(Guid.NewGuid());

        var outcome = await Repository.UnblockAsync(
            missingConversationId, siteId, new OperatorId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);

        Assert.Equal(ConversationBlockOutcome.NotFound, outcome);
    }

    /// <summary>`24-10`'s own defense-in-depth: a conversation's block records are its own operational
    /// history, not evidence meant to outlive it (unlike `erasure_records`/`access_records` -
    /// `ConversationBlockRecordEntity`'s own remarks on why this table carries a real, cascading FK).
    /// Erasing the conversation must take its block records with it.</summary>
    [Fact]
    public async Task ErasingTheConversation_CascadesToItsOwnBlockRecords()
    {
        var (siteId, conversationId) = await SeedConversation();
        await Repository.BlockAsync(conversationId, siteId, new OperatorId(Guid.NewGuid()), Guid.NewGuid(), Now, CancellationToken.None);
        await Repository.UnblockAsync(conversationId, siteId, new OperatorId(Guid.NewGuid()), Guid.NewGuid(), Now.AddHours(1), CancellationToken.None);

        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        {
            await connection.ExecuteAsync("delete from conversations where id = @id", new { id = conversationId.Value });
        }

        Assert.Equal(0, await CountBlockRecordsAsync(conversationId, ConversationBlockRecordKind.Blocked));
        Assert.Equal(0, await CountBlockRecordsAsync(conversationId, ConversationBlockRecordKind.Unblocked));
    }
}
