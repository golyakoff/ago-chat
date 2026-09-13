namespace Ago.Chat.Domain.Tests;

public class OperatorInviteTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId CreatedBy = new(Guid.NewGuid());
    private static readonly Guid RoleId = Guid.NewGuid();

    private static OperatorInvite Generate(TimeSpan? validFor = null, string email = "invitee@example.com") =>
        OperatorInvite.Generate(
            new OperatorInviteId(Guid.NewGuid()), SiteId, RoleId, [1, 2, 3], email, CreatedBy, Now,
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

    /// <summary>`25-73`: email became a required field on this aggregate - `CreateOperatorInviteHandler`
    /// validates the shape before ever calling this factory, but the factory itself still refuses an
    /// empty value as a last line of defence, the same "thrown, a caller bug not a business refusal"
    /// shape <see cref="Site"/>'s own constructor already uses for its required `PublicKey`.</summary>
    [Fact]
    public void Generate_WithNoEmail_Throws()
    {
        Assert.Throws<ArgumentException>(() => Generate(email: ""));
    }

    [Fact]
    public void Generate_SetsEmail()
    {
        var invite = Generate(email: "colleague@shop.example");

        Assert.Equal("colleague@shop.example", invite.Email);
    }

    [Fact]
    public void Revoke_MarksRevoked()
    {
        var invite = Generate();
        var revokedAt = Now + TimeSpan.FromMinutes(5);

        invite.Revoke(revokedAt);

        Assert.True(invite.IsRevoked);
        Assert.Equal(revokedAt, invite.RevokedAt);
    }

    [Fact]
    public void Revoke_WhenAlreadyRedeemed_Throws()
    {
        var invite = Generate();
        invite.Redeem(new OperatorId(Guid.NewGuid()), Now + TimeSpan.FromMinutes(5));

        Assert.Throws<InvalidOperatorInviteStateException>(() => invite.Revoke(Now + TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void Revoke_WhenAlreadyRevoked_Throws()
    {
        var invite = Generate();
        invite.Revoke(Now + TimeSpan.FromMinutes(5));

        Assert.Throws<InvalidOperatorInviteStateException>(() => invite.Revoke(Now + TimeSpan.FromMinutes(10)));
    }

    /// <summary>`25-73`'s own security boundary, at the domain layer: a revoked invite must never be
    /// redeemable, whatever else about it still looks valid - `OperatorInviteRedemptionRepository`'s
    /// own pre-lock check is the primary enforcement, this is the last-line-of-defence backstop
    /// <see cref="Redeem"/>'s own remarks already describe for `IsRedeemed`/`IsExpired`.</summary>
    [Fact]
    public void Redeem_WhenRevoked_Throws()
    {
        var invite = Generate();
        invite.Revoke(Now + TimeSpan.FromMinutes(1));

        Assert.Throws<InvalidOperatorInviteStateException>(
            () => invite.Redeem(new OperatorId(Guid.NewGuid()), Now + TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void MarkSendFailed_RecordsTheSmtpErrorCode()
    {
        var invite = Generate();

        invite.MarkSendFailed("550");

        Assert.Equal("550", invite.SendFailureCode);
    }
}
