using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `23-02`'s own Done-when, the third clause: "a second call with unchanged claims writes no row,
/// asserted on the command rather than by eye." <see cref="OperatorRepository.RefreshIdentityAsync"/>
/// returns whether it actually wrote - exactly so this is checkable on the method's own return value,
/// not by comparing two snapshots of the row by eye. Real Postgres, no Keycloak: the conditional
/// `UPDATE`'s own `IS DISTINCT FROM` guard is a database behaviour, the same reasoning
/// <see cref="OperatorCapacityStoreTests"/> already applies to `active_chats`' compare-and-set on this
/// identical table.
/// </summary>
[Collection(PostgresCollection.Name)]
public class OperatorIdentityRefreshTests(PostgresFixture fixture)
{
    [Fact]
    public async Task RefreshIdentityAsync_TheFirstCallWithARealName_WritesTheRow_AndReturnsTrue()
    {
        var operatorId = await SeedOperatorAsync();

        bool wrote;
        await using (var db = fixture.CreateDbContext())
        {
            wrote = await new OperatorRepository(db).RefreshIdentityAsync(
                operatorId, "Ivan Petrov", "ivan@example.test", CancellationToken.None);
        }

        Assert.True(wrote);
        Assert.Equal(("Ivan Petrov", "ivan@example.test"), await ReadIdentityAsync(operatorId));
    }

    /// <summary>The exact clause: identical claims, second call, no write.</summary>
    [Fact]
    public async Task RefreshIdentityAsync_ASecondCallWithUnchangedClaims_WritesNoRow_AndReturnsFalse()
    {
        var operatorId = await SeedOperatorAsync();
        await using (var db = fixture.CreateDbContext())
        {
            Assert.True(await new OperatorRepository(db).RefreshIdentityAsync(
                operatorId, "Ivan Petrov", "ivan@example.test", CancellationToken.None));
        }

        bool wroteAgain;
        await using (var db = fixture.CreateDbContext())
        {
            wroteAgain = await new OperatorRepository(db).RefreshIdentityAsync(
                operatorId, "Ivan Petrov", "ivan@example.test", CancellationToken.None);
        }

        Assert.False(wroteAgain);
        Assert.Equal(("Ivan Petrov", "ivan@example.test"), await ReadIdentityAsync(operatorId));
    }

    /// <summary>A genuinely different value on the second call still writes - the guard is "unchanged",
    /// never "already written once."</summary>
    [Fact]
    public async Task RefreshIdentityAsync_ASecondCallWithADifferentName_WritesTheNewValue_AndReturnsTrue()
    {
        var operatorId = await SeedOperatorAsync();
        await using (var db = fixture.CreateDbContext())
        {
            Assert.True(await new OperatorRepository(db).RefreshIdentityAsync(
                operatorId, "Ivan Petrov", "ivan@example.test", CancellationToken.None));
        }

        bool wroteAgain;
        await using (var db = fixture.CreateDbContext())
        {
            wroteAgain = await new OperatorRepository(db).RefreshIdentityAsync(
                operatorId, "Ivan Sidorov", "ivan@example.test", CancellationToken.None);
        }

        Assert.True(wroteAgain);
        Assert.Equal(("Ivan Sidorov", "ivan@example.test"), await ReadIdentityAsync(operatorId));
    }

    /// <summary>The `IS DISTINCT FROM` reason this is not a bare `&lt;&gt;` - two `NULL`s (the demo
    /// tenant's own shape, `MintDemoTenantHandler`'s own remarks) must not look like a change forever.
    /// Without this, an identity with no claims to give would cost a real write on every single sign-in
    /// it never actually has (it is never authenticated through this path at all) - stated here anyway,
    /// because a future caller passing `null`/`null` for an ordinary operator must see the identical
    /// no-op this test proves.</summary>
    [Fact]
    public async Task RefreshIdentityAsync_CalledTwiceWithNoClaimsAtAll_WritesNoRow_AndReturnsFalse()
    {
        var operatorId = await SeedOperatorAsync();
        await using (var db = fixture.CreateDbContext())
        {
            Assert.False(await new OperatorRepository(db).RefreshIdentityAsync(
                operatorId, null, null, CancellationToken.None));
        }

        bool secondCall;
        await using (var db = fixture.CreateDbContext())
        {
            secondCall = await new OperatorRepository(db).RefreshIdentityAsync(
                operatorId, null, null, CancellationToken.None);
        }

        Assert.False(secondCall);
        Assert.Equal((null, null), await ReadIdentityAsync(operatorId));
    }

    private async Task<OperatorId> SeedOperatorAsync()
    {
        var siteId = new SiteId(Guid.NewGuid());
        var operatorId = new OperatorId(Guid.NewGuid());

        await using var db = fixture.CreateDbContext();
        db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
        db.Operators.Add(new Operator(operatorId, siteId, OperatorStatus.Online, capacity: 5));
        await db.SaveChangesAsync();

        return operatorId;
    }

    private async Task<(string? DisplayName, string? Email)> ReadIdentityAsync(OperatorId operatorId)
    {
        await using var db = fixture.CreateDbContext();
        var row = await db.Operators.AsNoTracking().SingleAsync(o => o.Id == operatorId);
        return (row.DisplayName, row.Email);
    }
}
