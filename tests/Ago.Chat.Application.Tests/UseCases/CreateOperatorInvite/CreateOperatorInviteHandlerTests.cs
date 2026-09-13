using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.CreateOperatorInvite;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;

namespace Ago.Chat.Application.Tests.UseCases.CreateOperatorInvite;

public class CreateOperatorInviteHandlerTests
{
    private static readonly SiteId SiteId = new(Guid.NewGuid());
    private static readonly OperatorId OperatorId = new(Guid.NewGuid());
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid OperatorRoleId = Guid.NewGuid();
    private const string InviteeEmail = "colleague@shop.example";

    private sealed record Fixture(
        Application.UseCases.CreateOperatorInvite.CreateOperatorInviteHandler Handler,
        FakeOperatorInviteRepository Invites,
        FakePermissionChecker Permissions,
        FakeRoleRepository Roles,
        FakeOperatorInviteEmailProvisioner EmailProvisioner);

    private static Fixture CreateFixture(
        bool grantPermission = true,
        TimeSpan? validFor = null,
        Ago.Platform.Abstractions.IRateLimiter? rateLimiter = null,
        OperatorInviteProvisionOutcome? provisionOutcome = null)
    {
        var invites = new FakeOperatorInviteRepository();
        var permissions = new FakePermissionChecker();
        if (grantPermission)
        {
            permissions.Grant(OperatorId, SiteId, Permission.SiteManageOperators);
        }

        var roles = new FakeRoleRepository();
        roles.Seed(SiteId, "Operator", OperatorRoleId);

        var emailProvisioner = new FakeOperatorInviteEmailProvisioner(provisionOutcome);

        var handler = new Application.UseCases.CreateOperatorInvite.CreateOperatorInviteHandler(
            invites, roles, permissions, new FakeOperatorInviteCodeGenerator("invite_abc123"),
            emailProvisioner, new FakeSiteRepository(), rateLimiter ?? new FakeRateLimiter(),
            new OperatorInviteOptions { ValidFor = validFor ?? TimeSpan.FromDays(7), ConsoleBaseUrl = "https://console.example.test" },
            new OperatorInviteCreationRateLimitOptions(), new FakeIdGenerator(), new FakeClock(Now));

        return new Fixture(handler, invites, permissions, roles, emailProvisioner);
    }

    private static Application.UseCases.CreateOperatorInvite.CreateOperatorInvite Command(string? roleName = null, string? email = null) =>
        new(OperatorId, SiteId, roleName ?? "Operator", email ?? InviteeEmail);

