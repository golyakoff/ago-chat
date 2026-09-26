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

    /// <summary>`26-122`: <see langword="null"/> is a valid device id (a caller that has not been
    /// updated yet) - only a blank-but-non-null value is a mistake worth rejecting.</summary>
    [Fact]
    public void Register_WithNoDeviceId_Succeeds()
    {
        var device = OperatorDevice.Register(
            new OperatorDeviceId(Guid.NewGuid()), SiteId, OperatorId, "installation-1", PushProvider.RuStore, "android",
            "token-1", Now);

        Assert.Null(device.DeviceId);
    }

    [Fact]
    public void Register_WithADeviceId_StoresIt()
    {
        var device = OperatorDevice.Register(
            new OperatorDeviceId(Guid.NewGuid()), SiteId, OperatorId, "installation-1", PushProvider.RuStore, "android",
            "token-1", Now, "device-1");

        Assert.Equal("device-1", device.DeviceId);
    }

    [Fact]
    public void Register_BlankDeviceId_Throws()
    {
        Assert.Throws<ArgumentException>(() => OperatorDevice.Register(
            new OperatorDeviceId(Guid.NewGuid()), SiteId, OperatorId, "installation-1", PushProvider.RuStore, "android",
            "token-1", Now, "  "));
    }

    [Fact]
    public void Register_DeviceIdTooLong_Throws()
    {
        var tooLong = new string('a', OperatorDevice.MaxDeviceIdLength + 1);
        Assert.Throws<ArgumentException>(() => OperatorDevice.Register(
            new OperatorDeviceId(Guid.NewGuid()), SiteId, OperatorId, "installation-1", PushProvider.RuStore, "android",
            "token-1", Now, tooLong));
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

    /// <summary>`26-122`: the whole point of moving identity onto <see cref="OperatorDevice.DeviceId"/> -
    /// a reinstall's fresh `installationId` must land on the row, or a later sign-out DELETE from the
    /// *new* install would address an id this row no longer answers to (this type's own remarks).
    /// </summary>
    [Fact]
    public void Refresh_WithANewInstallationId_UpdatesIt()
    {
        var device = Register();
        var later = Now.AddDays(1);

        device.Refresh(PushProvider.RuStore, "android", "token-2", later, installationId: "installation-2");

        Assert.Equal("installation-2", device.InstallationId);
    }

    /// <summary>The default (no installationId/deviceId passed) leaves both untouched - the shape every
    /// pre-`26-122` call site (and every ordinary token-rotation call) still uses.</summary>
    [Fact]
    public void Refresh_WithNoInstallationIdOrDeviceId_LeavesBothUnchanged()
    {
        var device = OperatorDevice.Register(
            new OperatorDeviceId(Guid.NewGuid()), SiteId, OperatorId, "installation-1", PushProvider.RuStore, "android",
            "token-1", Now, "device-1");

        device.Refresh(PushProvider.RuStore, "android", "token-2", Now.AddDays(1));

        Assert.Equal("installation-1", device.InstallationId);
        Assert.Equal("device-1", device.DeviceId);
    }

    /// <summary>`26-122`: adopting a pre-migration row - `RegisterOperatorDeviceHandler` found it by
    /// `installationId` alone (its own `DeviceId` was <see langword="null"/>) and backfills the device
    /// id the first time an updated client sends one.</summary>
    [Fact]
    public void Refresh_BackfillsANullDeviceId()
    {
        var device = Register();
        Assert.Null(device.DeviceId);

        device.Refresh(PushProvider.RuStore, "android", "token-2", Now.AddDays(1), deviceId: "device-1");

        Assert.Equal("device-1", device.DeviceId);
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

    /// <summary>`26-05`: `NotifyOperatorDevicesHandler`'s own `PushSendOutcome.TransientFailure` path -
    /// a credential- or provider-side failure that says nothing about this device, so unlike
    /// <see cref="Revoke"/> the row stays live.</summary>
    [Fact]
    public void RecordSendFailure_SetsLastFailureAtAndReason_WithoutRevoking()
    {
        var device = Register();
        var failedAt = Now.AddHours(1);

        device.RecordSendFailure("RuStore 401 UNAUTHORIZED", failedAt);

        Assert.Equal(failedAt, device.LastFailureAt);
        Assert.Equal("RuStore 401 UNAUTHORIZED", device.FailureReason);
        Assert.Null(device.RevokedAt);
    }

    /// <summary>Bounded the identical way <see cref="ChannelDelivery.MaxProviderDetailLength"/> is - a
    /// failure reason is a code or a phrase, never an essay, regardless of how long the provider's own
    /// message text happens to be.</summary>
    [Fact]
    public void RecordSendFailure_TruncatesAnOverlongReason()
    {
        var device = Register();
        var tooLong = new string('x', OperatorDevice.MaxFailureReasonLength + 100);

        device.RecordSendFailure(tooLong, Now);

        Assert.Equal(OperatorDevice.MaxFailureReasonLength, device.FailureReason!.Length);
    }

    /// <summary>A later successful send is not this method's job to record - `NotifyOperatorDevicesHandler`
    /// simply does not call it for a <c>Delivered</c> outcome. This pins down that a device already
    /// carrying a stale failure keeps it recorded (a historical fact, not a "currently failing" flag -
    /// this type's own remarks on <see cref="OperatorDevice.RecordSendFailure"/>) unless a fresh
    /// <see cref="OperatorDevice.Refresh"/> clears it, which <see cref="Refresh_ClearsFailureTracking"/>
    /// already proves.</summary>
    [Fact]
    public void RecordSendFailure_CalledTwice_OverwritesWithTheLatestReason()
    {
        var device = Register();
        device.RecordSendFailure("RuStore 500 INTERNAL", Now);

        device.RecordSendFailure("RuStore 429 TOO_MANY_REQUESTS", Now.AddMinutes(5));

        Assert.Equal(Now.AddMinutes(5), device.LastFailureAt);
        Assert.Equal("RuStore 429 TOO_MANY_REQUESTS", device.FailureReason);
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
