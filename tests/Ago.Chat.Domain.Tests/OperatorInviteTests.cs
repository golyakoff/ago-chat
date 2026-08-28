namespace Ago.Chat.Domain.Tests;

public class OperatorInviteTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId CreatedBy = new(Guid.NewGuid());
    private static readonly Guid RoleId = Guid.NewGuid();

    private static OperatorInvite Generate(TimeSpan? validFor = null) =>
        OperatorInvite.Generate(
            new OperatorInviteId(Guid.NewGuid()), SiteId, RoleId, [1, 2, 3], CreatedBy, Now,
            validFor ?? TimeSpan.FromDays(7));

    [Fact]
    public void Generate_StartsUnredeemed()
    {
        var invite = Generate();

        Assert.False(invite.IsRedeemed);
        Assert.Null(invite.RedeemedAt);
        Assert.Null(invite.RedeemedByOperatorId);
    }

    [Fact]
    public void Generate_SetsExpiresAtToNowPlusValidFor()
    {
        var invite = Generate(TimeSpan.FromDays(7));

        Assert.Equal(Now + TimeSpan.FromDays(7), invite.ExpiresAt);
    }

    [Fact]
    public void Redeem_MarksRedeemedByTheGivenOperator()
    {
        var invite = Generate();
        var redeemingOperatorId = new OperatorId(Guid.NewGuid());
        var redeemedAt = Now + TimeSpan.FromMinutes(5);

        invite.Redeem(redeemingOperatorId, redeemedAt);

        Assert.True(invite.IsRedeemed);
        Assert.Equal(redeemedAt, invite.RedeemedAt);
        Assert.Equal(redeemingOperatorId, invite.RedeemedByOperatorId);
    }

    [Fact]
    public void Redeem_WhenAlreadyRedeemed_Throws()
    {
        var invite = Generate();
        invite.Redeem(new OperatorId(Guid.NewGuid()), Now + TimeSpan.FromMinutes(5));

        Assert.Throws<InvalidOperatorInviteStateException>(
            () => invite.Redeem(new OperatorId(Guid.NewGuid()), Now + TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void Redeem_WhenExpired_Throws()
    {
        var invite = Generate(TimeSpan.FromDays(7));
        var afterExpiry = invite.ExpiresAt + TimeSpan.FromSeconds(1);

        Assert.Throws<InvalidOperatorInviteStateException>(
            () => invite.Redeem(new OperatorId(Guid.NewGuid()), afterExpiry));
    }

    [Fact]
    public void IsExpired_AtExactlyExpiresAt_IsTrue()
    {
        // >= , not > - `OperatorInvite.IsExpired`'s own contract: the boundary instant itself counts
        // as expired, matching `Site.HasExpired`'s identical `>=` choice for the same reason (an
        // expiry that is only "true a moment later" is a race waiting to happen at the boundary).
        var invite = Generate(TimeSpan.FromDays(7));

        Assert.True(invite.IsExpired(invite.ExpiresAt));
    }

    [Fact]
    public void IsExpired_OneTickBeforeExpiresAt_IsFalse()
    {
        var invite = Generate(TimeSpan.FromDays(7));

        Assert.False(invite.IsExpired(invite.ExpiresAt - TimeSpan.FromTicks(1)));
    }
}