    [Fact]
    public async Task HandleAsync_WhenPermitted_ReturnsTheGeneratedCode()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("invite_abc123", result.Value.Code);
        Assert.Equal(Now + TimeSpan.FromDays(7), result.Value.ExpiresAt);
        Assert.False(result.Value.SendFailed);
    }

    [Fact]
    public async Task HandleAsync_WhenPermitted_PersistsTheInviteWithTheResolvedRoleIdAndEmail()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        var saved = fixture.Invites.Get(new OperatorInviteId(result.Value.OperatorInviteId));
        Assert.NotNull(saved);
        Assert.Equal(OperatorRoleId, saved.RoleId);
        Assert.Equal(SiteId, saved.SiteId);
        Assert.Equal(OperatorId, saved.CreatedByOperatorId);
        Assert.Equal(InviteeEmail, saved.Email);
        Assert.False(saved.IsRedeemed);
    }

    [Fact]
    public async Task HandleAsync_NeverPersistsThePlaintextCode()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        var saved = fixture.Invites.Get(new OperatorInviteId(result.Value.OperatorInviteId));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes("invite_abc123"));
        Assert.Equal(expectedHash, saved!.CodeHash);
    }

    [Fact]
    public async Task HandleAsync_WhenTheOperatorLacksSiteManageOperators_ReturnsForbidden()
    {
        var fixture = CreateFixture(grantPermission: false);

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("Conversation.Forbidden", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WhenTheRoleNameDoesNotExistOnThisSite_ReturnsInvalidRole()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(roleName: "SuperAdmin"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorInvite.InvalidRole", result.Error!.Value.Code);
    }

    /// <summary>`25-73`'s own Done-when: "creating an invite without an email is refused by the API, not
    /// merely hidden in the console." Fails-before: reverting <c>CreateOperatorInviteHandler</c>'s own
    /// <c>ValidateEmail</c> call (or the required-field shape of <c>CreateOperatorInvite.Email</c>)
    /// makes this fail, because an empty email would reach <c>OperatorInvite.Generate</c> and either
    /// throw an unhandled <see cref="ArgumentException"/> or (before this item existed at all) simply
    /// not exist as a concept to validate.</summary>
    [Fact]
    public async Task HandleAsync_WithNoEmail_ReturnsInvalidEmail()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(email: ""), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorInvite.InvalidEmail", result.Error!.Value.Code);
        Assert.Equal(0, fixture.Invites.Count);
    }

    [Fact]
    public async Task HandleAsync_WithAMalformedEmail_ReturnsInvalidEmail()
    {
        var fixture = CreateFixture();

        var result = await fixture.Handler.HandleAsync(Command(email: "not-an-email"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorInvite.InvalidEmail", result.Error!.Value.Code);
    }

    [Fact]
    public async Task HandleAsync_WithAnInvalidEmail_NeverCallsTheEmailProvisioner()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(Command(email: "not-an-email"), CancellationToken.None);

        Assert.Null(fixture.EmailProvisioner.LastRequest);
    }

    /// <summary>`25-73`'s own Done-when: "a sixth invite from the same site on the same day is refused
    /// with a message naming the limit." Fails-before: with <c>CreateOperatorInviteHandler</c>'s own
    /// `IRateLimiter.CheckAsync` call reverted (or never added), this fake's `RateLimitedFakeRateLimiter`
    /// - which always denies - would never be consulted, and this test would see a plain success
    /// instead.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheSitesDailyInviteBudgetIsExhausted_ReturnsRateLimited()
    {
        var fixture = CreateFixture(rateLimiter: new RateLimitedFakeRateLimiter(TimeSpan.FromHours(6)));

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorInvite.RateLimited", result.Error!.Value.Code);
        Assert.Contains("5", result.Error.Value.Message);
    }

    [Fact]
    public async Task HandleAsync_WhenRateLimited_NeverPersistsAnInvite()
    {
        var fixture = CreateFixture(rateLimiter: new RateLimitedFakeRateLimiter(TimeSpan.FromHours(6)));

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(0, fixture.Invites.Count);
    }

    /// <summary>`25-73`'s own Done-when: rate limiting is keyed per site - `SelectiveFakeRateLimiter`
    /// only denies a key containing this exact site's id, proving the bucket the handler actually
    /// checks is scoped to `command.SiteId`, not some other key that would coincidentally also deny
    /// every call.</summary>
    [Fact]
    public async Task HandleAsync_ChecksARateLimitBucketKeyedToThisSite()
    {
        var fixture = CreateFixture(rateLimiter: new SelectiveFakeRateLimiter($"site:{SiteId.Value}", TimeSpan.FromHours(1)));

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal("OperatorInvite.RateLimited", result.Error!.Value.Code);
    }

    /// <summary>`25-73`'s own point 6: "a send failure is not swallowed" - the invite is still created
    /// (`201`-shaped success from this handler's own point of view), but flagged, and the SMTP error
    /// code the fake provisioner reported is what ends up on the saved row for the console's own
    /// invite-list screen to read later.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheEmailProvisionerReportsASendFailure_StillCreatesTheInviteButFlagsIt()
    {
        var fixture = CreateFixture(provisionOutcome: new OperatorInviteProvisionOutcome.SendFailed("550"));

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.SendFailed);

        var saved = fixture.Invites.Get(new OperatorInviteId(result.Value.OperatorInviteId));
        Assert.NotNull(saved);
        Assert.Equal("550", saved.SendFailureCode);
    }

    [Fact]
    public async Task HandleAsync_PassesTheGeneratedCodeAndEmailToTheProvisioner()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.NotNull(fixture.EmailProvisioner.LastRequest);
        Assert.Equal("invite_abc123", fixture.EmailProvisioner.LastRequest.Code);
        Assert.Equal(InviteeEmail, fixture.EmailProvisioner.LastRequest.Email);
        Assert.Equal(TimeSpan.FromDays(7), fixture.EmailProvisioner.LastRequest.Lifespan);
        Assert.Contains("invite_abc123", fixture.EmailProvisioner.LastRequest.RedirectUri);
    }
}
