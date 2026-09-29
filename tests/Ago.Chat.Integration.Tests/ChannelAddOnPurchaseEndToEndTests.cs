using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases;
using Ago.Chat.Application.UseCases.OperatorRoleSeats;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Modules;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-278`'s own end-to-end Done-when: "a paid channel connects (register succeeds); when the option
/// lapses, the watchdog pauses the credential within a tick." Composes three already-proven pieces -
/// <see cref="ChannelAddOnPurchaseApplier"/> (this item), the real <c>SubscriptionRenewalApplier</c>'s
/// own option-lapse revoke (`23-86`, proven in <c>SubscriptionRenewalJobTests</c>), and
/// <see cref="EntitlementWatchdogJob"/>'s own pause reconciliation (`25-170`, proven in
/// <see cref="EntitlementWatchdogJobTests"/>) - proving here only that the three actually compose end to
/// end for a channel this item's own purchase path minted, not re-proving any one of them in isolation.
///
/// <para><b>"Register succeeds" is proven through <see cref="ChannelEntitlement.IsEntitledAsync"/>, the
/// exact two-step gate <c>RegisterChannelCredentialHandler</c> itself calls</b> - composing the same
/// real <see cref="ConfiguredBillingOptionEntitlementProvider"/> and <see cref="ModuleQuantityGrantStore"/>
/// that handler receives by constructor injection, rather than standing up that handler's own full
/// dependency graph (permission checker, channel credential repository, token cipher) for a fact this
/// gate alone already determines.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChannelAddOnPurchaseEndToEndTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private const string TelegramOptionKey = "channel-telegram";
    private const string TelegramModuleKey = "channel";

    [Fact]
    public async Task APaidChannel_ConnectsImmediately_AndIsPausedByTheWatchdogOnceItsOptionLapses()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var baseId = new BillingSubscriptionId(Guid.NewGuid());
        var optionId = new BillingSubscriptionId(Guid.NewGuid());
        var credentialId = new ChannelCredentialId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], tier: SubscriptionTierBands.Starter, seatLimit: 5));
            var baseSubscription = BillingSubscription.Create(
                baseId, siteId, $"pmt_{baseId.Value:N}", 5, SubscriptionTierBands.Starter, 1, 1, Now - BillingSubscription.PeriodLength);
            baseSubscription.MarkSucceeded("card_on_file", Now - BillingSubscription.PeriodLength);
            db.BillingSubscriptions.Add(baseSubscription);
            db.ChannelCredentials.Add(
                ChannelCredential.Register(credentialId, siteId, ChannelKind.Telegram, tokenCiphertext: [1], webhookSecretHash: [2], now: Now));
            await db.SaveChangesAsync();
        }

        var entitlementMappings = new Dictionary<string, string?> { [TelegramOptionKey] = TelegramModuleKey };

        // 1) Purchase - the exact write PurchaseChannelAddOnHandler triggers on a verified charge.
        await using (var db = fixture.CreateDbContext())
        {
            var purchaseApplier = BuildPurchaseApplier(db, entitlementMappings);
            await purchaseApplier.ApplyPurchaseAsync(
                new ChannelAddOnPurchaseApplyRequest(
                    optionId, siteId, ChannelKind.Telegram, new BillingOptionKey(TelegramOptionKey),
                    $"pmt_channel_{optionId.Value:N}", "card_on_file", Now, Now),
                CancellationToken.None);
        }

        // 2) "Register succeeds" - the exact gate RegisterChannelCredentialHandler calls.
        await using (var db = fixture.CreateDbContext())
        {
            var entitlements = BuildEntitlementProvider(entitlementMappings);
            var grants = new ModuleQuantityGrantStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(), new FixedClock(Now));
            var isEntitled = await ChannelEntitlement.IsEntitledAsync(entitlements, grants, siteId, ChannelKind.Telegram, CancellationToken.None);
            Assert.True(isEntitled);
        }

        // 3) The option lapses - the identical path a retry-window-exhausted renewal takes
        // (ProcessSubscriptionRenewalHandler calls the same ApplyLapseAsync regardless of IsOption).
        await using (var db = fixture.CreateDbContext())
        {
            var renewalApplier = BuildRenewalApplier(db, entitlementMappings);
            await renewalApplier.ApplyLapseAsync(optionId, Now.AddDays(1), CancellationToken.None);
        }

        await using (var db = fixture.CreateDbContext())
        {
            var entitlements = BuildEntitlementProvider(entitlementMappings);
            var grants = new ModuleQuantityGrantStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(), new FixedClock(Now));
            var isEntitled = await ChannelEntitlement.IsEntitledAsync(entitlements, grants, siteId, ChannelKind.Telegram, CancellationToken.None);
            Assert.False(isEntitled);
        }

        // 4) The watchdog pauses the now-unentitled credential within one tick.
        await CreateWatchdogJob(entitlementMappings).RunOnceAsync(CancellationToken.None);

        await using var verify = fixture.CreateDbContext();
        var pausedAt = await verify.ChannelCredentials
            .Where(c => c.Id == credentialId)
            .Select(c => c.EntitlementPausedAt)
            .SingleAsync(CancellationToken.None);
        Assert.NotNull(pausedAt);
    }

    private static ChannelAddOnPurchaseApplier BuildPurchaseApplier(AgoChatDbContext db, IReadOnlyDictionary<string, string?> mappings)
    {
        var entitlements = BuildEntitlementProvider(mappings);
        var grants = new ModuleQuantityGrantStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(), new FixedClock(Now));
        return new ChannelAddOnPurchaseApplier(db, grants, entitlements);
    }

    private static SubscriptionRenewalApplier BuildRenewalApplier(AgoChatDbContext db, IReadOnlyDictionary<string, string?> mappings)
    {
        var entitlements = BuildEntitlementProvider(mappings);
        var grants = new ModuleQuantityGrantStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(), new FixedClock(Now));
        var roleSeatReconciler = new OperatorRoleSeatReconciler(new OperatorRoleRepository(db));
        return new SubscriptionRenewalApplier(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(), grants, entitlements, roleSeatReconciler);
    }

    private static IBillingOptionEntitlementProvider BuildEntitlementProvider(IReadOnlyDictionary<string, string?> mappings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(mappings.ToDictionary(kv => $"{ConfiguredBillingOptionEntitlementProvider.SectionName}:{kv.Key}", kv => kv.Value))
            .Build();
        return new ConfiguredBillingOptionEntitlementProvider(config);
    }

    private EntitlementWatchdogJob CreateWatchdogJob(IReadOnlyDictionary<string, string?> mappings)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AgoChatDbContext>(options => options.UseNpgsql(fixture.DataSource));
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<ISiteRepository, SiteRepository>();
        services.AddScoped<OperatorRoleSeatReconciler>();
        services.AddScoped<IOperatorRoleRepository, OperatorRoleRepository>();
        services.AddScoped<IChannelCredentialRepository, ChannelCredentialRepository>();
        services.AddScoped<IOutboxWriter, EfOutboxWriter<AgoChatDbContext>>();
        services.AddScoped<IIdGenerator, UuidV7Generator>();
        services.AddScoped<IModuleQuantityGrantStore, ModuleQuantityGrantStore>();
        services.AddScoped<IOwnerSeatGrantStore, OwnerSeatGrantStore>();
        services.AddSingleton<IClock>(new FixedClock(Now));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(mappings.ToDictionary(kv => $"{ConfiguredBillingOptionEntitlementProvider.SectionName}:{kv.Key}", kv => kv.Value))
            .Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddScoped<IBillingOptionEntitlementProvider, ConfiguredBillingOptionEntitlementProvider>();

        var provider = services.BuildServiceProvider();
        return new EntitlementWatchdogJob(
            provider.GetRequiredService<IServiceScopeFactory>(),
            fixture.DataSource,
            new FixedClock(Now),
            Options.Create(new EntitlementWatchdogJobOptions()),
            NullLogger<EntitlementWatchdogJob>.Instance);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
