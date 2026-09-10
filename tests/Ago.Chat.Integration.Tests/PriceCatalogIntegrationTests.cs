using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.PublishPriceVersion;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-43`'s own fails-before demonstrations, against a real Postgres container rather than an
/// in-memory fake - <see cref="Domain.Tests.PricedResourceTests"/> already proves the same invariants
/// at the aggregate level with nothing to fake; this class proves they survive an actual round trip
/// through <see cref="PriceCatalogRepository"/> and the real schema
/// <c>Stage25AddPricedResourceCatalog</c> creates. Deliberately mirrors
/// <see cref="PublishedDocumentIntegrationTests"/> - the identical grandfathering shape, proven the
/// identical way.
///
/// <para><b>Why most of this file talks to <see cref="PriceCatalogRepository"/> directly rather than
/// through <see cref="PublishPriceVersionHandler"/>.</b> The handler refuses any key
/// <see cref="PricedResourceKeys.IsKnown"/> does not recognise - the two real keys are exactly
/// <see cref="SubscriptionTierBands.BaseSeatPriceKey"/>/<see cref="SubscriptionTierBands.ExtraSeatPriceKey"/>,
/// and this file's own tests need a fresh, disposable key per test so they never collide with the
/// migration's own seed data or with other files sharing this collection's Postgres container. There
/// is no production seam to register a throwaway key through (`25-43`'s own first decision: "code
/// registers the key"), so the tests that need the concurrency/grandfathering mechanism itself - not
/// the registry gate - exercise <see cref="PriceCatalogRepository"/> and <see cref="PricedResource"/>
/// directly, the same layer split <see cref="PricedResourceTests"/> and this file's own registry test
/// draw between them. <see cref="PublishPriceVersion_ForAnUnregisteredKey_RefusesCleanly_NeverInsertingARow"/>
/// is the one test that does go through the handler, precisely because the registry gate is what it
/// proves.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PriceCatalogIntegrationTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    // `25-43`'s own Done-when, proven end to end: "a completed charge can be read back showing the
    // exact price version it was charged under" - a later publish must never be observable as having
    // changed what an earlier version reports, not even after a real round trip through Postgres.
    [Fact]
    public async Task PublishingASecondVersion_LeavesTheFirstReadableAtItsOwnUnchangedAmount()
    {
        var key = new PriceKey($"test-key-{Guid.NewGuid():N}");

        await using (var db = fixture.CreateDbContext())
        {
            var repository = new PriceCatalogRepository(db);
            var resource = PricedResource.Create(new PricedResourceId(Guid.NewGuid()), key);
            resource.Publish(new PublishedPriceVersionId(Guid.NewGuid()), 490m, Now);
            await repository.SaveAsync(resource, CancellationToken.None);

            var reloaded = await repository.GetByKeyAsync(key, CancellationToken.None);
            reloaded!.Publish(new PublishedPriceVersionId(Guid.NewGuid()), 550m, Now.AddMonths(1));
            await repository.SaveAsync(reloaded, CancellationToken.None);
        }

        // A fresh DbContext, a fresh repository - proving this is not the same tracked instance still
        // sitting in a change tracker's identity map, but real rows Postgres itself is holding.
        await using var verify = fixture.CreateDbContext();
        var readRepository = new PriceCatalogRepository(verify);

        var readBackV1 = await readRepository.FindVersionAsync(key, 1, CancellationToken.None);
        var readBackV2 = await readRepository.FindVersionAsync(key, 2, CancellationToken.None);
        var current = await readRepository.FindCurrentAsync(key, CancellationToken.None);

        Assert.NotNull(readBackV1);
        Assert.Equal(490m, readBackV1!.AmountRub);
        Assert.NotNull(readBackV2);
        Assert.Equal(550m, readBackV2!.AmountRub);
        // Publishing v2 answers "current" as v2, but never touches v1's own already-published amount -
        // both this method's contract and PricedResource.Publish's own append-only invariant, now
        // proven through real Postgres rows.
        Assert.Equal("v2", current!.Version);
        Assert.Equal(490m, readBackV1.AmountRub);
    }

    // `25-43`'s own Done-when: "a key with no published version refuses any charge attempt cleanly
    // and namedly ... never a crash, never a zero-amount charge" - proven here at the repository
    // level, the honest "built, not yet for sale" state a real charge site must treat as ordinary.
    [Fact]
    public async Task FindCurrentAsync_ForAKeyWithNoPublishedVersion_ReturnsNull_NeverAFabricatedZero()
    {
        var key = new PriceKey($"test-key-{Guid.NewGuid():N}");

        await using var db = fixture.CreateDbContext();
        var repository = new PriceCatalogRepository(db);

        var current = await repository.FindCurrentAsync(key, CancellationToken.None);

        Assert.Null(current);
    }

    /// <summary>The first decision this whole item turns on, proven at the one layer that actually
    /// enforces it: the owner may only ever move the Rouble figure for a key code has already
    /// registered - never invent one through this handler. <see cref="OwnerPricingEndpointTests"/>'s
    /// own equivalent test proves the same refusal reaches an HTTP caller as 400; this one proves the
    /// handler itself never inserts a row for the attempt.</summary>
    [Fact]
    public async Task PublishPriceVersion_ForAnUnregisteredKey_RefusesCleanly_NeverInsertingARow()
    {
        var key = $"not-a-real-key-{Guid.NewGuid():N}";

        await using var db = fixture.CreateDbContext();
        var handler = new PublishPriceVersionHandler(new PriceCatalogRepository(db), new UuidV7Generator(), new SystemClock());

        var result = await handler.HandleAsync(new PublishPriceVersion(key, 100m), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Billing.PriceKeyUnknown", result.Error!.Value.Code);

        await using var verify = fixture.CreateDbContext();
        Assert.Null(await new PriceCatalogRepository(verify).GetByKeyAsync(new PriceKey(key), CancellationToken.None));
    }

    // The concurrency mechanism `PublishPriceVersionHandler`'s own retry loop leans on - proven here
    // directly against PriceCatalogRepository/PricedResource, the same layer PricedResourceTests and
    // this file's own registry test draw the line at (this file's class remarks explain why the
    // registry gate keeps this test off the handler itself).
    [Fact]
    public async Task SaveAsync_RacedByAConcurrentPublishForTheSameKey_ThrowsAConcurrencyConflict_AndARetryAgainstTheFreshRowSucceeds()
    {
        var key = new PriceKey($"test-key-{Guid.NewGuid():N}");

        // Seeded first, outside the race: the scenario this proves is two saves racing over an
        // *existing* PricedResource row's own xmin (an UPDATE conflict) - the identical shape
        // PublishedDocumentIntegrationTests' own equivalent test races an assign/close against an
        // already-seeded aggregate, never a brand-new key's own first insert (which would instead hit
        // ix_priced_resources_key's own uniqueness, a different, already-prevented failure mode).
        await using (var seed = fixture.CreateDbContext())
        {
            var seedRepository = new PriceCatalogRepository(seed);
            var seeded = PricedResource.Create(new PricedResourceId(Guid.NewGuid()), key);
            seeded.Publish(new PublishedPriceVersionId(Guid.NewGuid()), 490m, Now);
            await seedRepository.SaveAsync(seeded, CancellationToken.None);
        }

        await using var db = fixture.CreateDbContext();
        var repository = new PriceCatalogRepository(db);

        // Load the aggregate this attempt will try to save against, then let a second, independent
        // context publish and commit a version for the identical key before this one saves - the same
        // "load, then let someone else win the race, then try to save the stale copy" shape
        // RacingDocumentRepository injects mechanically; done inline here since only one call site
        // needs it.
        var stale = await repository.GetByKeyAsync(key, CancellationToken.None);
        var staleAttempt = stale!.Publish(new PublishedPriceVersionId(Guid.NewGuid()), 999m, Now);

        await using (var concurrent = fixture.CreateDbContext())
        {
            var concurrentRepository = new PriceCatalogRepository(concurrent);
            var winner = await concurrentRepository.GetByKeyAsync(key, CancellationToken.None);
            winner!.Publish(new PublishedPriceVersionId(Guid.NewGuid()), 500m, Now);
            await concurrentRepository.SaveAsync(winner, CancellationToken.None);
        }

        // The clean, technology-agnostic signal `25-43` asks for - never a raw
        // DbUpdateConcurrencyException reaching a caller, the identical translation
        // DocumentConcurrencyConflictException already proves for `24-02`.
        await Assert.ThrowsAsync<PriceCatalogConcurrencyConflictException>(
            () => repository.SaveAsync(stale, CancellationToken.None));

        // A retry against a freshly reloaded aggregate - exactly what PublishPriceVersionHandler's own
        // retry loop does - succeeds and mints the next sequence after the winner's, never colliding
        // with it and never silently overwriting it.
        var fresh = await repository.GetByKeyAsync(key, CancellationToken.None);
        var retried = fresh!.Publish(new PublishedPriceVersionId(Guid.NewGuid()), staleAttempt.AmountRub, Now);
        await repository.SaveAsync(fresh, CancellationToken.None);

        Assert.Equal(3, retried.Sequence);
        Assert.Equal("v3", retried.Version);

        await using var verifyDb = fixture.CreateDbContext();
        var final = await new PriceCatalogRepository(verifyDb).GetByKeyAsync(key, CancellationToken.None);
        Assert.Equal(3, final!.Versions.Count);
        Assert.Equal(490m, final.Versions.Single(v => v.Sequence == 1).AmountRub);
        Assert.Equal(500m, final.Versions.Single(v => v.Sequence == 2).AmountRub);
        Assert.Equal(999m, final.Versions.Single(v => v.Sequence == 3).AmountRub);
    }
}
