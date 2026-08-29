using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Microsoft.Extensions.DependencyInjection;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// Found live, 2026-08-29, while landing `16-04`: `13-02`/`13-03`/`13-04` all registered the handlers
/// that depend on <see cref="IBillingSubscriptionRepository"/> and <see cref="IBillingWebhookApplier"/>
/// (<c>GetBillingStatusHandler</c>, <c>ProcessYooKassaWebhookHandler</c>) in `ChatModule`, but neither
/// port itself was ever registered by <see cref="ServiceCollectionExtensions.AddPostgresPersistence"/>
/// - the exact same shape of gap
/// <see cref="ChannelCredentialCipherDependencyInjectionTests"/> already found and closed for a
/// different type on 2026-08-28, and for the identical reason: every existing test for these two
/// handlers resolves a fake or a hand-constructed concrete instance directly
/// (`GetBillingStatusHandlerTests`, `BillingWebhookApplierTests`), never through the real DI
/// registration path this file exercises instead. The container itself built without error - nothing
/// about a missing registration is checked until something actually asks for it - so the first real
/// `GET /billing/status` or ЮKassa webhook call against a running <c>Ago.Chat.Api</c> would have thrown
/// `InvalidOperationException: Unable to resolve service for type
/// 'Ago.Chat.Application.Abstractions.IBillingSubscriptionRepository'` (or the webhook applier's).
///
/// Unlike <see cref="ChannelCredentialCipherDependencyInjectionTests"/>, which reproduces its two
/// registration lines in an isolated <c>ServiceCollection</c>, this test calls the real
/// <see cref="ServiceCollectionExtensions.AddPostgresPersistence"/> entry point directly (the same
/// technique <c>TelemetryLeakGuardTests</c> uses) - so a future regression that removes either
/// registration from that method fails this test for real, not only a hand-reproduced copy of it.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BillingRepositoryDependencyInjectionTests(PostgresFixture fixture)
{
    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        // The production call, not a hand-built registration - the whole point is proving the wiring
        // a host actually uses resolves these two ports, per this file's own doc comment.
        services.AddPostgresPersistence(fixture.ConnectionString);
        // IIdGenerator is the platform kernel's own registration (AddPlatformKernel, every real host's
        // own Program.cs), not AddPostgresPersistence's - added here directly, the same minimal-glue
        // shape ActiveSiteResolutionTests already uses for the identical reason.
        services.AddSingleton<IIdGenerator, UuidV7Generator>();
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task IBillingSubscriptionRepository_ResolvesThroughTheRealDIRegistration_NotJustAFake()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        var repository = scope.ServiceProvider.GetRequiredService<IBillingSubscriptionRepository>();

        Assert.IsType<BillingSubscriptionRepository>(repository);
    }

    [Fact]
    public async Task IBillingWebhookApplier_ResolvesThroughTheRealDIRegistration_NotJustAFake()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        var applier = scope.ServiceProvider.GetRequiredService<IBillingWebhookApplier>();

        Assert.IsType<BillingWebhookApplier>(applier);
    }

    /// <summary>Proves the resolved repository is not just constructible but actually wired to a real
    /// database - the same "prove it actually works, not just that it exists" bar
    /// <see cref="ChannelCredentialCipherDependencyInjectionTests"/> holds itself to.</summary>
    [Fact]
    public async Task TheResolvedRepository_SavesAndReadsBackASubscription()
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using (var seed = fixture.CreateDbContext())
        {
            seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await seed.SaveChangesAsync(CancellationToken.None);
        }

        await using var provider = BuildProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IBillingSubscriptionRepository>();
            var subscription = BillingSubscription.Create(
                new BillingSubscriptionId(Guid.NewGuid()), siteId, "pmt_di_smoke", requestedSeats: 2,
                tier: SubscriptionTierBands.Starter, DateTimeOffset.UtcNow);
            await repository.SaveAsync(subscription, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<AgoChatDbContext>().SaveChangesAsync(CancellationToken.None);
        }

        await using var readDb = fixture.CreateDbContext();
        var readRepository = new BillingSubscriptionRepository(readDb);
        var result = await readRepository.GetLatestForSiteAsync(siteId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("pmt_di_smoke", result!.YooKassaPaymentId);
    }
}
