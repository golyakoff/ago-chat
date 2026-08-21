using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;

namespace Ago.Chat.Integration.Tests;

[Collection(PostgresCollection.Name)]
public class PermissionCheckerTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private async Task SeedSiteAndOperator(SiteId siteId, OperatorId operatorId)
    {
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task HasPermissionAsync_WhenTheOperatorsRoleGrantsIt_ReturnsTrue()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        var roleId = Guid.NewGuid();
        await SeedSiteAndOperator(siteId, operatorId);

        await using (var db = fixture.CreateDbContext())
        {
            db.Roles.Add(new RoleRecord
            {
                Id = roleId,
                SiteId = siteId,
                Name = "Operator",
                Permissions = [Permission.ConversationSend.Value, Permission.ConversationRead.Value],
            });
            db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
            await db.SaveChangesAsync();
        }

        await using var readDb = fixture.CreateDbContext();
        var checker = new PermissionChecker(readDb);

        Assert.True(await checker.HasPermissionAsync(operatorId, siteId, Permission.ConversationSend, CancellationToken.None));
        Assert.False(await checker.HasPermissionAsync(operatorId, siteId, Permission.ConversationAssign, CancellationToken.None));
    }

    [Fact]
    public async Task HasPermissionAsync_WhenTheOperatorHasNoRoleForThatSite_ReturnsFalse()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());
        await SeedSiteAndOperator(siteId, operatorId);

        await using var db = fixture.CreateDbContext();
        var checker = new PermissionChecker(db);

        var result = await checker.HasPermissionAsync(operatorId, siteId, Permission.ConversationSend, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task HasPermissionAsync_WhenTheRoleIsForADifferentSite_ReturnsFalse()
    {
        var operatorId = new OperatorId(Guid.NewGuid());
        var roleId = Guid.NewGuid();
        var roleSiteId = new SiteId(Guid.NewGuid());
        var queriedSiteId = new SiteId(Guid.NewGuid());
        await SeedSiteAndOperator(roleSiteId, operatorId);
        await using (var siteDb = fixture.CreateDbContext())
        {
            siteDb.Sites.Add(new Site(queriedSiteId, $"site_{queriedSiteId.Value:N}", []));
            await siteDb.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            db.Roles.Add(new RoleRecord
            {
                Id = roleId,
                SiteId = roleSiteId,
                Name = "Operator",
                Permissions = [Permission.ConversationSend.Value],
            });
            db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId });
            await db.SaveChangesAsync();
        }

        await using var readDb = fixture.CreateDbContext();
        var checker = new PermissionChecker(readDb);

        var result = await checker.HasPermissionAsync(operatorId, queriedSiteId, Permission.ConversationSend, CancellationToken.None);

        Assert.False(result);
    }
}
