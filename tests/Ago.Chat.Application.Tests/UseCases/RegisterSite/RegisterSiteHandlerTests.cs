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

    private sealed record Fixture(
        RegisterSiteHandler Handler, FakeOperatorRepository Operators, FakeSiteRegistrationRepository Registrations);

    private static Fixture CreateFixture(IRateLimiter? rateLimiter = null)
    {
        var operators = new FakeOperatorRepository();
        var registrations = new FakeSiteRegistrationRepository();
        var handler = new RegisterSiteHandler(
            operators,
            registrations,
            rateLimiter ?? new FakeRateLimiter(),
            new RegisterSiteRateLimitOptions(),
            new FakeIdGenerator(),
            new FakeClock(Now));

        return new Fixture(handler, operators, registrations);
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
            [Permission.SiteConfigure.Value, Permission.SiteManageOperators.Value, Permission.AttachmentDelete.Value],
            registration.AdminRole.Permissions);
    }

    [Fact]
    public async Task HandleAsync_WhenTheSubjectAlreadyHasAnOperatorRow_ReturnsAlreadyRegistered_WithoutRegistering()
    {
        var fixture = CreateFixture();
        fixture.Operators.Seed(new Operator(
            new OperatorId(Guid.NewGuid()), new SiteId(Guid.NewGuid()), OperatorStatus.Online, 5, ExternalSubjectId));

        var result = await fixture.Handler.HandleAsync(ValidCommand(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Site.AlreadyRegistered", result.Error!.Value.Code);
        Assert.Empty(fixture.Registrations.Registered);
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
