using Ago.Chat.Application.Abstractions;
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
using Npgsql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-170`: <see cref="EntitlementWatchdogJob"/> end to end against a real Postgres - the item's own
/// Done-when demands proof at the level of the job actually shipped, not only at the level of the two
/// Application-layer pieces it composes (<see cref="OperatorRoleSeatReconciler"/>'s own
/// <c>OperatorRoleSeatReconcilerTests</c> already proves the role-capacity math in isolation; this file
/// proves the watchdog's own channel-entitlement half, which nothing else in this codebase exercises at
/// all, and proves the role-capacity half runs correctly through the job's own per-site scope/transaction
/// wiring rather than only through a direct call to the reconciler).
///
/// <para><b>Why a real <see cref="IServiceScopeFactory"/>, not <see cref="SubscriptionRenewalJobTests"/>'s
/// own <c>DirectScopeFactory</c> double.</b> <see cref="EntitlementWatchdogJob.RunOnceAsync"/> opens a
/// fresh scope per site *and* a fresh scope per channel credential in the same run - a hand-rolled
/// dictionary-backed <see cref="IServiceScope"/> double (that file's own shape) would need to mint a
/// fresh <see cref="AgoChatDbContext"/> per <see cref="IServiceScopeFactory.CreateScope"/> call to prove
/// anything about per-scope isolation, which is exactly what a real <see cref="ServiceCollection"/>
/// already does for free.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EntitlementWatchdogJobTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    private const string TelegramOptionKey = "channel-telegram";
    private const string TelegramModuleKey = "channel-telegram";

    // -----------------------------------------------------------------------------------------
    // Channel-entitlement reconciliation - entirely unexercised anywhere else in this codebase.
    // -----------------------------------------------------------------------------------------

    /// <summary>This item's own Done-when: "for a long-polling channel, [confirm] that the poll loop
    /// itself pauses within one minute." <see cref="TelegramLongPollingService"/>'s own
    /// `RefreshPollersAsync` reads exactly this column (`EntitlementPausedAt is null`) to decide whether
    /// to keep polling a credential - proving this job sets it on a real row, against a real Postgres,
    /// is what actually proves the poll loop pauses, without needing a second, heavier test that spins
    /// up a real long-polling loop against a fake Telegram server to observe the same fact indirectly.</summary>
    [Fact]
    public async Task RunOnceAsync_AChannelWhoseEntitlementHasLapsedToZero_PausesTheCredential()
    {
        var siteId = await SeedSiteAsync();
        var credentialId = await SeedActiveTelegramCredentialAsync(siteId);
        await SeedGrantAsync(siteId, quantity: 0);

        await CreateJob().RunOnceAsync(CancellationToken.None);

        var paused = await ReadEntitlementPausedAtAsync(credentialId);
        Assert.NotNull(paused);
    }

    /// <summary>The reversible half of the same fact - a tenant renewing (or an owner regranting) after
    /// a lapse is un-paused on the very next tick, the identical "read-time, reversible" posture
    /// `Site.SuspendedUntil` already takes, never a one-way disconnect.</summary>
    [Fact]
    public async Task RunOnceAsync_APausedCredentialsEntitlementHasBeenRestored_ResumesIt()
    {
        var siteId = await SeedSiteAsync();
        var credentialId = await SeedActiveTelegramCredentialAsync(siteId);
        await SeedGrantAsync(siteId, quantity: 0);
        var job = CreateJob();
        await job.RunOnceAsync(CancellationToken.None);
        Assert.NotNull(await ReadEntitlementPausedAtAsync(credentialId));

        await SeedGrantAsync(siteId, quantity: 1);
        await job.RunOnceAsync(CancellationToken.None);

        Assert.Null(await ReadEntitlementPausedAtAsync(credentialId));
    }

    /// <summary>The negative control every assertion above relies on to mean what it says: a channel
    /// still within its granted quantity is never touched.</summary>
    [Fact]
    public async Task RunOnceAsync_AnEntitledChannel_IsNeverPaused()
    {
        var siteId = await SeedSiteAsync();
        var credentialId = await SeedActiveTelegramCredentialAsync(siteId);
        await SeedGrantAsync(siteId, quantity: 1);

        await CreateJob().RunOnceAsync(CancellationToken.None);

        Assert.Null(await ReadEntitlementPausedAtAsync(credentialId));
    }

    // -----------------------------------------------------------------------------------------
    // Role-capacity reconciliation, run through the job's own per-site scope/transaction wiring -
    // OperatorRoleSeatReconcilerTests already proves the tie-break math directly; this proves the
    // job assembles it correctly (fresh scope, real transaction, every site on the deployment).
    // -----------------------------------------------------------------------------------------

    /// <summary>This item's own Done-when: "a subscription downgrade that drops either limit below
    /// current headcount results in the excess being disabled... within one minute, most-recently-
    /// granted-first" - proven here through the watchdog job itself, not only through
    /// <see cref="OperatorRoleSeatReconciler"/> called directly.</summary>
    [Fact]
    public async Task RunOnceAsync_ASiteOverItsAdminLimit_DisablesTheMoreRecentlyGrantedAdminSeat()
    {
        var siteId = await SeedSiteAsync(seatLimit: 5, adminLimit: 1);
        var adminRoleId = await SeedRoleAsync(siteId, "Admin");
        var earlierGranted = await SeedRoleHolderAsync(siteId, adminRoleId, Now - TimeSpan.FromDays(10));
        var laterGranted = await SeedRoleHolderAsync(siteId, adminRoleId, Now - TimeSpan.FromDays(1));

        await CreateJob().RunOnceAsync(CancellationToken.None);

        await AssertHoldsSeatAsync(laterGranted, adminRoleId, expected: false);
        await AssertHoldsSeatAsync(earlierGranted, adminRoleId, expected: true);
    }

    /// <summary>The identical procedure, applied to the Operator role - the job's own `RoleNames`
    /// sweeps both seeded roles for every site, not the Admin role alone.</summary>
    [Fact]
    public async Task RunOnceAsync_ASiteOverItsSeatLimit_DisablesTheMoreRecentlyGrantedOperatorSeat()
    {
        var siteId = await SeedSiteAsync(seatLimit: 1, adminLimit: 5);
        var operatorRoleId = await SeedRoleAsync(siteId, "Operator");
        var earlierGranted = await SeedRoleHolderAsync(siteId, operatorRoleId, Now - TimeSpan.FromDays(10));
        var laterGranted = await SeedRoleHolderAsync(siteId, operatorRoleId, Now - TimeSpan.FromDays(1));

        await CreateJob().RunOnceAsync(CancellationToken.None);

        await AssertHoldsSeatAsync(laterGranted, operatorRoleId, expected: false);
        await AssertHoldsSeatAsync(earlierGranted, operatorRoleId, expected: true);
    }

    // -----------------------------------------------------------------------------------------
    // `25-181`: the platform owner's own hand-granted extra, fed into the identical reconciliation
    // procedure above rather than a second, bespoke consequence - see OwnerSeatGrantStore's own
    // remarks and EntitlementWatchdogJob.ReconcileRoleSeatsAsync for how the live extra reaches here.
    // -----------------------------------------------------------------------------------------

    /// <summary>The owner's own grant covers what would otherwise be an over-limit site - a live,
    /// unexpired extra keeps both Administrators seated even though `AdminLimit` alone is 1.</summary>
    [Fact]
    public async Task RunOnceAsync_ASiteOverItsAdminLimit_ButCoveredByALiveOwnerGrant_DisablesNobody()
    {
        var siteId = await SeedSiteAsync(seatLimit: 5, adminLimit: 1);
        var adminRoleId = await SeedRoleAsync(siteId, "Admin");
        var earlierGranted = await SeedRoleHolderAsync(siteId, adminRoleId, Now - TimeSpan.FromDays(10));
        var laterGranted = await SeedRoleHolderAsync(siteId, adminRoleId, Now - TimeSpan.FromDays(1));
        await SeedOwnerSeatGrantAsync(siteId, OwnerSeatGrantRole.Administrator, quantity: 1, expiresAt: Now.AddHours(1));

        await CreateJob(now: Now).RunOnceAsync(CancellationToken.None);

        await AssertHoldsSeatAsync(laterGranted, adminRoleId, expected: true);
        await AssertHoldsSeatAsync(earlierGranted, adminRoleId, expected: true);
    }

    /// <summary>The item's own Done-when, proven end to end through the real watchdog rather than only
    /// through <see cref="Application.UseCases.GetOwnerSeatSummary.GetOwnerSeatSummaryHandlerTests"/>'s
    /// own displayed-number proof: an owner grant that has since expired stops covering the site, and
    /// the very next tick demotes the excess Administrator exactly as it would for a billing-driven
    /// drop - reusing this job's own existing consequence, never a second one built for owner
    /// grants.</summary>
    [Fact]
    public async Task RunOnceAsync_AnOwnerGrantThatHasExpired_DemotesTheExcessAdministrator()
    {
        var siteId = await SeedSiteAsync(seatLimit: 5, adminLimit: 1);
        var adminRoleId = await SeedRoleAsync(siteId, "Admin");
        var earlierGranted = await SeedRoleHolderAsync(siteId, adminRoleId, Now - TimeSpan.FromDays(10));
        var laterGranted = await SeedRoleHolderAsync(siteId, adminRoleId, Now - TimeSpan.FromDays(1));
        var expiresAt = Now.AddHours(1);
        await SeedOwnerSeatGrantAsync(siteId, OwnerSeatGrantRole.Administrator, quantity: 1, expiresAt);

        // Still within the grant - a tick right now covers both.
        await CreateJob(now: Now).RunOnceAsync(CancellationToken.None);
        await AssertHoldsSeatAsync(laterGranted, adminRoleId, expected: true);
        await AssertHoldsSeatAsync(earlierGranted, adminRoleId, expected: true);

        // A fake clock advanced past the expiry - the next tick demotes the excess, most-recently-
        // granted-first, the identical tie-break every other reconciliation test in this file proves.
        var afterExpiry = expiresAt.AddMinutes(1);
        await CreateJob(now: afterExpiry).RunOnceAsync(CancellationToken.None);

        await AssertHoldsSeatAsync(laterGranted, adminRoleId, expected: false);
        await AssertHoldsSeatAsync(earlierGranted, adminRoleId, expected: true);
    }

    private EntitlementWatchdogJob CreateJob(IReadOnlyDictionary<string, string?>? entitlementMappings = null, DateTimeOffset? now = null)
    {
        var effectiveNow = now ?? Now;
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
        // `25-181`: the platform owner's own hand-granted seat extra - resolved live, every tick, by
        // this job's own ReconcileRoleSeatsAsync loop, against effectiveNow below.
        services.AddScoped<IOwnerSeatGrantStore, OwnerSeatGrantStore>();
        services.AddSingleton<IClock>(new FixedClock(effectiveNow));
        // `23-86`: the real ConfiguredBillingOptionEntitlementProvider, backed by an in-memory
        // configuration - the identical shape SubscriptionRenewalJobTests' own DirectScopeFactory uses,
        // for the identical reason (this item's own guard composes the real port, not a fake standing
        // in for the config-driven option-to-module mapping it resolves).
        var entitlementConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(
                (entitlementMappings ?? new Dictionary<string, string?> { [TelegramOptionKey] = TelegramModuleKey })
                .ToDictionary(kv => $"{ConfiguredBillingOptionEntitlementProvider.SectionName}:{kv.Key}", kv => kv.Value))
            .Build();
        services.AddSingleton<IConfiguration>(entitlementConfig);
        services.AddScoped<IBillingOptionEntitlementProvider, ConfiguredBillingOptionEntitlementProvider>();

        var provider = services.BuildServiceProvider();
        return new EntitlementWatchdogJob(
            provider.GetRequiredService<IServiceScopeFactory>(),
            fixture.DataSource,
            new FixedClock(effectiveNow),
            Options.Create(new EntitlementWatchdogJobOptions()),
            NullLogger<EntitlementWatchdogJob>.Instance);
    }

    private async Task<SiteId> SeedSiteAsync(int seatLimit = 1, int adminLimit = 1)
    {
        var siteId = new SiteId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], seatLimit: seatLimit));
        await db.SaveChangesAsync();
        // AdminLimit has no constructor parameter (Site's own remarks: derived from tier by default) -
        // the identical Entry(...).Property(...).CurrentValue write OperatorRoleSeatReconcilerTests'
        // own ReconcileAsync helper already uses to set it directly for a test.
        db.Entry(await db.Sites.SingleAsync(s => s.Id == siteId)).Property(nameof(Site.AdminLimit)).CurrentValue = adminLimit;
        await db.SaveChangesAsync();
        return siteId;
    }

    private async Task<Guid> SeedRoleAsync(SiteId siteId, string roleName)
    {
        var roleId = Guid.NewGuid();
        await using var db = fixture.CreateDbContext();
        db.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = roleName, Permissions = [Permission.ConversationAssign.Value] });
        await db.SaveChangesAsync();
        return roleId;
    }

    private async Task<OperatorId> SeedRoleHolderAsync(SiteId siteId, Guid roleId, DateTimeOffset grantedAt)
    {
        var operatorId = new OperatorId(Guid.NewGuid());
        await using var db = fixture.CreateDbContext();
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Offline, capacity: 5));
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = operatorId, RoleId = roleId, HoldsSeat = true, GrantedAt = grantedAt });
        await db.SaveChangesAsync();
        return operatorId;
    }

    private async Task<ChannelCredentialId> SeedActiveTelegramCredentialAsync(SiteId siteId)
    {
        var credentialId = new ChannelCredentialId(Guid.NewGuid());
        var credential = ChannelCredential.Register(
            credentialId, siteId, ChannelKind.Telegram, tokenCiphertext: [1], webhookSecretHash: [2], now: Now);
        await using var db = fixture.CreateDbContext();
        db.ChannelCredentials.Add(credential);
        await db.SaveChangesAsync();
        return credentialId;
    }

    private async Task SeedGrantAsync(SiteId siteId, int quantity)
    {
        await using var db = fixture.CreateDbContext();
        var outbox = new EfOutboxWriter<AgoChatDbContext>(db);
        var idGenerator = new UuidV7Generator();
        var grants = new ModuleQuantityGrantStore(db, outbox, idGenerator, new FixedClock(Now));
        await grants.GrantAsync(siteId, new ModuleKey(TelegramModuleKey), quantity, Now, CancellationToken.None);
    }

    /// <summary>`25-181`: seeds a real <c>owner_seat_grants</c> row through <see cref="OwnerSeatGrantStore"/> -
    /// production writes however it writes, the same shortcut <see cref="SeedGrantAsync"/> already
    /// takes for its own (site, module) grant.</summary>
    private async Task SeedOwnerSeatGrantAsync(
        SiteId siteId, OwnerSeatGrantRole role, int quantity, DateTimeOffset? expiresAt = null)
    {
        await using var db = fixture.CreateDbContext();
        var grants = new OwnerSeatGrantStore(db);
        await grants.GrantAsync(siteId, role, quantity, "owner-sub", "25-181 integration test", Now, expiresAt, CancellationToken.None);
    }

    private async Task<DateTimeOffset?> ReadEntitlementPausedAtAsync(ChannelCredentialId credentialId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.ChannelCredentials
            .Where(c => c.Id == credentialId)
            .Select(c => c.EntitlementPausedAt)
            .SingleAsync(CancellationToken.None);
    }

    private async Task AssertHoldsSeatAsync(OperatorId operatorId, Guid roleId, bool expected)
    {
        await using var db = fixture.CreateDbContext();
        var holdsSeat = await db.OperatorRoles
            .Where(or => or.OperatorId == operatorId && or.RoleId == roleId)
            .Select(or => or.HoldsSeat)
            .SingleAsync(CancellationToken.None);
        Assert.Equal(expected, holdsSeat);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
