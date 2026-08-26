using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.MintDemoTenant;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Infrastructure.Keycloak;

/// <summary>
/// `8-07`/`adr/0058`: the only thing in this codebase that writes to Keycloak. Implements
/// <see cref="IDemoIdentityProvisioner"/> against the realm admin API using a service-account client
/// scoped to `manage-users`.
///
/// <para><b>The access token is cached across calls</b> because it is valid for minutes and a fresh
/// `client_credentials` exchange per minted tenant would triple this endpoint's outbound traffic for
/// nothing. Guarded by a <see cref="SemaphoreSlim"/> rather than a lock, because acquiring it is an
/// async call: two viewers clicking at once must not each start their own exchange, and one of them
/// must not block a thread while the other finishes.</para>
///
/// <para><b>What it deliberately does not do.</b> No retry loop and no circuit breaker of its own -
/// those are applied by wrapping this type's <see cref="HttpClient"/> at registration, the same
/// "resilience hidden behind the port" shape `RedisCache`, `S3FileStorage` and
/// `HttpWebhookDeliveryClient` already use. And no user *lookup*: the caller chooses the subject id,
/// so there is never a "find the user first" round trip to get wrong.</para>
/// </summary>
public sealed class KeycloakDemoIdentityProvisioner(
    HttpClient http,
    KeycloakAdminOptions options,
    IClock clock,
    ILogger<KeycloakDemoIdentityProvisioner> logger) : IDemoIdentityProvisioner, IDisposable
{
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;

    private string BaseUrl => options.BaseUrl.TrimEnd('/');

    public async Task<Result<string>> CreateAsync(
        string username, string password, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{BaseUrl}/admin/realms/{options.Realm}/users")
        {
            // No `id` in the body, because supplying one does not work and does not say so: Keycloak
            // answers 201 and assigns a different id anyway (IDemoIdentityProvisioner's own remarks).
            // The id below is whatever it chose, read from the Location header.
            Content = JsonContent.Create(new
            {
                username,
                enabled = true,
                // A username alone is not enough: Keycloak's declarative user profile (on by default
                // since 24) leaves such an account with pending required actions, and the password
                // grant then answers `invalid_grant: Account is not fully set up` - which is what the
                // first version of this did, found by `DemoTenantLifecycleTests` rather than in
                // production. `requiredActions: []` states there are none, and the three profile fields
                // below are what stops Keycloak adding some.
                requiredActions = Array.Empty<string>(),
                firstName = "Demo",
                lastName = "Operator",
                // `.invalid` is reserved by RFC 2606 and can never resolve, so this address exists only
                // to satisfy the user profile and cannot receive anything. That keeps `8-07`'s "email of
                // any kind is out of scope" true in substance: nothing is sent, nothing is verified, and
                // there is no address a person could be reached at.
                email = $"{username}@demo.invalid",
                // Marked verified because there is nothing to verify and an unverified address would
                // block the login this whole item exists to enable. Stated plainly here because it is
                // the one place this code asserts something it did not check (`adr/0058`).
                emailVerified = true,
                credentials = new[] { new { type = "password", value = password, temporary = false } },
            }),
        };
        request.Headers.Authorization = new("Bearer", token);

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            // Keycloak answers 201 with the new user's URL in `Location` and an empty body; the last
            // path segment is the subject id. A 201 without one would mean Keycloak changed its
            // contract, which is worth failing loudly rather than silently minting an operator whose
            // identity nothing can resolve.
            var location = response.Headers.Location?.ToString();
            var subjectId = location?[(location.LastIndexOf('/') + 1)..];
            if (string.IsNullOrWhiteSpace(subjectId))
            {
                return DemoTenantErrors.IdentityRejected("created without a Location header");
            }

            return subjectId;
        }

        // A 409 means the username or the id is taken. Reported as an expected failure rather than
        // thrown: the caller can act on it, and it says nothing about Keycloak's health, so it must not
        // reach a circuit breaker (`IInboundChannelAdapter`'s contract draws the same line).
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogWarning(
                "Keycloak refused a demo user: {Status} {Body}", (int)response.StatusCode, Truncate(body));
            return DemoTenantErrors.IdentityRejected($"{(int)response.StatusCode}");
        }

        // Everything else - 5xx, gateway errors - is an infrastructure fault. Thrown, so the resilience
        // pipeline wrapping this client is what decides whether to retry.
        response.EnsureSuccessStatusCode();
        throw new InvalidOperationException(
            "Unreachable: EnsureSuccessStatusCode did not throw for a non-success response.");
    }

    public async Task DeleteAsync(string subjectId, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);

        using var request = new HttpRequestMessage(
            HttpMethod.Delete, $"{BaseUrl}/admin/realms/{options.Realm}/users/{Uri.EscapeDataString(subjectId)}");
        request.Headers.Authorization = new("Bearer", token);

        using var response = await http.SendAsync(request, cancellationToken);

        // 404 is success, per this port's contract. A user somebody removed by hand must not be able to
        // wedge the expiry sweeper forever - that would leave exactly the rows `8-07` exists to remove.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            logger.LogInformation(
                "Demo identity {SubjectId} was already absent from Keycloak; treating as deleted.", subjectId);
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is { } cached && clock.UtcNow < _accessTokenExpiresAt)
        {
            return cached;
        }

        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            // Re-checked inside the gate: the caller that waited here may have been waiting for exactly
            // the exchange that has now populated it.
            if (_accessToken is { } stillCached && clock.UtcNow < _accessTokenExpiresAt)
            {
                return stillCached;
            }

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret,
            });

            using var response = await http.PostAsync(
                $"{BaseUrl}/realms/{options.Realm}/protocol/openid-connect/token", form, cancellationToken);
            response.EnsureSuccessStatusCode();

            using var payload = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken));
            var token = payload.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("Keycloak returned a token response with no access_token.");
            var expiresIn = payload.RootElement.TryGetProperty("expires_in", out var seconds)
                ? TimeSpan.FromSeconds(seconds.GetInt32())
                : TimeSpan.FromMinutes(1);

            _accessToken = token;
            _accessTokenExpiresAt = clock.UtcNow + expiresIn - options.TokenRefreshSkew;
            return token;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    // Keycloak's error bodies are short, but a body is attacker-influenced (the username came from us,
    // the error text does not) and a log line is not a place to paste an unbounded string.
    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500];

    public void Dispose() => _tokenGate.Dispose();
}
