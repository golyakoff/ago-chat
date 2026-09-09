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

    /// <summary>
    /// `23-89`: the support-call failure this item exists for - an owner grants, sees no immediate
    /// change (the module applies off this very outbox, asynchronously), and grants the identical
    /// quantity again. Against a real Postgres, not merely the fake store's dictionary: the natural
    /// key (<c>site_id</c>, <c>module_key</c>) means a second identical call can never produce a
    /// second row - <see cref="ModuleQuantityGrant"/>'s own remarks on why that key <em>is</em> the
    /// identity. Two outbox rows still stage (the identical fact, restated), which is exactly what
    /// makes this safe rather than merely harmless: `ago-calendar`'s own
    /// <c>ModuleQuantityGrantedConsumer</c> treats a redelivered or repeated
    /// <c>ModuleQuantityGranted</c> as a no-op by the identical snapshot reasoning
    /// (<c>WorkerQuotaTests.ReapplyingTheIdenticalGrant_IsANoOp_ProvingTheSnapshotIsIdempotent</c> in
    /// that repository proves the receiving half; this proves the sending half never even needed
    /// deduplicating in the first place).
    /// </summary>
    [Fact]
    public async Task GrantingTheIdenticalQuantityTwice_LeavesOneGrantRow_AndStagesTheIdenticalFactTwice()
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

        // The identical quantity, a second time - not a race, the same sequential repeat a
        // support-call retry produces. No exception is itself part of what "safe" means here.
        await using (var db = fixture.CreateDbContext())
        {
            var store = new ModuleQuantityGrantStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            await store.GrantAsync(siteId, new ModuleKey("calendar"), 5, Now.AddSeconds(1), CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var grant = await verify.ModuleQuantityGrants.SingleAsync(
            g => g.SiteId == siteId && g.ModuleKey == new ModuleKey("calendar"));
        Assert.Equal(5, grant.Quantity);

        var outboxRows = await verify.Set<OutboxMessage>()
            .Where(o => o.Type == nameof(ModuleQuantityGranted) && o.PartitionKey == siteId.Value.ToString())
            .ToListAsync();
        Assert.Equal(2, outboxRows.Count);
    }
}
