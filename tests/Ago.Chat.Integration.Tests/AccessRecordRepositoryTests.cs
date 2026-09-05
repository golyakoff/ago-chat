using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Dapper;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `24-12`'s own Done-when, against a real Postgres (<see cref="PostgresFixture"/>): a boundary-crossing
/// access actually writes a row; the row holds nothing about what was read; and a tenant's own read
/// sees only their own site's rows - the tenant-isolation assertion this item's own brief calls
/// "worthless as anything but a demonstration", proven here rather than merely asserted.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AccessRecordRepositoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // The exact, ordered column set `access_records` is allowed to have - a positive assertion, not
    // merely "these forbidden names are absent" - the same discipline `erasure_records`'s own shape
    // test (`ErasureRecordIntegrationTests`) applies to itself. A future column added without updating
    // this list fails this test immediately.
    private static readonly string[] ExpectedColumns =
    [
        "access_kind",
        "actor_id",
        "actor_kind",
        "id",
        "occurred_at",
        "resource_id",
        "resource_kind",
        "site_id",
    ];

    [Fact]
    public async Task AccessRecords_HasExactlyTheColumnsThisItemDecidedOn()
    {
        var columns = (await QueryColumnsAsync()).OrderBy(c => c, StringComparer.Ordinal).ToArray();
        Assert.Equal(ExpectedColumns, columns);
    }

    [Fact]
    public async Task RecordAsync_WritesARow_NamingWhoWhatAndWhen_ButNeverAnyContent()
    {
        var repository = new AccessRecordRepository(fixture.DataSource);
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = new ConversationId(Guid.NewGuid());
        var recordId = Guid.NewGuid();

        await repository.RecordAsync(
            new AccessRecordToWrite(
                recordId, Now, AccessRecordKind.CrossConversationHistoryRead, siteId, AccessRecordActorKind.Operator,
                operatorId.Value.ToString(), AccessRecordResourceKind.Conversation, conversationId.Value),
            CancellationToken.None);

        var row = await QuerySingleAsync(recordId);
        Assert.Equal("CrossConversationHistoryRead", (string)row.access_kind);
        Assert.Equal(siteId.Value, (Guid)row.site_id);
        Assert.Equal("Operator", (string)row.actor_kind);
        Assert.Equal(operatorId.Value.ToString(), (string)row.actor_id);
        Assert.Equal("Conversation", (string)row.resource_kind);
        Assert.Equal(conversationId.Value, (Guid)row.resource_id);

        // `24-12`'s own "record that a read happened and by whom - not what was returned": the row has
        // exactly the columns above (proven positively by the shape test) and none of them is, or could
        // hold, a message body, a preview, or any other copy of what the access actually returned.
        IDictionary<string, object> values = row;
        Assert.DoesNotContain("body", values.Keys);
        Assert.DoesNotContain("preview", values.Keys);
        Assert.DoesNotContain("content", values.Keys);
    }

    [Fact]
    public async Task RecordAsync_WithNoSingleSiteOrResource_WritesNullSiteAndResourceColumns()
    {
        // `AccessRecordKind.OwnerSiteList`'s own shape: the platform owner's cross-tenant read has no
        // single site and no single resource.
        var repository = new AccessRecordRepository(fixture.DataSource);
        var recordId = Guid.NewGuid();

        await repository.RecordAsync(
            new AccessRecordToWrite(
                recordId, Now, AccessRecordKind.OwnerSiteList, SiteId: null, AccessRecordActorKind.PlatformOwner,
                "keycloak-owner-subject", ResourceKind: null, ResourceId: null),
            CancellationToken.None);

        var row = await QuerySingleAsync(recordId);
        Assert.Null((Guid?)row.site_id);
        Assert.Null((string?)row.resource_kind);
        Assert.Null((Guid?)row.resource_id);
    }

    [Fact]
    public async Task ListForSiteAsync_ReturnsOnlyThisSitesOwnRows_NeverAnotherSites()
    {
        var repository = new AccessRecordRepository(fixture.DataSource);
        var siteId = new SiteId(Guid.NewGuid());
        var otherSiteId = new SiteId(Guid.NewGuid());
        var thisSitesActorId = Guid.NewGuid().ToString();
        var otherSitesActorId = Guid.NewGuid().ToString();

        await repository.RecordAsync(
            new AccessRecordToWrite(
                Guid.NewGuid(), Now, AccessRecordKind.CrossConversationHistoryRead, siteId, AccessRecordActorKind.Operator,
                thisSitesActorId, AccessRecordResourceKind.Conversation, Guid.NewGuid()),
            CancellationToken.None);
        await repository.RecordAsync(
            new AccessRecordToWrite(
                Guid.NewGuid(), Now, AccessRecordKind.CrossConversationHistoryRead, otherSiteId, AccessRecordActorKind.Operator,
                otherSitesActorId, AccessRecordResourceKind.Conversation, Guid.NewGuid()),
            CancellationToken.None);

        var page = await repository.ListForSiteAsync(siteId, beforeId: null, limit: 50, CancellationToken.None);

        // The load-bearing claim: this tenant's own read never returns the other tenant's row, however
        // many rows either site has - the shared-database "worthless as anything but a demonstration"
        // guard this item's own brief names explicitly.
        var item = Assert.Single(page.Items);
        Assert.Equal(thisSitesActorId, item.ActorId);
        Assert.DoesNotContain(page.Items, i => i.ActorId == otherSitesActorId);
    }

    [Fact]
    public async Task ListForSiteAsync_PagesWithBeforeId_NewestFirst_WithoutGapOrDuplicate()
    {
        var repository = new AccessRecordRepository(fixture.DataSource);
        var siteId = new SiteId(Guid.NewGuid());

        // IIdGenerator's own contract ("ids sort in generation order") is what real callers rely on for
        // keyset paging - reproduced here with three ids minted in increasing order rather than a real
        // IIdGenerator, since only the ordering (not the exact algorithm) is this test's own subject.
        var firstId = Guid.Parse("00000000-0000-7000-8000-000000000001");
        var secondId = Guid.Parse("00000000-0000-7000-8000-000000000002");
        var thirdId = Guid.Parse("00000000-0000-7000-8000-000000000003");

        foreach (var id in new[] { firstId, secondId, thirdId })
        {
            await repository.RecordAsync(
                new AccessRecordToWrite(
                    id, Now, AccessRecordKind.CrossConversationHistoryRead, siteId, AccessRecordActorKind.Operator,
                    Guid.NewGuid().ToString(), AccessRecordResourceKind.Conversation, Guid.NewGuid()),
                CancellationToken.None);
        }

        var firstPage = await repository.ListForSiteAsync(siteId, beforeId: null, limit: 2, CancellationToken.None);
        Assert.Equal([thirdId, secondId], firstPage.Items.Select(i => i.Id));
        Assert.Equal(secondId, firstPage.NextBeforeId);

        var secondPage = await repository.ListForSiteAsync(siteId, firstPage.NextBeforeId, limit: 2, CancellationToken.None);
        Assert.Equal([firstId], secondPage.Items.Select(i => i.Id));
        Assert.Null(secondPage.NextBeforeId);
    }

    private async Task<IEnumerable<string>> QueryColumnsAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.QueryAsync<string>(
            "select column_name from information_schema.columns where table_name = 'access_records'");
    }

    private async Task<dynamic> QuerySingleAsync(Guid id)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync("select * from access_records where id = @id", new { id });
    }
}
