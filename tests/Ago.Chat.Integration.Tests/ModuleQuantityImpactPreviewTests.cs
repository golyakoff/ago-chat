using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-88`/`adr/0093`: rule 4 for <see cref="ModuleQuantityImpactPreviewStore"/> - a request stages
/// its own row and its own outbox row in one transaction, proved against a real Postgres rather than
/// asserted from the design. The identical split <see cref="ModuleQuantityGrantedOutboxTests"/>
/// already establishes for its sibling: this file cannot reach whatever module product eventually
/// answers, and does not try to.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ModuleQuantityImpactPreviewTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

    private async Task<SiteId> SeedSiteAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var seed = fixture.CreateDbContext();
        seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        await seed.SaveChangesAsync();
        return siteId;
    }

    [Fact]
    public async Task RequestAsync_StagesTheRowAndOneModuleQuantityImpactRequestedRow_InTheSameTransaction()
    {
        var siteId = await SeedSiteAsync();

        await using (var db = fixture.CreateDbContext())
        {
            var store = new ModuleQuantityImpactPreviewStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            await store.RequestAsync(siteId, new ModuleKey("calendar"), 2, Now, CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var preview = await verify.ModuleQuantityImpactPreviews.SingleAsync(
            p => p.SiteId == siteId && p.ModuleKey == new ModuleKey("calendar"));
        Assert.Equal(2, preview.RequestedQuantity);
        Assert.Null(preview.AffectedCount);
        Assert.Null(preview.AnsweredAt);

        var outboxRow = await verify.Set<OutboxMessage>().SingleAsync(
            o => o.Type == nameof(ModuleQuantityImpactRequested) && o.PartitionKey == siteId.Value.ToString());

        var contract = System.Text.Json.JsonSerializer.Deserialize<ModuleQuantityImpactRequested>(outboxRow.Payload)!;
        Assert.Equal(siteId.Value, contract.SiteId);
        Assert.Equal("calendar", contract.ModuleKey);
        Assert.Equal(2, contract.RequestedQuantity);
        Assert.Null(outboxRow.PublishedAt);
    }

    [Fact]
    public async Task RequestAsync_ASecondTimeWithADifferentCandidate_OverwritesTheRow_AndStagesASecondRequest()
    {
        var siteId = await SeedSiteAsync();

        await using (var db = fixture.CreateDbContext())
        {
            var store = new ModuleQuantityImpactPreviewStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            await store.RequestAsync(siteId, new ModuleKey("calendar"), 2, Now, CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var store = new ModuleQuantityImpactPreviewStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            await store.RequestAsync(siteId, new ModuleKey("calendar"), 3, Now.AddSeconds(1), CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        // One row - a snapshot per (site, module), the identical shape ModuleQuantityGrant's own
        // remarks give for the sibling table.
        var preview = await verify.ModuleQuantityImpactPreviews.SingleAsync(
            p => p.SiteId == siteId && p.ModuleKey == new ModuleKey("calendar"));
        Assert.Equal(3, preview.RequestedQuantity);

        var outboxRows = await verify.Set<OutboxMessage>()
            .Where(o => o.Type == nameof(ModuleQuantityImpactRequested) && o.PartitionKey == siteId.Value.ToString())
            .ToListAsync();
        Assert.Equal(2, outboxRows.Count);
    }

    [Fact]
    public async Task AnswerAsync_WhenTheAnsweredQuantityMatchesThePendingRow_RecordsTheAnswer()
    {
        var siteId = await SeedSiteAsync();

        await using (var db = fixture.CreateDbContext())
        {
            var store = new ModuleQuantityImpactPreviewStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            await store.RequestAsync(siteId, new ModuleKey("calendar"), 2, Now, CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var store = new ModuleQuantityImpactPreviewStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            await store.AnswerAsync(
                siteId, new ModuleKey("calendar"), 2, 3, ["Anna", "Boris", "Vera"], Now.AddSeconds(2), CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var preview = await verify.ModuleQuantityImpactPreviews.SingleAsync(
            p => p.SiteId == siteId && p.ModuleKey == new ModuleKey("calendar"));
        Assert.Equal(3, preview.AffectedCount);
        Assert.Equal(["Anna", "Boris", "Vera"], preview.AffectedItemDisplayNames);
        Assert.NotNull(preview.AnsweredAt);
    }

    /// <summary>`23-88`'s own "a late or stray answer is a silent no-op" rule
    /// (<c>IModuleQuantityImpactPreviewStore.AnswerAsync</c>'s own remarks), proved against a real
    /// row: an answer arriving for a candidate the owner has since moved past (a fresh
    /// <c>RequestAsync</c> already overwrote <c>RequestedQuantity</c>) must not overwrite the newer
    /// question's own still-pending state with an answer to a question nobody is asking any more.
    /// </summary>
    [Fact]
    public async Task AnswerAsync_WhenTheAnsweredQuantityNoLongerMatchesTheRow_IsDiscarded()
    {
        var siteId = await SeedSiteAsync();

        await using (var db = fixture.CreateDbContext())
        {
            var store = new ModuleQuantityImpactPreviewStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            await store.RequestAsync(siteId, new ModuleKey("calendar"), 2, Now, CancellationToken.None);
            // The owner changed their mind before the module ever answered candidate 2.
            await store.RequestAsync(siteId, new ModuleKey("calendar"), 4, Now.AddSeconds(1), CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var store = new ModuleQuantityImpactPreviewStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            // A late answer to the superseded question, candidate 2.
            await store.AnswerAsync(siteId, new ModuleKey("calendar"), 2, 3, ["Anna"], Now.AddSeconds(2), CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var preview = await verify.ModuleQuantityImpactPreviews.SingleAsync(
            p => p.SiteId == siteId && p.ModuleKey == new ModuleKey("calendar"));
        // Still the newer question, still unanswered - the stray reply for candidate 2 changed nothing.
        Assert.Equal(4, preview.RequestedQuantity);
        Assert.Null(preview.AffectedCount);
        Assert.Null(preview.AnsweredAt);
    }

    [Fact]
    public async Task AnswerAsync_WhenNoPreviewRowExistsAtAll_IsDiscarded_AndCreatesNoRow()
    {
        var siteId = await SeedSiteAsync();

        await using (var db = fixture.CreateDbContext())
        {
            var store = new ModuleQuantityImpactPreviewStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator());
            await store.AnswerAsync(siteId, new ModuleKey("calendar"), 2, 3, ["Anna"], Now, CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.ModuleQuantityImpactPreviews.AnyAsync(
            p => p.SiteId == siteId && p.ModuleKey == new ModuleKey("calendar")));
    }
}
