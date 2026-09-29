using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Modules;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-278`: the identical bar <see cref="AdministratorSlotChangeApplierTests"/> already sets for the
/// analogous Administrator-slot purchase - real Postgres, real one-transaction commit, real grant.
/// Proves what <see cref="Application.Tests.UseCases.PurchaseChannelAddOn.PurchaseChannelAddOnHandlerTests"/>
/// (fakes) cannot: that a channel add-on purchase actually inserts a real, `Succeeded`
/// <see cref="BillingSubscription"/> option row and a real <c>module_quantity_grants</c> row, in one
/// transaction, through the real <see cref="ConfiguredBillingOptionEntitlementProvider"/> mapping a
/// deployment would actually declare.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChannelAddOnPurchaseApplierTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private const string TelegramOptionKey = "channel-telegram";
    private const string TelegramModuleKey = "channel";

    [Fact]
    public async Task ApplyPurchaseAsync_CreatesASucceededOptionSubscription_AlignedToTheBase_AndGrantsTheEntitlement()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var baseId = new BillingSubscriptionId(Guid.NewGuid());
        // MarkSucceeded sets CurrentPeriodEnd = succeededAt + PeriodLength - succeeding a full period
        // ago lands it exactly at Now, a known value this test can assert the option copied without
        // reaching past the aggregate's own public surface to force one.
        var basePeriodEnd = Now;

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], tier: SubscriptionTierBands.Starter, seatLimit: 5));
            var baseSubscription = BillingSubscription.Create(
                baseId, siteId, $"pmt_{baseId.Value:N}", 5, SubscriptionTierBands.Starter, 1, 1, Now - BillingSubscription.PeriodLength);
            baseSubscription.MarkSucceeded("card_on_file", Now - BillingSubscription.PeriodLength);
            db.BillingSubscriptions.Add(baseSubscription);
            await db.SaveChangesAsync();
        }

        var optionId = new BillingSubscriptionId(Guid.NewGuid());
        await using (var db = fixture.CreateDbContext())
        {
            var applier = BuildApplier(db, new Dictionary<string, string?> { [TelegramOptionKey] = TelegramModuleKey });
            await applier.ApplyPurchaseAsync(
                new ChannelAddOnPurchaseApplyRequest(
                    optionId, siteId, ChannelKind.Telegram, new BillingOptionKey(TelegramOptionKey),
                    $"pmt_channel_{optionId.Value:N}", "card_on_file", basePeriodEnd, Now),
                CancellationToken.None);
        }

        await using var verify = fixture.CreateDbContext();
        var option = await verify.BillingSubscriptions.SingleAsync(s => s.Id == optionId);
        Assert.Equal(BillingSubscriptionStatus.Succeeded, option.Status);
        Assert.Equal(new BillingOptionKey(TelegramOptionKey), option.OptionKey);
        Assert.Equal(basePeriodEnd, option.CurrentPeriodEnd);
        Assert.Equal("card_on_file", option.PaymentMethodId);
        Assert.Equal($"pmt_channel_{optionId.Value:N}", option.YooKassaPaymentId);
        // Meaningless for an option row - fixed at the CreateOption convention.
        Assert.Equal(0, option.RequestedSeats);

        var grants = new ModuleQuantityGrantStore(verify, new EfOutboxWriter<AgoChatDbContext>(verify), new UuidV7Generator(), new FixedClock(Now));
        var quantity = await grants.GetQuantityAsync(siteId, new ModuleKey(TelegramModuleKey), CancellationToken.None);
        Assert.Equal(1, quantity);
    }

    [Fact]
    public async Task ApplyPurchaseAsync_WhenNoEntitlementMappingIsConfigured_ThrowsRatherThanGrantingSilently()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var optionId = new BillingSubscriptionId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], tier: SubscriptionTierBands.Starter, seatLimit: 5));
        await db.SaveChangesAsync();

        // Deliberately empty - this deployment has not declared BillingOptionEntitlements:channel-telegram.
        var applier = BuildApplier(db, new Dictionary<string, string?>());

        await Assert.ThrowsAsync<InvalidOperationException>(() => applier.ApplyPurchaseAsync(
            new ChannelAddOnPurchaseApplyRequest(
                optionId, siteId, ChannelKind.Telegram, new BillingOptionKey(TelegramOptionKey),
                $"pmt_channel_{optionId.Value:N}", "card_on_file", Now.AddDays(10), Now),
            CancellationToken.None));
    }

    private static ChannelAddOnPurchaseApplier BuildApplier(AgoChatDbContext db, IReadOnlyDictionary<string, string?> entitlementMappings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                entitlementMappings.ToDictionary(kv => $"{ConfiguredBillingOptionEntitlementProvider.SectionName}:{kv.Key}", kv => kv.Value))
            .Build();
        var optionEntitlements = new ConfiguredBillingOptionEntitlementProvider(config);
        var entitlementGrants = new ModuleQuantityGrantStore(db, new EfOutboxWriter<AgoChatDbContext>(db), new UuidV7Generator(), new FixedClock(Now));
        return new ChannelAddOnPurchaseApplier(db, entitlementGrants, optionEntitlements);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
