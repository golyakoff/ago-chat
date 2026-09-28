using System.Net;
using System.Text;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Keycloak;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `26-260`: the create-or-find branch of <see cref="OperatorInviteEmailProvisioner"/>, driven through a
/// stub <see cref="HttpMessageHandler"/> so the real Admin-API call shapes (token, create, the two
/// lookups, locale sync, execute-actions-email) are exercised without a live Keycloak. The bug this item
/// fixes was observed on the stand: a `POST .../users` returns `409` because the *username* is already
/// taken (this class creates every user with `username = email`), but the pre-existing identity carries no
/// `email` attribute, so the by-email lookup returns `[]` and the provisioner threw - unhandled, a 500 for
/// the whole invite. The fix falls back to a by-username lookup before treating "not found" as a real
/// inconsistency.
/// </summary>
public sealed class OperatorInviteEmailProvisionerTests
{
    private const string Email = "alena.chuprianova@yandex.ru";
    private const string ExistingUserId = "b1c2d3e4-0000-0000-0000-abcdefabcdef";

    private static readonly KeycloakAdminOptions Options = new()
    {
        BaseUrl = "http://keycloak.invalid.example",
        Realm = "ago-chat",
        ClientId = "ago-demo-provisioner",
        ClientSecret = "not-a-real-secret",
        ConsoleClientId = "ago-console",
    };

    private static OperatorInviteProvisionRequest Request() =>
        new(Email, "invite_abc123", TimeSpan.FromDays(7), "http://console.invalid.example/callback?inviteCode=invite_abc123", Locale.En);

    /// <summary>The live bug's exact shape: `409` on create, the by-email lookup finds nothing, the
    /// by-username lookup finds the existing user. The provisioner uses that id, syncs its locale and
    /// sends - no exception, and the send targets the id the username lookup returned. Fails-before: with
    /// only the by-email lookup (the pre-`26-260` code), the empty email result throws and this fails.</summary>
    [Fact]
    public async Task ProvisionAndSendAsync_When409AndEmailLookupEmptyButUsernameFound_UsesThatIdAndSends()
    {
        var handler = new StubKeycloakHandler(emailLookupFindsUser: false, usernameLookupFindsUser: true);
        using var provisioner = CreateProvisioner(handler);

        var outcome = await provisioner.ProvisionAndSendAsync(Request(), CancellationToken.None);

        Assert.IsType<OperatorInviteProvisionOutcome.Sent>(outcome);
        Assert.True(handler.QueriedByUsername, "expected a fallback by-username lookup after the by-email one found nothing");
        Assert.Equal(ExistingUserId, handler.LocaleSyncedUserId);
        Assert.Equal(ExistingUserId, handler.ActionsEmailUserId);
    }

    /// <summary>`409` on create, but neither the by-email nor the by-username lookup finds a user - a
    /// genuine realm-state inconsistency Keycloak just contradicted, so the provisioner throws. This is the
    /// only remaining throw path after the fallback: the handler (`26-260`) catches it and still creates
    /// the invite, but the provisioner itself keeps signalling the contradiction rather than inventing an
    /// id.</summary>
    [Fact]
    public async Task ProvisionAndSendAsync_When409AndBothLookupsEmpty_Throws()
    {
        var handler = new StubKeycloakHandler(emailLookupFindsUser: false, usernameLookupFindsUser: false);
        using var provisioner = CreateProvisioner(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provisioner.ProvisionAndSendAsync(Request(), CancellationToken.None));
    }

    /// <summary>The unchanged common case: `409` on create and the by-email lookup finds the user directly
    /// (a user this same flow created earlier does carry the attribute). The provisioner proceeds exactly
    /// as before and never needs the by-username fallback.</summary>
    [Fact]
    public async Task ProvisionAndSendAsync_When409AndEmailLookupFinds_UsesThatIdWithoutTheUsernameFallback()
    {
        var handler = new StubKeycloakHandler(emailLookupFindsUser: true, usernameLookupFindsUser: true);
        using var provisioner = CreateProvisioner(handler);

        var outcome = await provisioner.ProvisionAndSendAsync(Request(), CancellationToken.None);

        Assert.IsType<OperatorInviteProvisionOutcome.Sent>(outcome);
        Assert.False(handler.QueriedByUsername, "the by-email lookup found the user, so no by-username fallback should run");
        Assert.Equal(ExistingUserId, handler.ActionsEmailUserId);
    }

    private static OperatorInviteEmailProvisioner CreateProvisioner(StubKeycloakHandler handler) =>
        new(new HttpClient(handler), Options, new FixedClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<OperatorInviteEmailProvisioner>.Instance);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }

    /// <summary>Answers just the Admin-API calls this provisioner makes, in order: the client-credentials
    /// token, the `POST .../users` create (always `409` here - the case under test), the by-email and
    /// by-username lookups (each returning a one-element array or `[]` per its flags), the locale-sync
    /// `PUT .../users/{id}`, and the `PUT .../users/{id}/execute-actions-email` send.</summary>
    private sealed class StubKeycloakHandler(bool emailLookupFindsUser, bool usernameLookupFindsUser) : HttpMessageHandler
    {
        public bool QueriedByUsername { get; private set; }

        public string? LocaleSyncedUserId { get; private set; }

        public string? ActionsEmailUserId { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            var query = uri.Query;

            // Token endpoint.
            if (request.Method == HttpMethod.Post && path.EndsWith("/protocol/openid-connect/token", StringComparison.Ordinal))
            {
                return Task.FromResult(Json(HttpStatusCode.OK, "{\"access_token\":\"stub-token\",\"expires_in\":300}"));
            }

            // Create - always a username collision in these tests.
            if (request.Method == HttpMethod.Post && path.EndsWith("/users", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict));
            }

            // The two lookups: GET .../users?email=... and GET .../users?username=...
            if (request.Method == HttpMethod.Get && path.EndsWith("/users", StringComparison.Ordinal))
            {
                if (query.Contains("username=", StringComparison.Ordinal))
                {
                    QueriedByUsername = true;
                    return Task.FromResult(Json(HttpStatusCode.OK, usernameLookupFindsUser ? UserArray : "[]"));
                }

                return Task.FromResult(Json(HttpStatusCode.OK, emailLookupFindsUser ? UserArray : "[]"));
            }

            // Locale sync PUT .../users/{id}
            if (request.Method == HttpMethod.Put && path.EndsWith($"/users/{ExistingUserId}", StringComparison.Ordinal))
            {
                LocaleSyncedUserId = ExistingUserId;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            // Send PUT .../users/{id}/execute-actions-email
            if (request.Method == HttpMethod.Put && path.EndsWith("/execute-actions-email", StringComparison.Ordinal))
            {
                ActionsEmailUserId = ExistingUserId;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private const string UserArray = "[{\"id\":\"" + ExistingUserId + "\"}]";

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
