using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `22-07`/`adr/0093`: rule 4 - "a state change and its integration event are committed in one
/// transaction" - proved against a real Postgres rather than asserted from the design.
/// <see cref="ModuleQuantityGrantStore"/> is `ago-calendar`'s own half's counterpart:
/// <c>ModuleQuantityGrantedConsumer</c>/<c>IWorkerQuotaGrantStore</c> in that repository. This file
/// cannot reach either and does not try to - see <c>RoleAssignmentsChangedOutboxTests</c> for the
/// identical split this codebase already made once.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ModuleQuantityGrantedOutboxTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    [Fact]
    public async Task GrantingAQuantity_StagesOneModuleQuantityGrantedRow_InTheSameTransactionAsTheGrant()
    {
        var siteId = new SiteId(Guid.NewGuid());

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var store = new ModuleQuantityGrantStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            await store.GrantAsync(siteId, new ModuleKey("calendar"), 5, Now, CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var grant = await verify.ModuleQuantityGrants.SingleAsync(
            g => g.SiteId == siteId && g.ModuleKey == new ModuleKey("calendar"));
        Assert.Equal(5, grant.Quantity);

        var outboxRow = await verify.Set<OutboxMessage>().SingleAsync(
            o => o.Type == nameof(ModuleQuantityGranted) && o.PartitionKey == siteId.Value.ToString());

        var contract = System.Text.Json.JsonSerializer.Deserialize<ModuleQuantityGranted>(outboxRow.Payload)!;
        Assert.Equal(siteId.Value, contract.SiteId);
        Assert.Equal("calendar", contract.ModuleKey);
        Assert.Equal(5, contract.Quantity);
        Assert.Null(outboxRow.PublishedAt);
    }

    [Fact]
    public async Task LoweringTheQuantity_RestagesTheSameFactAsANewSnapshot()
    {
        var siteId = new SiteId(Guid.NewGuid());

        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var store = new ModuleQuantityGrantStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            await store.GrantAsync(siteId, new ModuleKey("calendar"), 5, Now, CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var store = new ModuleQuantityGrantStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            await store.GrantAsync(siteId, new ModuleKey("calendar"), 2, Now.AddSeconds(1), CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        // One row - a snapshot, never a history (ModuleQuantityGrant's own remarks).
        var grant = await verify.ModuleQuantityGrants.SingleAsync(
            g => g.SiteId == siteId && g.ModuleKey == new ModuleKey("calendar"));
        Assert.Equal(2, grant.Quantity);

        var outboxRows = await verify.Set<OutboxMessage>()
            .Where(o => o.Type == nameof(ModuleQuantityGranted) && o.PartitionKey == siteId.Value.ToString())
            .ToListAsync();
        Assert.Equal(2, outboxRows.Count);
    }
}
