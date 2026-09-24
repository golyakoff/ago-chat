using System.Buffers.Text;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Infrastructure.Fcm;

/// <summary>
/// `26-100`/`adr/0181`: mints the FCM access token by the standard OAuth2 "JWT bearer" service-account
/// flow - build a JWT asserting this service account and the messaging scope, sign it RS256 with the
/// account's private key, POST it to Google's token endpoint, cache the returned access token until just
/// before it expires. Registered as a singleton so the cache is shared across every send
/// (<c>Ago.Chat.Worker/Program.cs</c>); the HTTP call to the token endpoint goes through an
/// <see cref="IHttpClientFactory"/>-created client, so the singleton never holds a socket open.
///
/// <para><b>Why hand-rolled, not Google.Apis.Auth (CLAUDE.md's package rule).</b> Google.Apis.Auth would
/// replace roughly the <see cref="MintAsync"/> method below, and it is genuinely good at it - but pulling
/// it in is worse here on three counts. First, it drags a transitive tree (Google.Apis.Core,
/// Newtonsoft.Json) into a backend that otherwise has none of it, widening the dependency and
/// vulnerability surface `vulnerability-response.md` has to track. Second, every other outbound provider
/// in this repo (RuStore, VK, MAX, WhatsApp, YooKassa) hand-rolls its own HTTP and auth rather than taking
/// a vendor SDK, so a Google SDK here would be the lone exception to a settled convention. Third - and
/// decisively - "hand-rolled" here does <em>not</em> mean hand-rolled cryptography: the one security-
/// sensitive step, the RS256 signature, is <see cref="RSA"/>'s own vetted BCL implementation
/// (<c>ImportFromPem</c> + <c>SignData</c>); what this class actually assembles is two JSON objects, a
/// base64url join and a form POST, none of which an SDK does more safely. The token-cache logic that
/// would be the other reason to reach for the SDK is a dozen lines with a lock. If the mint ever needs
/// features this flow lacks (workload-identity federation, universe domains), that is the moment to
/// reconsider - not now.</para>
/// </summary>
public sealed class FcmServiceAccountTokenProvider : IFcmAccessTokenProvider
{
    /// <summary>The one scope FCM HTTP v1 send requires.</summary>
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";

    private const string DefaultTokenUri = "https://oauth2.googleapis.com/token";

    /// <summary>Refresh this far before the token's stated expiry, so a send never races the boundary and
    /// gets a 401 it would have to retry. Google's tokens live an hour; a minute of slack is ample.</summary>
    private static readonly TimeSpan ExpiryLeeway = TimeSpan.FromSeconds(60);

    /// <summary>Named client so the token-endpoint call is a distinct, separately-configurable HTTP client
    /// from <see cref="FcmPushSender"/>'s send client (`Ago.Chat.Worker/Program.cs` registers both).</summary>
    public const string HttpClientName = "fcm-oauth-token";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FcmServiceAccount _account;
    private readonly string _tokenUri;
    private readonly SemaphoreSlim _mintLock = new(1, 1);

    private string? _cachedToken;
    private DateTimeOffset _cachedTokenExpiresAt;

    public FcmServiceAccountTokenProvider(IHttpClientFactory httpClientFactory, IOptions<FcmOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _account = ParseServiceAccount(options.Value.ServiceAccountJson);
        _tokenUri = string.IsNullOrWhiteSpace(_account.TokenUri) ? DefaultTokenUri : _account.TokenUri;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        // DateTimeOffset.UtcNow is allowed here: this is Infrastructure (CLAUDE.md rule 11 bans it only
        // above this boundary), and a token's validity is real wall-clock time Google measures, not an
        // orderable domain event, so it does not belong on IClock's sequence-based contract.
        if (_cachedToken is { } cached && DateTimeOffset.UtcNow < _cachedTokenExpiresAt)
        {
            return cached;
        }

        await _mintLock.WaitAsync(cancellationToken);
        try
        {
            if (_cachedToken is { } stillCached && DateTimeOffset.UtcNow < _cachedTokenExpiresAt)
            {
                return stillCached;
            }

            var (token, lifetime) = await MintAsync(cancellationToken);
            _cachedToken = token;
            _cachedTokenExpiresAt = DateTimeOffset.UtcNow + lifetime - ExpiryLeeway;
            return token;
        }
        finally
        {
            _mintLock.Release();
        }
    }

    private async Task<(string Token, TimeSpan Lifetime)> MintAsync(CancellationToken cancellationToken)
    {
        var assertion = BuildSignedAssertion();

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
            ["assertion"] = assertion,
        });

        var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await httpClient.PostAsync(_tokenUri, content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<FcmTokenResponse>(cancellationToken);
        if (body?.AccessToken is not { Length: > 0 } accessToken)
        {
            throw new InvalidOperationException("Google's token endpoint returned no access_token.");
        }

        return (accessToken, TimeSpan.FromSeconds(body.ExpiresIn));
    }

    private string BuildSignedAssertion()
    {
        var issuedAt = DateTimeOffset.UtcNow;
        var header = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new JwtHeader("RS256", "JWT")));
        var claims = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new JwtClaims(
            _account.ClientEmail!, Scope, _tokenUri, issuedAt.ToUnixTimeSeconds(),
            issuedAt.AddHours(1).ToUnixTimeSeconds())));

        var signingInput = $"{header}.{claims}";

        using var rsa = RSA.Create();
        rsa.ImportFromPem(_account.PrivateKey!);
        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }

    private static FcmServiceAccount ParseServiceAccount(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException(
                $"{FcmOptions.SectionName}:ServiceAccountJson is empty - the FCM service-account key must be supplied "
                + "(FCM_SERVICE_ACCOUNT_JSON).");
        }

        FcmServiceAccount? account;
        try
        {
            account = JsonSerializer.Deserialize<FcmServiceAccount>(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"{FcmOptions.SectionName}:ServiceAccountJson is not valid JSON - expected a Google service-account key file.",
                ex);
        }

        if (account?.ClientEmail is not { Length: > 0 } || account.PrivateKey is not { Length: > 0 })
        {
            throw new InvalidOperationException(
                $"{FcmOptions.SectionName}:ServiceAccountJson is missing client_email or private_key.");
        }

        return account;
    }

    private sealed record JwtHeader(
        [property: System.Text.Json.Serialization.JsonPropertyName("alg")] string Alg,
        [property: System.Text.Json.Serialization.JsonPropertyName("typ")] string Typ);

    private sealed record JwtClaims(
        [property: System.Text.Json.Serialization.JsonPropertyName("iss")] string Iss,
        [property: System.Text.Json.Serialization.JsonPropertyName("scope")] string Scope,
        [property: System.Text.Json.Serialization.JsonPropertyName("aud")] string Aud,
        [property: System.Text.Json.Serialization.JsonPropertyName("iat")] long Iat,
        [property: System.Text.Json.Serialization.JsonPropertyName("exp")] long Exp);
}
