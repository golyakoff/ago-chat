using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.OperatorRoleSeats;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `13-03`'s own named regression: `13-01`'s <see cref="OperatorInviteRedemptionRepository"/> counted
/// every `operators` row for a site, including one this item's own <see cref="Operator.Remove"/> has
/// since marked <see cref="Operator.RemovedAt"/> - without the `AND removed_at IS NULL` fix, a removed
/// operator counted against the seat limit forever. Proven against real Postgres: a site at its
/// `seat_limit` cannot redeem a new invite, then can immediately after removing one existing operator,
/// in the same test.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OperatorRemovalSeatCountRegressionTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RemovingAnOperator_FreesTheSeatItOccupied_SoANewInviteCanBeRedeemedImmediately()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var roleId = Guid.NewGuid();
        var existingOperatorId = new OperatorId(Guid.NewGuid());

        await using (var db = fixture.CreateDbContext())
        {
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], tier: SubscriptionTierBands.Starter, seatLimit: 1));
            db.Roles.Add(new RoleRecord { Id = roleId, SiteId = siteId, Name = "Operator", Permissions = [Permission.ConversationRead.Value] });
            db.Operators.Add(new Operator(existingOperatorId, siteId, OperatorStatus.Offline, capacity: 5, externalSubjectId: "sub-existing"));
            // `25-170`: the seat-limit check now counts operator_roles rows for the Operator role
            // specifically - this operator needs a real one (HoldsSeat defaults true) to occupy the
            // site's only seat the way this test's own baseline requires.
            db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = existingOperatorId, RoleId = roleId });
            await db.SaveChangesAsync();
        }

        var (codeHash1, _) = await SeedInviteAsync(siteId, roleId, "new1@example.com");
        await using var repositoryDb = fixture.CreateDbContext();
        // `25-181`: a real OwnerSeatGrantStore with nothing seeded reads 0 - this test keeps
        // exercising the identical billing-only seat limit it always has.
        var roleSeatCapacity = new OperatorRoleSeatCapacity(
            new OperatorRoleRepository(repositoryDb), new SiteRepository(repositoryDb),
            new OwnerSeatGrantStore(repositoryDb), new Ago.Platform.Hosting.SystemClock());
        var repository = new OperatorInviteRedemptionRepository(
            repositoryDb, new Ago.Platform.Kernel.UuidV7Generator(), new EfOutboxWriter<AgoChatDbContext>(repositoryDb), roleSeatCapacity);

        // At seat_limit=1 with one live operator already occupying the site's only seat, a new
        // identity's redemption is rejected - the baseline every removal must actually change.
        // `25-73`: the attempt's own email must match the invite's own email now - "new1@example.com"
        // both times, the same address SeedInviteAsync generated the invite for.
        var beforeRemoval = await repository.RedeemAsync(
            new RedeemOperatorInviteAttempt(codeHash1, "sub-new-1", Now, Email: "new1@example.com"), CancellationToken.None);
        Assert.IsType<OperatorInviteRedemptionResult.SeatLimitReached>(beforeRemoval);

        await using (var db = fixture.CreateDbContext())
        {
            var existing = await db.Operators.SingleAsync(o => o.Id == existingOperatorId);
            existing.Remove(Now);
            await db.SaveChangesAsync();
        }

        var (codeHash2, _) = await SeedInviteAsync(siteId, roleId, "new2@example.com");
        var afterRemoval = await repository.RedeemAsync(
            new RedeemOperatorInviteAttempt(codeHash2, "sub-new-2", Now, Email: "new2@example.com"), CancellationToken.None);

        var success = Assert.IsType<OperatorInviteRedemptionResult.Success>(afterRemoval);
        Assert.Equal(siteId, success.SiteId);
    }

    private async Task<(byte[] CodeHash, OperatorInviteId InviteId)> SeedInviteAsync(SiteId siteId, Guid roleId, string email)
    {
        var inviteId = new OperatorInviteId(Guid.NewGuid());
        var codeHash = SHA256.HashData(Encoding.UTF8.GetBytes($"code-{Guid.NewGuid():N}"));

        await using var db = fixture.CreateDbContext();
        var invite = OperatorInvite.Generate(
            inviteId, siteId, roleId, codeHash, email, new OperatorId(Guid.NewGuid()), Now, TimeSpan.FromDays(7));
        db.OperatorInvites.Add(invite);
        await db.SaveChangesAsync();

        return (codeHash, inviteId);
    }
}
