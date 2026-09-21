namespace Ago.Chat.Domain.Tests;

public class OperatorDeviceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());

    private static OperatorDevice Register(string token = "token-1") =>
        OperatorDevice.Register(
            new OperatorDeviceId(Guid.NewGuid()), SiteId, OperatorId, "installation-1", PushProvider.RuStore, "android", token, Now);

    [Fact]
    public void Register_StartsLive()
    {
        var device = Register();

        Assert.Null(device.RevokedAt);
        Assert.Equal(Now, device.LastSeenAt);
    }

    [Fact]
    public void Register_BlankToken_Throws()
    {
        Assert.Throws<ArgumentException>(() => OperatorDevice.Register(
            new OperatorDeviceId(Guid.NewGuid()), SiteId, OperatorId, "installation-1", PushProvider.RuStore, "android", " ", Now));
    }

    [Fact]
    public void Register_TokenTooLong_Throws()
    {
        var tooLong = new string('a', OperatorDevice.MaxTokenLength + 1);
        Assert.Throws<ArgumentException>(() => OperatorDevice.Register(
            new OperatorDeviceId(Guid.NewGuid()), SiteId, OperatorId, "installation-1", PushProvider.RuStore, "android", tooLong, Now));
    }

    [Fact]
    public void Register_BlankInstallationId_Throws()
    {
        Assert.Throws<ArgumentException>(() => OperatorDevice.Register(
            new OperatorDeviceId(Guid.NewGuid()), SiteId, OperatorId, "  ", PushProvider.RuStore, "android", "token-1", Now));
    }

    [Fact]
    public void Refresh_WritesNewTokenAndTouchesLastSeenAt()
    {
        var device = Register();
        var later = Now.AddDays(1);

        device.Refresh(PushProvider.RuStore, "android", "token-2", later);

        Assert.Equal("token-2", device.Token);
        Assert.Equal(later, device.LastSeenAt);
    }

    /// <summary>`adr/0179` §1's own stated rule: "a revoked device sends nothing until a fresh
    /// registration revives it" - Refresh is what a later PUT on the same installation does, and it
    /// must un-revoke, not merely update the token on a row that stays dead.</summary>
    [Fact]
    public void Refresh_OnARevokedDevice_RevivesIt()
    {
        var device = Register();
        device.Revoke(Now.AddHours(1));
        Assert.NotNull(device.RevokedAt);

        device.Refresh(PushProvider.RuStore, "android", "token-2", Now.AddHours(2));

        Assert.Null(device.RevokedAt);
    }

    [Fact]
    public void Refresh_ClearsFailureTracking()
    {
        var device = Register();
        device.Revoke(Now.AddHours(1));

        device.Refresh(PushProvider.RuStore, "android", "token-2", Now.AddHours(2));

        Assert.Null(device.LastFailureAt);
        Assert.Null(device.FailureReason);
    }

    [Fact]
    public void Revoke_SetsRevokedAt()
    {
        var device = Register();

        device.Revoke(Now.AddHours(1));

        Assert.Equal(Now.AddHours(1), device.RevokedAt);
    }

    /// <summary>`adr/0179`: both mutations are idempotent, deliberately unlike
    /// <see cref="WebhookEndpoint.Revoke"/> - a sign-out DELETE called twice, or a redelivered
    /// `OperatorRemovedFromSite`, must never throw.</summary>
    [Fact]
    public void Revoke_WhenAlreadyRevoked_IsANoOp_AndKeepsTheFirstRevocationInstant()
    {
        var device = Register();
        device.Revoke(Now.AddHours(1));

        device.Revoke(Now.AddHours(2));

        Assert.Equal(Now.AddHours(1), device.RevokedAt);
    }
}
