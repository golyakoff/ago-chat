using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.GrantOwnerSeatsAsOwner;
using Ago.Chat.Application.UseCases.OperatorRoleSeats;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-181`'s own headline promise, proven where it is actually enforced, not only where it is
/// displayed: <see cref="Application.UseCases.GetOwnerSeatSummary.GetOwnerSeatSummaryHandlerTests"/>
/// already proves the summary line's own number rises and falls with a live grant, but that number
/// would be a lie if <see cref="OperatorRoleSeatCapacity"/> - the real gate every invite redemption,
/// role change, seat toggle and owner seat restore goes through - stayed blind to it. This file proves
/// the actual write path: an Admin invite refused at the tenant's own billing-only limit succeeds, with
/// no other change, once the platform owner grants one more Administrator seat by hand.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OwnerSeatGrantCapacityTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AnAdminInvite_RefusedAtTheBillingLimit_SucceedsOnceTheOwnerGrantsOneMoreAdministratorSeat()
    {
        var seed = await SeedSiteWithOneAdministratorAsync(adminLimit: 1);

        var firstInvite = await GenerateAdminInviteAsync(seed, "second-admin-1@example.com");
        var refused = await RedeemAsync(firstInvite);
        Assert.IsType<OperatorInviteRedemptionResult.AdminLimitReached>(refused);

        // The owner's own hand-granted extra - through the real handler, exactly as the owner console
        // calls it, not a direct store write.
        await using (var db = fixture.CreateDbContext())
        {
            var handler = new GrantOwnerSeatsAsOwnerHandler(
                new OwnerSeatGrantStore(db), new SiteRepository(db), new SystemClock());
            var result = await handler.HandleAsync(
                new GrantOwnerSeatsAsOwner(
                    seed.SiteId, OwnerSeatGrantRole.Administrator, Quantity: 1, GrantedBy: "owner-sub",
                    Reason: "Covering an incident while billing is sorted out.", ExpiresAt: null),
                CancellationToken.None);
            Assert.True(result.IsSuccess);
        }

        // A fresh invite (the first one is already consumed by the refusal path's own row state) -
        // same site, same role, nothing else about the tenant changed.
        var secondInvite = await GenerateAdminInviteAsync(seed, "second-admin-2@example.com");
        var succeeded = await RedeemAsync(secondInvite);
        Assert.IsType<OperatorInviteRedemptionResult.Success>(succeeded);

        var finalAdministratorCount = await CountAdministratorsAsync(seed);
        Assert.Equal(2, finalAdministratorCount); // the founder plus the one just redeemed
    }

    /// <summary>The negative control this file's own headline test relies on to mean what it says: with
    /// no grant at all, a second Admin invite stays refused - proven here as its own assertion so a
    /// future change that broke the refusal path itself (rather than the grant) would fail here, not
    /// silently pass the headline test for the wrong reason.</summary>
    [Fact]
    public async Task AnAdminInvite_WithNoOwnerGrantAtAll_StaysRefused()
    {
        var seed = await SeedSiteWithOneAdministratorAsync(adminLimit: 1);
        var invite = await GenerateAdminInviteAsync(seed, "second-admin@example.com");

        var refused = await RedeemAsync(invite);

        Assert.IsType<OperatorInviteRedemptionResult.AdminLimitReached>(refused);
    }

    /// <summary>The expiry half of the same promise, at this same enforcement point: a grant that has
    /// already lapsed does not count - the identical live-clock check
    /// <see cref="Domain.OwnerSeatGrantTests"/> proves at the domain level, proven here against the
    /// actual capacity gate.</summary>
    [Fact]
    public async Task AnExpiredOwnerGrant_NoLongerCountsToward_TheCapacityCheck()
    {
        var seed = await SeedSiteWithOneAdministratorAsync(adminLimit: 1);
        await using (var db = fixture.CreateDbContext())
        {
            var grants = new OwnerSeatGrantStore(db);
            await grants.GrantAsync(
                seed.SiteId, OwnerSeatGrantRole.Administrator, quantity: 1, "owner-sub", "A lapsed trial extra.",
                now: Now.AddDays(-2), expiresAt: Now.AddDays(-1), CancellationToken.None);
        }

        var invite = await GenerateAdminInviteAsync(seed, "second-admin@example.com");
        var refused = await RedeemAsync(invite);

        Assert.IsType<OperatorInviteRedemptionResult.AdminLimitReached>(refused);
    }

    private sealed record Seed(SiteId SiteId, Guid AdminRoleId, OperatorId CreatedByOperatorId);

    /// <summary>The identical seeding shape `OperatorInviteAdminLimitConcurrencyTests`' own
    /// `SeedSiteAsync` already establishes, narrowed to this file's own fixed one-existing-Administrator
    /// case.</summary>
    private async Task<Seed> SeedSiteWithOneAdministratorAsync(int adminLimit)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var adminRoleId = Guid.NewGuid();
        var operatorRoleId = Guid.NewGuid();
        var creatorId = new OperatorId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        var site = new Site(siteId, $"site_{siteId.Value:N}", [], tier: SubscriptionTierBands.Growth, seatLimit: 100);
        db.Sites.Add(site);
        db.Entry(site).Property(nameof(Site.AdminLimit)).CurrentValue = adminLimit;
        db.Roles.Add(new RoleRecord { Id = adminRoleId, SiteId = siteId, Name = "Admin", Permissions = [Permission.SiteManageOperators.Value] });
        db.Roles.Add(new RoleRecord { Id = operatorRoleId, SiteId = siteId, Name = "Operator", Permissions = [Permission.ConversationRead.Value] });
        db.Operators.Add(new Operator(creatorId, siteId, OperatorStatus.Online, capacity: 5, externalSubjectId: "creator"));
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = creatorId, RoleId = adminRoleId });
        db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = creatorId, RoleId = operatorRoleId });
        await db.SaveChangesAsync(CancellationToken.None);
        return new Seed(siteId, adminRoleId, creatorId);
    }

    private sealed record GeneratedInvite(byte[] CodeHash, string Email);

    private async Task<GeneratedInvite> GenerateAdminInviteAsync(Seed seed, string email)
    {
        await using var db = fixture.CreateDbContext();
        var codeHash = new byte[32];
        Random.Shared.NextBytes(codeHash);
        var invite = OperatorInvite.Generate(
            new OperatorInviteId(Guid.NewGuid()), seed.SiteId, seed.AdminRoleId, codeHash, email, seed.CreatedByOperatorId, Now,
            TimeSpan.FromDays(7));
        db.OperatorInvites.Add(invite);
        await db.SaveChangesAsync(CancellationToken.None);
        return new GeneratedInvite(codeHash, email);
    }

    private async Task<OperatorInviteRedemptionResult> RedeemAsync(GeneratedInvite invite)
    {
        await using var db = fixture.CreateDbContext();
        var operatorRoles = new OperatorRoleRepository(db);
        var roleSeatCapacity = new OperatorRoleSeatCapacity(operatorRoles, new SiteRepository(db), new OwnerSeatGrantStore(db), new SystemClock());
        var repository = new OperatorInviteRedemptionRepository(
            db, new UuidV7Generator(), new EfOutboxWriter<AgoChatDbContext>(db), roleSeatCapacity);
        return await repository.RedeemAsync(
            new RedeemOperatorInviteAttempt(invite.CodeHash, $"external-{Guid.NewGuid():N}", Now.AddMinutes(1), Email: invite.Email),
            CancellationToken.None);
    }

    private async Task<int> CountAdministratorsAsync(Seed seed)
    {
        await using var db = fixture.CreateDbContext();
        return await db.OperatorRoles.AsNoTracking()
            .Where(link => link.RoleId == seed.AdminRoleId)
            .Join(db.Operators.AsNoTracking(), link => link.OperatorId, o => o.Id, (link, o) => o)
            .CountAsync(o => o.SiteId == seed.SiteId && o.RemovedAt == null);
    }
}
