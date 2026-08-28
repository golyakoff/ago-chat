using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.RegisterSite;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;

namespace Ago.Chat.Application.Tests.UseCases.RegisterSite;

public class RegisterSiteHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private const string ExternalSubjectId = "keycloak-sub-1";
    private const string RequestIp = "203.0.113.5";

    private sealed record Fixture(RegisterSiteHandler Handler, FakeSiteRegistrationRepository Registrations);

    private static Fixture CreateFixture(IRateLimiter? rateLimiter = null)
    {
        var registrations = new FakeSiteRegistrationRepository();
        var handler = new RegisterSiteHandler(
            registrations,
            rateLimiter ?? new FakeRateLimiter(),
            new RegisterSiteRateLimitOptions(),
            new FakeIdGenerator(),
            new FakeClock(Now));

        return new Fixture(handler, registrations);
    }

    private static Ago.Chat.Application.UseCases.RegisterSite.RegisterSite ValidCommand() =>
        new(ExternalSubjectId, RequestIp, "Acme Support", "https://shop.example.com");

    [Fact]
    public async Task HandleAsync_WhenTheChecksPass_CreatesTheSiteBothRolesAndTheOperator_AssignedBothRoles()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(ValidCommand(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Single(fixture.Registrations.Registered);
        var registration = fixture.Registrations.Registered[0];

        Assert.Equal(result.Value.SiteId, registration.Site.Id.Value);
        Assert.Equal("Acme Support", registration.Site.Name);
        Assert.Contains("https://shop.example.com", registration.Site.AllowedOrigins);

        Assert.Equal(result.Value.OperatorId, registration.Operator.Id.Value);
        Assert.Equal(ExternalSubjectId, registration.Operator.ExternalSubjectId);
        Assert.Equal(OperatorStatus.Offline, registration.Operator.Status);

        Assert.Equal("Operator", registration.OperatorRole.Name);
        Assert.Equal(
            [Permission.ConversationRead.Value, Permission.ConversationSend.Value, Permission.ConversationAssign.Value],
            registration.OperatorRole.Permissions);

        Assert.Equal("Admin", registration.AdminRole.Name);
        Assert.Equal(
            [
                Permission.SiteConfigure.Value, Permission.SiteManageOperators.Value, Permission.AttachmentDelete.Value,
                Permission.SiteErase.Value, Permission.ConversationErase.Value, Permission.SiteExport.Value,
            ],
            registration.AdminRole.Permissions);
    }

    /// <summary>
    /// `13-07`/`adr/0068`: the direct replacement for what used to be
    /// "...AlreadyHasAnOperatorRow_ReturnsAlreadyRegistered" - before this item, a `sub` that already
    /// resolved to *any* `operators` row was refused `409` by a pre-check this handler ran. That
    /// pre-check is gone; the whole point of `13-07` is that this now succeeds, exactly like a
    /// first-time caller, and produces a genuinely second `SiteRegistration`. Fails before this item's
    /// change (the old code returned `Site.AlreadyRegistered` here) and passes after - the regression
    /// proof this item's own "zero behavioural change for a single-tenant identity" claim does not
    /// cover, because this identity is deliberately not single-tenant.
    /// </summary>
    [Fact]
    public async Task HandleAsync_WhenTheSubjectAlreadyHasAnOperatorRowOnADifferentSite_RegistersASecondSiteAnyway()
    {
        var fixture = CreateFixture();
        var firstResult = await fixture.Handler.HandleAsync(ValidCommand(), CancellationToken.None);
        Assert.True(firstResult.IsSuccess);

        var secondResult = await fixture.Handler.HandleAsync(
            ValidCommand() with { SiteName = "Acme Support - Second Shop", InitialAllowedOrigin = "https://second.example.com" },
            CancellationToken.None);

        Assert.True(secondResult.IsSuccess);
        Assert.NotEqual(firstResult.Value.SiteId, secondResult.Value.SiteId);
        Assert.NotEqual(firstResult.Value.OperatorId, secondResult.Value.OperatorId);
        Assert.Equal(2, fixture.Registrations.Registered.Count);
        Assert.All(fixture.Registrations.Registered, r => Assert.Equal(ExternalSubjectId, r.Operator.ExternalSubjectId));
    }

    [Fact]
    public async Task HandleAsync_WhenTheRepositoryLosesTheUniqueIndexRace_ReturnsAlreadyRegistered()
    {
        // The pre-check above passed (no seeded operator), but a concurrent registration for the
        // same identity won the actual insert - ISiteRegistrationRepository.TryRegisterAsync's own
        // remarks on why the database's unique index, not the pre-check, is the real correctness
        // guarantee.
        var fixture = CreateFixture();
        fixture.Registrations.DenyNextRegistration = true;

        var result = await fixture.Handler.HandleAsync(ValidCommand(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.AlreadyRegistered", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenThePerSubjectRateLimitIsExceeded_ReturnsRateLimited_WithoutRegistering()
    {
        var fixture = CreateFixture(new RateLimitedFakeRateLimiter(TimeSpan.FromSeconds(30)));

        var result = await fixture.Handler.HandleAsync(ValidCommand(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.RateLimited", result.Error!.Value.Code);
        Assert.Empty(fixture.Registrations.Registered);
    }

    [Fact]
    public async Task HandleAsync_WhenOnlyThePerIpRateLimitIsExceeded_ReturnsRateLimited_WithoutRegistering()
    {
        var fixture = CreateFixture(new SelectiveFakeRateLimiter("ip", TimeSpan.FromSeconds(30)));

        var result = await fixture.Handler.HandleAsync(ValidCommand(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.RateLimited", result.Error!.Value.Code);
        Assert.Empty(fixture.Registrations.Registered);
    }

    [Fact]
    public async Task HandleAsync_WhenTheSiteNameIsBlank_ReturnsInvalidName()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            ValidCommand() with { SiteName = "   " }, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.InvalidName", result.Error!.Value.Code);
        Assert.Empty(fixture.Registrations.Registered);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://shop.example.com")]
    [InlineData("https://shop.example.com/path")]
    [InlineData("https://shop.example.com/")]
    public async Task HandleAsync_WhenTheInitialOriginIsMalformed_ReturnsInvalidOrigin(string origin)
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(
            ValidCommand() with { InitialAllowedOrigin = origin }, CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.InvalidOrigin", result.Error!.Value.Code);
        Assert.Empty(fixture.Registrations.Registered);
    }
}
