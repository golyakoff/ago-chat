using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Domain;
using Ago.Platform.Kernel;
using Microsoft.Extensions.Logging;

namespace Ago.Chat.Infrastructure.Keycloak;

/// <summary>
/// `25-73`: the second thing this codebase ever writes to Keycloak, and deliberately its own class -
/// see <c>IOperatorInviteEmailProvisioner</c>'s own remarks for why this does not reuse or extend
/// <see cref="KeycloakDemoIdentityProvisioner"/>, whose doc comment this one otherwise follows closely:
/// the token-cache/semaphore shape, the 409-as-expected-Result convention (widened here into a real
/// lookup, since this item's design actually needs one), and "no retry loop/circuit breaker inside the
/// type itself" - applied by wrapping this type's own <see cref="HttpClient"/> at registration, the
/// same seam <c>ServiceCollectionExtensions.AddKeycloakDemoIdentities</c> already establishes for its
/// sibling.
///
/// <para><b>Up to five Admin API calls per invite, not one.</b> (1) `POST .../users` - create; on `201`,
/// done with this step and only the send below follows. (2) On `409` only, find the existing identity -
/// `GET .../users?email=...&amp;exact=true` first and, `26-260`, `GET .../users?username=...&amp;exact=true`
/// as a fallback when the email search finds nothing (the 409 is a *username* collision, and a pre-existing
/// user can hold the username with no `email` attribute). (3) On that same `409` path, sync the existing
/// user - `GET .../users/{id}` to read its current representation, then `PUT .../users/{id}` with that whole
/// representation round-tripped: `email` set and the `locale` attribute merged in. `26-262`: this is a
/// full-representation PUT on purpose, because Keycloak's user PUT *replaces* the representation - a partial
/// `{ attributes: { locale } }` body silently wiped the `email` and broke the send below. (4)
/// `PUT .../users/{id}/execute-actions-email` either way - the actual send.</para>
///
/// <para><b>What was not verified against a live realm, stated plainly rather than assumed.</b>
/// (a) Whether this realm's <see cref="KeycloakAdminOptions.Realm"/> has email internationalization
/// turned on for more than the default locale's template - if not, the <c>locale</c> attribute this
/// class sets is simply ignored by Keycloak (a safe failure mode: the email still sends, in the realm's
/// default language) rather than causing an error. (b) Whether <see cref="KeycloakAdminOptions.ConsoleClientId"/>'s
/// default actually names the console's real registered client id, and whether that client's own valid-
/// redirect-uri patterns permit the extra `?inviteCode=...` query parameter this item's design appends
/// to it. (c) Whether Keycloak's Admin REST API response to a failed `execute-actions-email` call ever
/// actually carries the underlying SMTP relay's own error code, as opposed to a generic
/// `500`/"internal_server_error" with no further detail - <see cref="SmtpFailureDetail"/> captures the
/// best information this call's own HTTP response can offer either way, but could not be checked
/// against a real SMTP failure without a live deployment.</para>
/// </summary>
public sealed class OperatorInviteEmailProvisioner(
    HttpClient http,
    KeycloakAdminOptions options,
    IClock clock,
    ILogger<OperatorInviteEmailProvisioner> logger) : IOperatorInviteEmailProvisioner, IDisposable
{
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;

    private string BaseUrl => options.BaseUrl.TrimEnd('/');

    public async Task<OperatorInviteProvisionOutcome> ProvisionAndSendAsync(
        OperatorInviteProvisionRequest request, CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        var userId = await CreateOrFindUserAsync(request.Email, request.Locale, token, cancellationToken);
        return await SendActionsEmailAsync(userId, request, token, cancellationToken);
    }

    private async Task<string> CreateOrFindUserAsync(string email, Locale locale, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/admin/realms/{options.Realm}/users")
        {
            Content = JsonContent.Create(new
            {
                // Email as username - the only identifier known about a real invitee at creation time,
                // and the same choice this realm's own self-registration form already makes (`10-03`'s
                // Keycloak-hosted registration).
                username = email,
                email,
                enabled = true,
                // Not yet verified - unlike KeycloakDemoIdentityProvisioner's own `.invalid` address
                // (marked verified because there is nothing real to verify), this is a real address
                // nobody has confirmed receipt at yet.
                emailVerified = false,
                // The declarative-user-profile trap KeycloakDemoIdentityProvisioner's own doc comment
                // names, solved the opposite way here: that class supplies firstName/lastName so no
                // required action is left pending; this item's own invitee has no name to give at
                // creation time, so UPDATE_PROFILE is what collects one, and UPDATE_PASSWORD is what
                // lets them set a credential nothing here ever generates for them.
                requiredActions = new[] { "UPDATE_PASSWORD", "UPDATE_PROFILE" },
                attributes = new Dictionary<string, string[]> { ["locale"] = [KeycloakLocaleCode(locale)] },
            }),
        };
        request.Headers.Authorization = new("Bearer", token);

        using var response = await http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            var location = response.Headers.Location?.ToString();
            var subjectId = location?[(location.LastIndexOf('/') + 1)..];
            if (string.IsNullOrWhiteSpace(subjectId))
            {
                throw new InvalidOperationException(
                    "Keycloak created an operator-invite user without a Location header in the response.");
            }

            return subjectId;
        }

        // `25-73`'s own design point 2: a real, expected case, not an error path to refuse - the email
        // already has a Keycloak identity anywhere on the realm. Looked up, not thrown, unlike
        // KeycloakDemoIdentityProvisioner's own 409 (a name collision that class simply reports back as
        // an expected failure, because nothing there needs the colliding user's own id).
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            return await FindExistingUserIdAndSyncLocaleAsync(email, locale, token, cancellationToken);
        }

        response.EnsureSuccessStatusCode();
        throw new InvalidOperationException("Unreachable: EnsureSuccessStatusCode did not throw for a non-success response.");
    }

    private async Task<string> FindExistingUserIdAndSyncLocaleAsync(
        string email, Locale locale, string token, CancellationToken cancellationToken)
    {
        // `26-260`: the 409 the POST above returned is a *username* collision (this class creates every
        // user with `username = email`), which is not the same fact as "a user carries this email
        // attribute". A pre-existing identity created some other way - self-registration under an older
        // profile, an import, a demo mint - can hold the username without ever having had an `email`
        // attribute set, in which case the exact-email search below returns `[]` even though the user
        // genuinely exists. So this looks the user up two ways and only treats "not found by either" as
        // the real inconsistency: first by email (the common case - a user this same flow created earlier
        // does carry it), then, if that finds nothing, by the username the 409 was actually about.
        var userId = await FindUserIdByQueryAsync("email", email, token, cancellationToken)
            ?? await FindUserIdByQueryAsync("username", email, token, cancellationToken);
        if (userId is null)
        {
            // Both a by-email and a by-username lookup found nothing after Keycloak just reported the
            // username taken - a genuine inconsistency, not a case this port's own callers have any legal
            // recourse for, so it is thrown, the same "should be unreachable" shape
            // OperatorInviteRedemptionRepository's own site-not-found guard uses for its identical kind
            // of contradiction.
            throw new InvalidOperationException(
                $"Keycloak reported a 409 creating a user for '{email}' but a lookup by neither its email nor its username found one.");
        }

        // This user already existed under whatever locale (or none) it was last given - synced here so
        // the email this invite is about to send still renders in the inviting site's own language,
        // not whatever this person's account happened to carry from before. `26-262`: the same sync
        // also (re)asserts the `email`, both preserving an existing one and backfilling the `26-260`
        // username-only user that had none - without which the execute-actions-email below has no
        // address to send to.
        await SyncEmailAndLocaleAsync(userId, email, locale, token, cancellationToken);
        return userId;
    }

    private async Task<string?> FindUserIdByQueryAsync(
        string field, string value, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{BaseUrl}/admin/realms/{options.Realm}/users?{field}={Uri.EscapeDataString(value)}&exact=true");
        request.Headers.Authorization = new("Bearer", token);

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var root = payload.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0)
        {
            return null;
        }

        return root[0].GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Keycloak returned an existing user with no id.");
    }

    private async Task SyncEmailAndLocaleAsync(
        string userId, string email, Locale locale, string token, CancellationToken cancellationToken)
    {
        // `26-262`: Keycloak's `PUT .../users/{id}` replaces the *whole* user representation, so any field
        // omitted from the body is cleared. The earlier version of this method sent only
        // `{ "attributes": { "locale": [...] } }`, which wiped the user's `email`; the subsequent
        // execute-actions-email then failed with "User email missing" and the invite came back
        // `sendFailed`. So the current representation is fetched and round-tripped: `email` is set (which
        // both preserves an existing address and backfills the `26-260` username-only user that had none)
        // and the `locale` attribute is merged into whatever attributes the user already carries, with
        // every other field the GET returned - username, emailVerified, firstName, enabled,
        // requiredActions - left exactly as it was.
        using var getRequest = new HttpRequestMessage(
            HttpMethod.Get, $"{BaseUrl}/admin/realms/{options.Realm}/users/{Uri.EscapeDataString(userId)}");
        getRequest.Headers.Authorization = new("Bearer", token);

        using var getResponse = await http.SendAsync(getRequest, cancellationToken);
        getResponse.EnsureSuccessStatusCode();

        var representation = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync(cancellationToken)) as JsonObject
            ?? throw new InvalidOperationException("Keycloak returned an existing user representation that was not a JSON object.");

        representation["email"] = email;
        if (representation["attributes"] is not JsonObject attributes)
        {
            attributes = [];
            representation["attributes"] = attributes;
        }

        attributes["locale"] = new JsonArray(KeycloakLocaleCode(locale));

        using var putRequest = new HttpRequestMessage(
            HttpMethod.Put, $"{BaseUrl}/admin/realms/{options.Realm}/users/{Uri.EscapeDataString(userId)}")
        {
            Content = JsonContent.Create(representation),
        };
        putRequest.Headers.Authorization = new("Bearer", token);

        using var putResponse = await http.SendAsync(putRequest, cancellationToken);
        putResponse.EnsureSuccessStatusCode();
    }

    private async Task<OperatorInviteProvisionOutcome> SendActionsEmailAsync(
        string userId, OperatorInviteProvisionRequest request, string token, CancellationToken cancellationToken)
    {
        var lifespanSeconds = (int)Math.Max(1, request.Lifespan.TotalSeconds);
        var uri = $"{BaseUrl}/admin/realms/{options.Realm}/users/{Uri.EscapeDataString(userId)}/execute-actions-email"
            + $"?client_id={Uri.EscapeDataString(options.ConsoleClientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(request.RedirectUri)}"
            + $"&lifespan={lifespanSeconds}";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = JsonContent.Create(new[] { "UPDATE_PASSWORD", "UPDATE_PROFILE" }),
        };
        httpRequest.Headers.Authorization = new("Bearer", token);

        using var response = await http.SendAsync(httpRequest, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return new OperatorInviteProvisionOutcome.Sent();
        }

        // `25-73`'s own point 6: not swallowed into a log line, but not thrown either - the user was
        // created/found successfully (the step above already succeeded), so this invite is real and
        // must still be created; the failure belongs on its own row, for the console's own invite-list
        // screen. See this class's own doc comment for what "SmtpErrorCode" actually can and cannot
        // capture from Keycloak's own Admin REST response.
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        logger.LogWarning(
            "Keycloak's execute-actions-email call failed for an operator invite: {Status} {Body}",
            (int)response.StatusCode, Truncate(body));
        return new OperatorInviteProvisionOutcome.SendFailed(SmtpFailureDetail(response.StatusCode, body));
    }

    // Best-effort: Keycloak's Admin REST API answers execute-actions-email with its own HTTP status and
    // (sometimes) a short JSON error body - never a passthrough of the SMTP server's own numeric
    // response code. This is the most specific value actually available without a live realm to verify
    // against; see this class's own doc comment, point (c).
    private static string SmtpFailureDetail(HttpStatusCode status, string body)
    {
        var trimmed = Truncate(body).Trim();
        return trimmed.Length == 0 ? $"{(int)status}" : $"{(int)status}: {trimmed}";
    }

    private static string KeycloakLocaleCode(Locale locale) => locale switch
    {
        Locale.Ru => "ru",
        _ => "en",
    };

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is { } cached && clock.UtcNow < _accessTokenExpiresAt)
        {
            return cached;
        }

        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
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

            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
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

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500];

    public void Dispose() => _tokenGate.Dispose();
}
