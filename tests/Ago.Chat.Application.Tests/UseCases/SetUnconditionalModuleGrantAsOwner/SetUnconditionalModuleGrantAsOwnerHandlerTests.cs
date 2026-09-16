using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.SetUnconditionalModuleGrantAsOwner;
using Ago.Chat.Domain;

namespace Ago.Chat.Application.Tests.UseCases.SetUnconditionalModuleGrantAsOwner;

/// <summary>`23-86`: the platform owner's own write for the unconditional-grant flag - every port
/// faked, the identical shape <c>GrantModuleQuantityAsOwnerHandlerTests</c> already establishes for its
/// sibling. What a real Postgres transaction (and the OR read against a real
/// <c>SubscriptionRenewalApplier</c> lapse) does is <c>Ago.Chat.Integration.Tests</c>' job.</summary>
public class SetUnconditionalModuleGrantAsOwnerHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly ModuleKey ChannelModuleKey = new("channel");

    private sealed record Fixture(
        Application.UseCases.SetUnconditionalModuleGrantAsOwner.SetUnconditionalModuleGrantAsOwnerHandler Handler,
        FakeModuleQuantityGrantStore Grants);

    private static Fixture CreateFixture()
    {
        var grants = new FakeModuleQuantityGrantStore();
        return new Fixture(
            new Application.UseCases.SetUnconditionalModuleGrantAsOwner.SetUnconditionalModuleGrantAsOwnerHandler(grants, new FakeClock(Now)),
            grants);
    }

    private static Application.UseCases.SetUnconditionalModuleGrantAsOwner.SetUnconditionalModuleGrantAsOwner Command(
        string moduleKey = "channel", bool unconditionallyGranted = true, string setBy = "owner-sub-123",
        string reason = "sales trial", DateTimeOffset? expiresAt = null) =>
        new(SiteId, moduleKey, unconditionallyGranted, setBy, reason, expiresAt);

    [Fact]
    public async Task HandleAsync_SettingTheFlag_GrantsEvenWithNoBillingQuantityYet()
    {
        // `23-86` case 1: the owner grants a trial by hand, before any payment exists at all.
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(unconditionallyGranted: true), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, await fixture.Grants.GetQuantityAsync(SiteId, ChannelModuleKey, CancellationToken.None));
        Assert.True(fixture.Grants.UnconditionalGrants[(SiteId, ChannelModuleKey)]);
    }

    [Fact]
    public async Task HandleAsync_LiftingTheFlag_WithNoBillingQuantity_TurnsTheEntitlementOff()
    {
        var fixture = CreateFixture();
        await fixture.Handler.HandleAsync(Command(unconditionallyGranted: true), CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            Command(unconditionallyGranted: false, reason: "trial ended, no payment followed"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, await fixture.Grants.GetQuantityAsync(SiteId, ChannelModuleKey, CancellationToken.None));
    }

    [Fact]
    public async Task HandleAsync_WithNoReasonAtAll_Refuses()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(reason: ""), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.QuantityUnconditionalGrantReasonRequired", result.Error!.Value.Code);
        Assert.Empty(fixture.Grants.UnconditionalGrants);
    }

    [Fact]
    public async Task HandleAsync_WithAWhitespaceOnlyReason_Refuses()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(reason: "   "), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.QuantityUnconditionalGrantReasonRequired", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_LiftingTheFlag_WithNoReason_IsRefusedTheSameAsSettingIt()
    {
        // Both directions carry the identical requirement - lifting the flag is exactly as
        // consequential as setting it (this item's own text).
        var fixture = CreateFixture();
        await fixture.Handler.HandleAsync(Command(unconditionallyGranted: true), CancellationToken.None);

        var result = await fixture.Handler.HandleAsync(
            Command(unconditionallyGranted: false, reason: ""), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.QuantityUnconditionalGrantReasonRequired", result.Error!.Value.Code);
        // The earlier grant is untouched by the refused call.
        Assert.True(fixture.Grants.UnconditionalGrants[(SiteId, ChannelModuleKey)]);
    }

    [Fact]
    public async Task HandleAsync_WithAReasonOverTheBound_Refuses()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            Command(reason: new string('a', Application.UseCases.SetUnconditionalModuleGrantAsOwner.SetUnconditionalModuleGrantAsOwnerHandler.MaxReasonLength + 1)),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.QuantityUnconditionalGrantReasonRequired", result.Error!.Value.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Channel")]
    [InlineData("has spaces")]
    public async Task HandleAsync_WithAnInvalidModuleKey_IsRejected(string moduleKey)
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(moduleKey: moduleKey), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Module.Invalid", result.Error!.Value.Code);
        Assert.Empty(fixture.Grants.UnconditionalGrants);
    }

    // `25-115`: ExpiresAt - the channel-entitlement table's first real caller of this command with an
    // expiry to send, threaded straight through to the store rather than a second write use case.

    [Fact]
    public async Task HandleAsync_SettingTheFlag_WithAFutureExpiry_ThreadsItToTheStore()
    {
        var fixture = CreateFixture();
        var expiresAt = Now.AddDays(30);

        var result = await fixture.Handler.HandleAsync(
            Command(unconditionallyGranted: true, expiresAt: expiresAt), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(expiresAt, fixture.Grants.UnconditionalGrantExpiresAt[(SiteId, ChannelModuleKey)]);
    }

    [Fact]
    public async Task HandleAsync_SettingTheFlag_WithNoExpiry_StoresNull_Indefinite()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(unconditionallyGranted: true), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Null(fixture.Grants.UnconditionalGrantExpiresAt[(SiteId, ChannelModuleKey)]);
    }

    /// <summary>The item's own headline scope change, proven at the handler level: a past expiry means
    /// <see cref="FakeModuleQuantityGrantStore.GetQuantityAsync"/> - which honours expiry the identical
    /// way the real store's <c>ModuleQuantityGrant.EffectiveQuantity(now)</c> does - reads this grant as
    /// not entitled.</summary>
    [Fact]
    public async Task HandleAsync_SettingTheFlag_WithAPastExpiry_ReadsAsNotEntitled()
    {
        var fixture = CreateFixture();
        var expiresAt = Now.AddDays(-1);

        var result = await fixture.Handler.HandleAsync(
            Command(unconditionallyGranted: true, expiresAt: expiresAt), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(0, await fixture.Grants.GetQuantityAsync(SiteId, ChannelModuleKey, CancellationToken.None));
        // The flag itself is still recorded as set - only the effective read has fallen back.
        Assert.True(fixture.Grants.UnconditionalGrants[(SiteId, ChannelModuleKey)]);
    }
}
