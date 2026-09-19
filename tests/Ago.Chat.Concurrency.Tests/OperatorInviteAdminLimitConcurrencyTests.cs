using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.OperatorRoleSeats;
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
        // `25-73`: each redeemer presents the exact email its own invite was generated for - the new
        // code-and-email redemption check requires both to agree, and this test's own subject (the
        // admin-limit race) must not be pre-empted by an unrelated EmailMismatch on every attempt.
        var tasks = invites.Select((invite, index) => Task.Run(async () =>
        {
            await gate.Task;
            return await RedeemAsync(invite.CodeHash, invite.Email, $"redeemer-{index}");
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

        // `25-25`/`25-170`'s own independence requirement, under contention: the Operator role's own
        // held-seat count (the *other* limit gates) never moved, even while N callers raced the
        // Administrator limit - only the one existing operator row (the founder, seeded below, holding
        // both seeded roles the way a real founder does) ever held that seat.
        var heldSeats = await verify.OperatorRoles.AsNoTracking()
            .Where(link => link.RoleId == seed.OperatorRoleId && link.HoldsSeat)
            .Join(verify.Operators.AsNoTracking(), link => link.OperatorId, o => o.Id, (link, o) => o)
            .CountAsync(o => o.SiteId == seed.SiteId && o.RemovedAt == null);
        Assert.Equal(1, heldSeats);

        var redeemedInvites = await verify.OperatorInvites.AsNoTracking()
            .Where(i => i.SiteId == seed.SiteId && i.RedeemedAt != null)
            .CountAsync();
        Assert.Equal(1, redeemedInvites);
    }

    private sealed record Seed(SiteId SiteId, Guid AdminRoleId, Guid OperatorRoleId, OperatorId CreatedByOperatorId);

    private async Task<Seed> SeedSiteAsync(int adminLimit, int existingAdministrators)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var adminRoleId = Guid.NewGuid();
        var operatorRoleId = Guid.NewGuid();
        var creatorId = new OperatorId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        // A high, unconstraining seat_limit - this test's own subject is admin_limit alone, and a
        // seat-limit rejection on any of these redemptions would be a different failure this test must
        // not silently pass through.
        var site = new Site(siteId, $"site_{siteId.Value:N}", [], tier: SubscriptionTierBands.Growth, seatLimit: 100);
        db.Sites.Add(site);
        // The row-locked read this test exercises (`OperatorRoleSeatCapacity`, `25-170`'s unified
        // procedure) reads `admin_limit` straight off the column - `SubscriptionTierBands.Growth`'s own
        // derived default is `2`, but this write is explicit and independent of it, matching
        // `OperatorInviteSeatLimitConcurrencyTests`' own explicit `seatLimit:` argument rather than
        // relying on a tier's default to happen to agree.
        db.Entry(site).Property(nameof(Site.AdminLimit)).CurrentValue = adminLimit;
        db.Roles.Add(new RoleRecord
        {
            Id = adminRoleId,
            SiteId = siteId,
            Name = "Admin",
            Permissions = [Permission.SiteManageOperators.Value],
        });
        db.Roles.Add(new RoleRecord
        {
            Id = operatorRoleId,
            SiteId = siteId,
            Name = "Operator",
            Permissions = [Permission.ConversationRead.Value],
        });
        // The founder - holds both seeded roles from registration, the identical shape
        // RegisterSiteHandler gives a real founder (`25-170`: the Operator-role row's own HoldsSeat
        // default carries what this test's own account-level `holdsSeat: true` used to mean).
        db.Operators.Add(new Operator(creatorId, siteId, OperatorStatus.Online, capacity: 5, externalSubjectId: "creator"));
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = creatorId, RoleId = adminRoleId });
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = creatorId, RoleId = operatorRoleId });
        for (var i = 1; i < existingAdministrators; i++)
        {
            var extraAdminId = new OperatorId(Guid.NewGuid());
            // `25-170`: no Operator-role row for this operator at all - an Administrator invited
            // directly never held one, the identical fact this test's own pre-`25-170` `holdsSeat: false`
            // argument used to express at the account level.
            db.Operators.Add(new Operator(
                extraAdminId, siteId, OperatorStatus.Online, capacity: 5, externalSubjectId: $"existing-admin-{i}"));
            db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = extraAdminId, RoleId = adminRoleId });
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return new Seed(siteId, adminRoleId, operatorRoleId, creatorId);
    }

    private sealed record GeneratedInvite(byte[] CodeHash, string Email);

    private async Task<List<GeneratedInvite>> GenerateAdminInvitesAsync(Seed seed, int count)
    {
        var generated = new List<GeneratedInvite>();
        await using var db = fixture.CreateDbContext();
        for (var i = 0; i < count; i++)
        {
            var codeHash = new byte[32];
            Random.Shared.NextBytes(codeHash);
            var email = $"invitee{i}@example.com";
            var invite = OperatorInvite.Generate(
                new OperatorInviteId(Guid.NewGuid()), seed.SiteId, seed.AdminRoleId, codeHash, email,
                seed.CreatedByOperatorId, Now, TimeSpan.FromDays(7));
            db.OperatorInvites.Add(invite);
            generated.Add(new GeneratedInvite(codeHash, email));
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return generated;
    }

    private async Task<OperatorInviteRedemptionResult> RedeemAsync(byte[] codeHash, string email, string externalSubjectId)
    {
        await using var db = fixture.CreateDbContext();
        var roleSeatCapacity = new OperatorRoleSeatCapacity(new OperatorRoleRepository(db), new SiteRepository(db));
        var repository = new OperatorInviteRedemptionRepository(
            db, new UuidV7Generator(), new EfOutboxWriter<AgoChatDbContext>(db), roleSeatCapacity);
        return await repository.RedeemAsync(
            new RedeemOperatorInviteAttempt(codeHash, externalSubjectId, Now.AddMinutes(1), Email: email), CancellationToken.None);
    }
}
