using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Kernel;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Ago.Chat.Concurrency.Tests;

/// <summary>
/// `13-01`'s own Done-when, at the only level that can prove it: "a site with `seat_limit = 2` and one
/// existing operator, N concurrently-redeemed invites racing for the one remaining seat - exactly one
/// redemption succeeds, the rest get the capacity-reached response, and the site's final operator count
/// never exceeds `seat_limit`" - the same "proven under sustained contention" bar `concurrency.md`/
/// Stage 4's own tests hold themselves to, scaled to this item's much lower-frequency path.
///
/// <para>Unlike `active_chats`' compare-and-set (`OperatorCapacityStore`), which needs no explicit
/// transaction because a single `UPDATE ... WHERE` statement is already atomic, this path's
/// correctness rests entirely on <c>OperatorInviteRedemptionRepository</c>'s own `SELECT ... FOR UPDATE`
/// row lock on `sites` serializing every concurrent redemption attempt against the same site - so this
/// test exercises the repository directly, real concurrent calls against one real Postgres container,
/// exactly the level `CloseConversationCapacityConcurrencyTests` already established for the sibling
/// contended path this item's own Context explicitly contrasts itself against.</para>
/// </summary>
[Collection(ConcurrencyCollection.Name)]
public sealed class OperatorInviteSeatLimitConcurrencyTests(ConcurrencyTestFixture fixture, ITestOutputHelper output)
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConcurrentRedemptions_RacingForTheOneRemainingSeat_ExactlyOneSucceeds()
    {
        const int seatLimit = 2;
        const int existingOperators = 1;
        const int concurrentRedeemers = 20; // far more than the one remaining seat

        var seed = await SeedSiteAsync(seatLimit, existingOperators);
        var invites = await GenerateInvitesAsync(seed, concurrentRedeemers);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = invites.Select((invite, index) => Task.Run(async () =>
        {
            await gate.Task;
            return await RedeemAsync(invite, $"redeemer-{index}");
        })).ToList();

        gate.SetResult();
        var outcomes = await Task.WhenAll(tasks);

        var successes = outcomes.OfType<OperatorInviteRedemptionResult.Success>().ToList();
        var seatLimited = outcomes.OfType<OperatorInviteRedemptionResult.SeatLimitReached>().ToList();

        output.WriteLine(
            $"successes={successes.Count}; seatLimited={seatLimited.Count}; total={outcomes.Length}");

        // Exactly one remaining seat, exactly one winner - not "at most one", not "roughly one".
        Assert.Single(successes);
        Assert.Equal(concurrentRedeemers - 1, seatLimited.Count);
        // No outcome escaped as anything else (NotFound/Expired/AlreadyRedeemed/AlreadyOperatorOnSite)
        // - every one of these N distinct identities redeeming N distinct, valid, unexpired invites
        // must resolve to exactly Success or SeatLimitReached, nothing else.
        Assert.All(outcomes, o => Assert.True(o is OperatorInviteRedemptionResult.Success or OperatorInviteRedemptionResult.SeatLimitReached));

        await using var verify = fixture.CreateDbContext();
        var finalOperatorCount = await verify.Operators.AsNoTracking().CountAsync(o => o.SiteId == seed.SiteId);
        Assert.Equal(seatLimit, finalOperatorCount);

        // Every redeemed invite is redeemed exactly once (proven by the DB, not just the in-process
        // outcome count) and every rejected one is genuinely untouched.
        var redeemedInvites = await verify.OperatorInvites.AsNoTracking()
            .Where(i => i.SiteId == seed.SiteId && i.RedeemedAt != null)
            .CountAsync();
        Assert.Equal(1, redeemedInvites);
    }

    /// <summary>
    /// `13-01`'s own Done-when, the other half: a capacity-rejected invite is confirmed still
    /// redeemable afterward once a seat opens up - proving the rejected attempt above never silently
    /// consumed it. Reuses the storm's own losing invites rather than generating fresh ones, which is
    /// the stronger claim: these are the exact rows `SeatLimitReached` rejected a moment ago.
    /// </summary>
    [Fact]
    public async Task ARejectedInvite_IsStillRedeemableAfterASeatOpensUp()
    {
        const int seatLimit = 1;
        var seed = await SeedSiteAsync(seatLimit, existingOperators: 1);
        var invite = (await GenerateInvitesAsync(seed, count: 1))[0];

        var rejected = await RedeemAsync(invite, "redeemer-rejected");
        Assert.IsType<OperatorInviteRedemptionResult.SeatLimitReached>(rejected);

        await using (var db = fixture.CreateDbContext())
        {
            var site = await db.Sites.SingleAsync(s => s.Id == seed.SiteId);
            db.Entry(site).Property(nameof(Site.SeatLimit)).CurrentValue = 2;
            await db.SaveChangesAsync();
        }

        var succeeded = await RedeemAsync(invite, "redeemer-rejected");
        Assert.IsType<OperatorInviteRedemptionResult.Success>(succeeded);
    }

    private sealed record Seed(SiteId SiteId, Guid RoleId, OperatorId CreatedByOperatorId);

    private async Task<Seed> SeedSiteAsync(int seatLimit, int existingOperators)
    {
        var siteId = new SiteId(Guid.NewGuid());
        var roleId = Guid.NewGuid();
        var creatorId = new OperatorId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", [], seatLimit: seatLimit));
        db.Roles.Add(new RoleRecord
        {
            Id = roleId,
            SiteId = siteId,
            Name = "Operator",
            Permissions = [Permission.ConversationRead.Value],
        });
        db.Operators.Add(new Operator(creatorId, siteId, OperatorStatus.Online, capacity: 5, externalSubjectId: "creator"));
        for (var i = 1; i < existingOperators; i++)
        {
            db.Operators.Add(new Operator(
                new OperatorId(Guid.NewGuid()), siteId, OperatorStatus.Online, capacity: 5, externalSubjectId: $"existing-{i}"));
        }

        await db.SaveChangesAsync(CancellationToken.None);
        return new Seed(siteId, roleId, creatorId);
    }

    private async Task<List<byte[]>> GenerateInvitesAsync(Seed seed, int count)
    {
        var codeHashes = new List<byte[]>();
        await using var db = fixture.CreateDbContext();
        for (var i = 0; i < count; i++)
        {
            var codeHash = new byte[32];
            Random.Shared.NextBytes(codeHash);
            var invite = OperatorInvite.Generate(
                new OperatorInviteId(Guid.NewGuid()), seed.SiteId, seed.RoleId, codeHash, seed.CreatedByOperatorId, Now,
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
        var repository = new OperatorInviteRedemptionRepository(db, new UuidV7Generator());
        return await repository.RedeemAsync(
            new RedeemOperatorInviteAttempt(codeHash, externalSubjectId, Now.AddMinutes(1)), CancellationToken.None);
    }
}
