using System.Security.Cryptography;
using System.Text;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Tests.Fakes;
using Ago.Chat.Application.UseCases.CreateOperatorInvite;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging.Abstractions;

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
        FakeOperatorInviteEmailProvisioner EmailProvisioner,
        FakeNotificationMailSender MailSender);

    private static Fixture CreateFixture(
        bool grantPermission = true,
        TimeSpan? validFor = null,
        Ago.Platform.Abstractions.IRateLimiter? rateLimiter = null,
        OperatorInviteProvisionOutcome? provisionOutcome = null,
        Exception? fallbackMailThrows = null)
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
        var mailSender = new FakeNotificationMailSender(fallbackMailThrows);

        var handler = new Application.UseCases.CreateOperatorInvite.CreateOperatorInviteHandler(
            invites, roles, permissions, new FakeOperatorInviteCodeGenerator("invite_abc123"),
            emailProvisioner, mailSender, new FakeSiteRepository(), rateLimiter ?? new FakeRateLimiter(),
            new OperatorInviteOptions { ValidFor = validFor ?? TimeSpan.FromDays(7), ConsoleBaseUrl = "https://console.example.test" },
            new OperatorInviteCreationRateLimitOptions(), new FakeIdGenerator(), new FakeClock(Now),
            NullLogger<Application.UseCases.CreateOperatorInvite.CreateOperatorInviteHandler>.Instance);

        return new Fixture(handler, invites, permissions, roles, emailProvisioner, mailSender);
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

    /// <summary>`25-90`'s own Done-when: "a second `NotificationMailSender` email fires from
    /// `CreateOperatorInviteHandler`, carrying the invite code as plain, copyable text with accurate
    /// instructions for where it goes." This is the second, independent channel investigated (and
    /// deliberately not built) in `25-85`, and settled by the author's own 2026-09-18 decision in
    /// `docs/backlog/25-90-*.md`'s Scope: the plaintext code must never sit in Keycloak's own storage, so
    /// it travels through this codebase's own <see cref="INotificationMailSender"/> instead, to the
    /// invitee's own address - never the inviting admin's.</summary>
    [Fact]
    public async Task HandleAsync_SendsASecondFallbackEmailToTheInviteeCarryingTheCodeAsPlainText()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        var sent = Assert.Single(fixture.MailSender.Sent);
        Assert.Equal(InviteeEmail, sent.To);
        Assert.Contains("invite_abc123", sent.Subject + sent.Body);
    }

    /// <summary>`25-90`'s own Done-when: "Both `en` and `ru` render correctly." This port's only two
    /// existing callers (`InactivityWatchdogJob`/`DownloadThresholdWatchdogJob`, both `ago-chat`) render
    /// one bilingual message rather than selecting a single language per recipient -
    /// `OperatorInviteCodeMailTemplate`'s own doc comment has the full reasoning for following that exact
    /// convention here rather than inventing a per-`Locale` switch for this one new caller. Both language
    /// blocks are asserted present in the same send, in the same code's own real value, not a template
    /// constant read in isolation - this is `25-90`'s own explicit bar ("proven against a real send, not
    /// asserted from a template file"), read as "the real handler path", the level this test operates at
    /// without a live SMTP relay.</summary>
    [Fact]
    public async Task HandleAsync_TheFallbackEmailRendersBothEnglishAndRussianWithTheRealCode()
    {
        var fixture = CreateFixture();

        await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        var sent = Assert.Single(fixture.MailSender.Sent);
        Assert.Contains("Your AGO Chat backup invite code", sent.Subject);
        Assert.Contains("Резервный код приглашения AGO Chat", sent.Subject);
        Assert.Contains("invite_abc123", sent.Body);
        Assert.Contains("Invite code", sent.Body);
        Assert.Contains("Код приглашения", sent.Body);
    }

    /// <summary>`25-90`'s own Scope: "The two emails... are independent; neither should depend on the
    /// other succeeding." A Keycloak-side SMTP failure (recorded on the invite itself, per `25-73`'s own
    /// point 6) must not suppress this codebase's own second channel - the whole reason a second channel
    /// exists at all is to reach the invitee even when the first one did not.</summary>
    [Fact]
    public async Task HandleAsync_WhenKeycloaksOwnEmailFailsToSend_StillSendsTheFallbackInviteCodeEmail()
    {
        var fixture = CreateFixture(provisionOutcome: new OperatorInviteProvisionOutcome.SendFailed("550"));

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.SendFailed);
        var sent = Assert.Single(fixture.MailSender.Sent);
        Assert.Equal(InviteeEmail, sent.To);
    }

    /// <summary>`25-90`'s own Scope, the other half of "neither should depend on the other succeeding":
    /// a fault in this codebase's own second channel (the same transient/connection-stage throw
    /// `NotificationMailSender`'s own doc comment says it never swallows) must not fail invite creation -
    /// the Keycloak email is the load-bearing one, this is a redundant, best-effort channel, and
    /// `CreateOperatorInviteHandler.SendInviteCodeFallbackEmailAsync`'s own catch is this handler's fault
    /// boundary for it, the identical "one candidate's mail failure is logged and does not stop the rest"
    /// posture `InactivityWatchdogJob`/`DownloadThresholdWatchdogJob` already hold for this exact
    /// port.</summary>
    [Fact]
    public async Task HandleAsync_WhenTheFallbackMailSenderThrows_StillCreatesTheInviteSuccessfully()
    {
        var fixture = CreateFixture(fallbackMailThrows: new InvalidOperationException("relay unreachable"));

        var result = await fixture.Handler.HandleAsync(Command(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.SendFailed);
        Assert.Equal(1, fixture.Invites.Count);
    }
}
