using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>
/// `25-25`'s own Done-when, at the only level that can prove it - the identical shape
/// <see cref="OperatorInviteSeatLimitConcurrencyTests"/> already established for its sibling limit,
/// over the same primitive (<c>OperatorInviteRedemptionRepository.LockSiteAndReadCapacityAsync`'s own
/// `SELECT ... FOR UPDATE` lock on `sites`, extended by this item to also read `admin_limit`): "a site
/// with `admin_limit = 2` and one existing Administrator, N concurrently-redeemed Admin invites racing
/// for the one remaining Administrator slot - exactly one redemption succeeds, the rest get
/// `AdminLimitReached`, and the site's final Administrator count never exceeds `admin_limit`."
///
/// <para>Also proves this item's own independence requirement under real contention, not merely at
/// rest: none of the concurrently-redeemed Administrators ever consumes an operator seat, so the
/// site's held-seat count never moves even while the Administrator count is being raced over.</para>
/// </summary>
[Collection(ConcurrencyCollection.Name)]
public sealed class OperatorInviteAdminLimitConcurrencyTests(ConcurrencyTestFixture fixture, ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConcurrentRedemptions_RacingForTheOneRemainingAdministratorSlot_ExactlyOneSucceeds()
    {
        const int adminLimit = 2;
        const int existingAdministrators = 1;
        const int concurrentRedeemers = 20; // far more than the one remaining Administrator slot

        var seed = await SeedSiteAsync(adminLimit, existingAdministrators);
        var invites = await GenerateAdminInvitesAsync(seed, concurrentRedeemers);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = invites.Select((invite, index) => Task.Run(async () =>
        {
            await gate.Task;
            return await RedeemAsync(invite, $"redeemer-{index}");
        })).ToList();

        gate.SetResult();
        var outcomes = await Task.WhenAll(tasks);

        var successes = outcomes.OfType<OperatorInviteRedemptionResult.Success>().ToList();
        var adminLimited = outcomes.OfType<OperatorInviteRedemptionResult.AdminLimitReached>().ToList();

        output.WriteLine(
            $"successes={successes.Count}; adminLimited={adminLimited.Count}; total={outcomes.Length}");

        // Exactly one remaining slot, exactly one winner - not "at most one", not "roughly one".
        Assert.Single(successes);
        Assert.Equal(concurrentRedeemers - 1, adminLimited.Count);
        Assert.All(
            outcomes,
            o => Assert.True(o is OperatorInviteRedemptionResult.Success or OperatorInviteRedemptionResult.AdminLimitReached));

        await using var verify = fixture.CreateDbContext();
        var finalAdministratorCount = await verify.OperatorRoles.AsNoTracking()
            .Where(link => link.RoleId == seed.AdminRoleId)
            .Join(verify.Operators.AsNoTracking(), link => link.OperatorId, o => o.Id, (link, o) => o)
            .CountAsync(o => o.SiteId == seed.SiteId && o.RemovedAt == null);
        Assert.Equal(adminLimit, finalAdministratorCount);

        // `25-25`'s own independence requirement, under contention: the seat count the *other* limit
        // gates never moved, even while N callers raced the Administrator limit - only the one
        // existing operator row (the founder, seeded below) ever held a seat.
        var heldSeats = await verify.Operators.AsNoTracking()
            .CountAsync(o => o.SiteId == seed.SiteId && o.RemovedAt == null && o.HoldsSeat);
        Assert.Equal(1, heldSeats);

        var redeemedInvites = await verify.OperatorInvites.AsNoTracking()
            .Where(i => i.SiteId == seed.SiteId && i.RedeemedAt != null)
            .CountAsync();
        Assert.Equal(1, redeemedInvites);
    }

    private sealed record Seed(SiteId SiteId, Guid AdminRoleId, OperatorId CreatedByOperatorId);

    private async Task<Seed> SeedSiteAsync(int adminLimit, int existingAdministrators)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var adminRoleId = Guid.NewGuid();
        var creatorId = new OperatorId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        // A high, unconstraining seat_limit - this test's own subject is admin_limit alone, and a
        // seat-limit rejection on any of these redemptions would be a different failure this test must
        // not silently pass through.
        var site = new Site(siteId, $"site_{siteId.Value:N}", [], tier: SubscriptionTierBands.Growth, seatLimit: 100);
        db.Sites.Add(site);
        // The row-locked read this test exercises (`LockSiteAndReadCapacityAsync`) reads `admin_limit`
        // straight off the column - `SubscriptionTierBands.Growth`'s own derived default is `2`, but
        // this write is explicit and independent of it, matching `OperatorInviteSeatLimitConcurrencyTests`'
        // own explicit `seatLimit:` argument rather than relying on a tier's default to happen to agree.
        db.Entry(site).Property(nameof(Site.AdminLimit)).CurrentValue = adminLimit;
        db.Roles.Add(new RoleRecord
        {
            Id = adminRoleId,
            SiteId = siteId,
            Name = "Admin",
            Permissions = [Permission.SiteManageOperators.Value],
        });
        // The founder - holds a seat (registration's own default) and the Admin role, the identical
        // shape RegisterSiteHandler gives a real founder.
        db.Operators.Add(new Operator(creatorId, siteId, OperatorStatus.Online, capacity: 5, externalSubjectId: "creator", holdsSeat: true));
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = creatorId, RoleId = adminRoleId });
        for (var i = 1; i < existingAdministrators; i++)
        {
            var extraAdminId = new OperatorId(Guid.NewGuid());
            db.Operators.Add(new Operator(
                extraAdminId, siteId, OperatorStatus.Online, capacity: 5, externalSubjectId: $"existing-admin-{i}", holdsSeat: false));
            db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = extraAdminId, RoleId = adminRoleId });
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return new Seed(siteId, adminRoleId, creatorId);
    }

    private async Task<List<byte[]>> GenerateAdminInvitesAsync(Seed seed, int count)
    {
        var codeHashes = new List<byte[]>();
        await using var db = fixture.CreateDbContext();
        for (var i = 0; i < count; i++)
        {
            var codeHash = new byte[32];
            Random.Shared.NextBytes(codeHash);
            var invite = OperatorInvite.Generate(
                new OperatorInviteId(Guid.NewGuid()), seed.SiteId, seed.AdminRoleId, codeHash, seed.CreatedByOperatorId, Now,
                TimeSpan.FromDays(7));
            db.OperatorInvites.Add(invite);
            codeHashes.Add(codeHash);
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return codeHashes;
    }

    private async Task<OperatorInviteRedemptionResult> RedeemAsync(byte[] codeHash, string externalSubjectId)
    {
        await using var db = fixture.CreateDbContext();
        var repository = new OperatorInviteRedemptionRepository(db, new UuidV7Generator(), new EfOutboxWriter<AgoChatDbContext>(db));
        return await repository.RedeemAsync(
            new RedeemOperatorInviteAttempt(codeHash, externalSubjectId, Now.AddMinutes(1)), CancellationToken.None);
    }
}
