using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-22`'s own Done-when, against a real Postgres: every active operator on a site, by name, with
/// which hold seats - and never another site's row, the same tenant-isolation proof
/// <see cref="OperatorAnalyticsReadStoreTests"/> and its siblings already give for the identical
/// table.
/// </summary>
[Collection(PostgresCollection.Name)]
public class OperatorTeamReadStoreTests(PostgresFixture fixture)
{
    private OperatorTeamReadStore Store => new(fixture.DataSource);

    [Fact]
    public async Task GetForSiteAsync_ReturnsEveryActiveOperator_WithNameEmailAndSeatStatus()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var named = new OperatorId(Guid.NewGuid());
        var seatless = new OperatorId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Operators.Add(new Operator(
                named, siteId, OperatorStatus.Offline, capacity: 5,
                displayName: "Ada Lovelace", email: "ada@example.invalid"));
            db.Operators.Add(new Operator(
                seatless, siteId, OperatorStatus.Offline, capacity: 5, holdsSeat: false));
            await db.SaveChangesAsync();
        }

        var rows = await Store.GetForSiteAsync(siteId, CancellationToken.None);

        Assert.Equal(2, rows.Count);

        var adaRow = Assert.Single(rows, r => r.OperatorId == named);
        Assert.Equal("Ada Lovelace", adaRow.DisplayName);
        Assert.Equal("ada@example.invalid", adaRow.Email);
        Assert.True(adaRow.HoldsSeat);

        var seatlessRow = Assert.Single(rows, r => r.OperatorId == seatless);
        Assert.Null(seatlessRow.DisplayName);
        Assert.Null(seatlessRow.Email);
        Assert.False(seatlessRow.HoldsSeat);
    }

    /// <summary>The regression this query exists to avoid: a removed operator resolves to no
    /// `OperatorId` claim anywhere else in the product (`authorization.md`'s own "seat assignment
    /// blocks sign-in" section), and this list must agree - a tenant re-inviting into a "removed"
    /// row's old seat must not see a ghost still occupying it.</summary>
    [Fact]
    public async Task GetForSiteAsync_ExcludesARemovedOperator()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var stillActive = new OperatorId(Guid.NewGuid());
        var removedId = new OperatorId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Operators.Add(new Operator(stillActive, siteId, OperatorStatus.Offline, capacity: 5));
            var removed = new Operator(removedId, siteId, OperatorStatus.Offline, capacity: 5);
            removed.Remove(new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero));
            db.Operators.Add(removed);
            await db.SaveChangesAsync();
        }

        var rows = await Store.GetForSiteAsync(siteId, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(stillActive, row.OperatorId);
    }

    /// <summary>Done-when: "a tenant cannot see... another tenant's operators" - proven here at the
    /// read store's own level, the layer the SQL's `WHERE site_id = @SiteId` actually lives at.</summary>
    [Fact]
    public async Task GetForSiteAsync_NeverReturnsAnotherSitesOperator()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var otherSiteId = new SiteId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Sites.Add(new Site(otherSiteId, $"site_{otherSiteId.Value:N}", []));
            db.Operators.Add(new Operator(new OperatorId(Guid.NewGuid()), siteId, OperatorStatus.Offline, capacity: 5));
            db.Operators.Add(new Operator(
                new OperatorId(Guid.NewGuid()), otherSiteId, OperatorStatus.Offline, capacity: 5,
                displayName: "Someone Else's Operator"));
            await db.SaveChangesAsync();
        }

        var rows = await Store.GetForSiteAsync(siteId, CancellationToken.None);

        Assert.Single(rows);
        Assert.DoesNotContain(rows, r => r.DisplayName == "Someone Else's Operator");
    }
}
