using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-263`, against a real Postgres: the invite-list read store correlates a redeemed invite back to its
/// operator (`redeemed_by_operator_id → operators`) so the handler can tell "still in the team" from
/// "removed since". The store returns the raw facts - `RedeemedByOperatorId` and the operator's own
/// `removed_at` - and the effective-status derivation over them is unit-tested in
/// <c>ListOperatorInvitesHandlerTests</c>; this file proves the Dapper join actually reads those columns
/// from the two tables, the layer the SQL lives at.
/// </summary>
[Collection(PostgresCollection.Name)]
public class OperatorInviteListReadStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private OperatorInviteListReadStore Store => new(fixture.DataSource);

    /// <summary>The correlation this slice adds: a redeemed invite whose operator has since been
    /// soft-removed comes back carrying that operator's id AND its `removed_at`, so the handler can render
    /// `Removed` with an «Удалено» date. Fails-before: against `main` the read store selects neither column
    /// (there is no join to `operators` at all), so a removed operator's invite is indistinguishable from a
    /// still-active one.</summary>
    [Fact]
    public async Task ListForSiteAsync_ARedeemedInviteWhoseOperatorWasRemoved_CarriesTheOperatorAndItsRemovedAt()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var roleId = Guid.NewGuid();
        var operatorId = new OperatorId(Guid.NewGuid());
        var redeemedAt = Now.AddDays(-3);
        var removedAt = Now.AddHours(-2);

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Operator", Permissions = [] });
            var removed = new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5, displayName: "Gone Away");
            removed.Remove(removedAt);
            db.Operators.Add(removed);

            var invite = OperatorInvite.Generate(
                new OperatorInviteId(Guid.NewGuid()), siteId, [roleId],
                SHA256.HashData(Encoding.UTF8.GetBytes($"code-{Guid.NewGuid():N}")),
                "removed@example.invalid", new OperatorId(Guid.NewGuid()), Now.AddDays(-5), TimeSpan.FromDays(7));
            invite.Redeem(operatorId, redeemedAt);
            db.OperatorInvites.Add(invite);
            await db.SaveChangesAsync();
        }

        var row = Assert.Single(await Store.ListForSiteAsync(siteId, CancellationToken.None));

        Assert.Equal(operatorId.Value, row.RedeemedByOperatorId);
        Assert.Equal(removedAt, row.RedeemedOperatorRemovedAt);
        Assert.Equal(redeemedAt, row.RedeemedAt);
    }

    [Fact]
    public async Task ListForSiteAsync_ARedeemedInviteWhoseOperatorIsStillActive_CarriesTheOperatorButNoRemovedAt()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var roleId = Guid.NewGuid();
        var operatorId = new OperatorId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Operator", Permissions = [] });
            db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5, displayName: "Still Here"));

            var invite = OperatorInvite.Generate(
                new OperatorInviteId(Guid.NewGuid()), siteId, [roleId],
                SHA256.HashData(Encoding.UTF8.GetBytes($"code-{Guid.NewGuid():N}")),
                "active@example.invalid", new OperatorId(Guid.NewGuid()), Now.AddDays(-2), TimeSpan.FromDays(7));
            invite.Redeem(operatorId, Now.AddDays(-1));
            db.OperatorInvites.Add(invite);
            await db.SaveChangesAsync();
        }

        var row = Assert.Single(await Store.ListForSiteAsync(siteId, CancellationToken.None));

        Assert.Equal(operatorId.Value, row.RedeemedByOperatorId);
        Assert.Null(row.RedeemedOperatorRemovedAt);
    }

    [Fact]
    public async Task ListForSiteAsync_AnUnredeemedInvite_CarriesNoRedeemerAtAll()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var roleId = Guid.NewGuid();

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            db.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Operator", Permissions = [] });

            var invite = OperatorInvite.Generate(
                new OperatorInviteId(Guid.NewGuid()), siteId, [roleId],
                SHA256.HashData(Encoding.UTF8.GetBytes($"code-{Guid.NewGuid():N}")),
                "pending@example.invalid", new OperatorId(Guid.NewGuid()), Now, TimeSpan.FromDays(7));
            db.OperatorInvites.Add(invite);
            await db.SaveChangesAsync();
        }

        var row = Assert.Single(await Store.ListForSiteAsync(siteId, CancellationToken.None));

        Assert.Null(row.RedeemedByOperatorId);
        Assert.Null(row.RedeemedOperatorRemovedAt);
        Assert.Null(row.RedeemedAt);
    }
}
