using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Dapper;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-11`'s own Done-when, against a real Postgres (<see cref="PostgresFixture"/>): a reveal actually
/// writes a row; the row holds nothing about the value that was revealed; and a tenant's own read sees
/// only their own site's rows - the same tenant-isolation discipline
/// <c>AccessRecordRepositoryTests</c> already establishes for its sibling table.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ContactRevealRepositoryTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // The exact, ordered column set `contact_reveals` is allowed to have - a positive assertion, the
    // same discipline `AccessRecordRepositoryTests`'s own shape test applies to itself. A future column
    // added without updating this list fails this test immediately.
    private static readonly string[] ExpectedColumns =
    [
        "contact_detail_id",
        "conversation_id",
        "id",
        "occurred_at",
        "operator_id",
        "site_id",
        "surface",
    ];

    [Fact]
    public async Task ContactReveals_HasExactlyTheColumnsThisItemDecidedOn()
    {
        var columns = (await QueryColumnsAsync()).OrderBy(c => c, StringComparer.Ordinal).ToArray();
        Assert.Equal(ExpectedColumns, columns);
    }

    [Fact]
    public async Task RecordAsync_WritesARow_NamingWhoWhatAndWhen_ButNeverTheRevealedValue()
    {
        var repository = new ContactRevealRepository(fixture.DataSource);
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var conversationId = Guid.NewGuid();
        var contactDetailId = Guid.NewGuid();
        var recordId = Guid.NewGuid();

        await repository.RecordAsync(
            new ContactRevealToWrite(recordId, Now, siteId, conversationId, contactDetailId, operatorId, "ConsoleContactPanel"),
            CancellationToken.None);

        var row = await QuerySingleAsync(recordId);
        Assert.Equal(siteId.Value, (Guid)row.site_id);
        Assert.Equal(conversationId, (Guid)row.conversation_id);
        Assert.Equal(contactDetailId, (Guid)row.contact_detail_id);
        Assert.Equal(operatorId.Value, (Guid)row.operator_id);
        Assert.Equal("ConsoleContactPanel", (string)row.surface);

        // `IContactRevealRepository`'s own "record that a reveal happened, not what was revealed": the
        // row has exactly the columns above (proven positively by the shape test) and none of them is,
        // or could hold, the contact detail's own value.
        IDictionary<string, object> values = row;
        Assert.DoesNotContain("value", values.Keys);
        Assert.DoesNotContain("phone", values.Keys);
    }

    [Fact]
    public async Task ListForSiteAsync_ReturnsOnlyThisSitesOwnRows_NeverAnotherSites()
    {
        var repository = new ContactRevealRepository(fixture.DataSource);
        var siteId = new SiteId(Guid.NewGuid());
        var otherSiteId = new SiteId(Guid.NewGuid());
        var thisSitesOperatorId = new OperatorId(Guid.NewGuid());
        var otherSitesOperatorId = new OperatorId(Guid.NewGuid());

        await repository.RecordAsync(
            new ContactRevealToWrite(
                Guid.NewGuid(), Now, siteId, Guid.NewGuid(), Guid.NewGuid(), thisSitesOperatorId, "ConsoleContactPanel"),
            CancellationToken.None);
        await repository.RecordAsync(
            new ContactRevealToWrite(
                Guid.NewGuid(), Now, otherSiteId, Guid.NewGuid(), Guid.NewGuid(), otherSitesOperatorId, "ConsoleContactPanel"),
            CancellationToken.None);

        var page = await repository.ListForSiteAsync(siteId, beforeId: null, limit: 50, CancellationToken.None);

        // The load-bearing claim: this tenant's own read never returns the other tenant's row.
        var item = Assert.Single(page.Items);
        Assert.Equal(thisSitesOperatorId.Value, item.OperatorId);
        Assert.DoesNotContain(page.Items, i => i.OperatorId == otherSitesOperatorId.Value);
    }

    [Fact]
    public async Task ListForSiteAsync_PagesWithBeforeId_NewestFirst_WithoutGapOrDuplicate()
    {
        var repository = new ContactRevealRepository(fixture.DataSource);
        var siteId = new SiteId(Guid.NewGuid());

        // IIdGenerator's own contract ("ids sort in generation order") is what real callers rely on for
        // keyset paging - reproduced here with three ids minted in increasing order, the same
        // `AccessRecordRepositoryTests`'s own precedent.
        var firstId = Guid.Parse("00000000-0000-7000-8000-000000000001");
        var secondId = Guid.Parse("00000000-0000-7000-8000-000000000002");
        var thirdId = Guid.Parse("00000000-0000-7000-8000-000000000003");

        foreach (var id in new[] { firstId, secondId, thirdId })
        {
            await repository.RecordAsync(
                new ContactRevealToWrite(
                    id, Now, siteId, Guid.NewGuid(), Guid.NewGuid(), new OperatorId(Guid.NewGuid()), "ConsoleContactPanel"),
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
            "select column_name from information_schema.columns where table_name = 'contact_reveals'");
    }

    private async Task<dynamic> QuerySingleAsync(Guid id)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        return await connection.QuerySingleAsync("select * from contact_reveals where id = @id", new { id });
    }
}
