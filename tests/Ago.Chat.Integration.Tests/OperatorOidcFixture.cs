using System.Net.Http.Json;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.Keycloak;
using Testcontainers.PostgreSql;

namespace Ago.Chat.Integration.Tests;

/// <summary>`5-05`/`adr/0022`: a real Keycloak and a real Postgres - the two things
/// `OperatorIdentityClaimsTransformation` actually talks to. Own collection (not shared with
/// `PostgresFixture`) since nothing else in this suite needs Keycloak, matching the project's own
/// "one fixture per genuinely-needed resource combination" precedent (`SiteCachingFixture`).
///
/// The demo operator row is seeded once here, in <see cref="InitializeAsync"/>, not per-test - the
/// Keycloak side is a single fixed user (`demo-operator`, one external subject id), so a second test
/// seeding its own row for the same subject hits the unique-index violation
/// `AttachmentConfiguration`-style guards exist to enforce (found live: running the full class
/// together, not each test in isolation). Tests needing "no operator row exists yet" use
/// <see cref="GetOrphanOperatorAccessTokenAsync"/> instead - a second, permanently-unlinked Keycloak
/// user - rather than racing to observe the shared demo operator before some other test seeds it.</summary>
public sealed class OperatorOidcFixture : IAsyncLifetime
{
    public const string RealmName = "ago-chat-test";
    public const string ClientId = "ago-console";
    public const string DemoOperatorUsername = "demo-operator";
    public const string OrphanOperatorUsername = "orphan-operator";
    public const string DemoOperatorPassword = "demo-operator-password";
    public static readonly Guid DemoOperatorExternalSubjectId = Guid.Parse("00000000-0000-0000-0000-0000000000f0");

    private PostgreSqlContainer _postgres = null!;
    private KeycloakContainer _keycloak = null!;
    private IDisposable _dockerLock = null!;
    private static readonly HttpClient Http = new();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public string KeycloakAuthority { get; private set; } = null!;

    public SiteId SeededSiteId { get; private set; }

    public OperatorId SeededOperatorId { get; private set; }

    private string _keycloakBaseAddress = null!;

    public async Task InitializeAsync()
    {
        _dockerLock = await DockerResourceLock.AcquireAsync();

        _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        // Explicit image tag, matching every other Testcontainers builder in this project - the
        // parameterless/constant-based overloads are obsolete in the pinned Testcontainers.Keycloak
        // version (same reason 5-02's MinioBuilder needed one).
        _keycloak = new KeycloakBuilder("quay.io/keycloak/keycloak:21.1")
            .WithRealm(Path.Combine(AppContext.BaseDirectory, "keycloak-realm-import.json"))
            .Build();
        await Task.WhenAll(_postgres.StartAsync(), _keycloak.StartAsync());

        DataSource = new NpgsqlDataSourceBuilder(_postgres.GetConnectionString()).Build();
        await using (var db = CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        _keycloakBaseAddress = _keycloak.GetBaseAddress();
        KeycloakAuthority = $"{_keycloakBaseAddress}/realms/{RealmName}";

        SeededSiteId = new SiteId(Guid.NewGuid());
        SeededOperatorId = new OperatorId(Guid.NewGuid());
        await using (var db = CreateDbContext())
        {
            db.Sites.Add(new Site(SeededSiteId, $"site_{SeededSiteId.Value:N}", []));
            db.Operators.Add(new Operator(
                SeededOperatorId, SeededSiteId, OperatorStatus.Online, capacity: 5,
                externalSubjectId: DemoOperatorExternalSubjectId.ToString()));
            await db.SaveChangesAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _keycloak.DisposeAsync().AsTask());
        _dockerLock.Dispose();
    }

    public AgoChatDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(DataSource).Options;
        return new AgoChatDbContext(options);
    }

    /// <summary>The direct (password) grant - a real token, minted by a real Keycloak, for the
    /// realm-imported demo operator user. Not the Authorization Code + PKCE flow the real console
    /// uses (`adr/0022`) - that is a browser redirect flow, out of scope for an automated test that
    /// only needs a genuine, Keycloak-signed token to validate against.</summary>
    public Task<string> GetDemoOperatorAccessTokenAsync() => GetAccessTokenAsync(KeycloakAuthority, DemoOperatorUsername);

    /// <summary>A second, permanently-unlinked Keycloak user - no `operators` row will ever carry
    /// its subject, so a token for it always exercises the "no matching operator" rejection path
    /// without racing the shared demo operator seeded in <see cref="InitializeAsync"/>.</summary>
    public Task<string> GetOrphanOperatorAccessTokenAsync() => GetAccessTokenAsync(KeycloakAuthority, OrphanOperatorUsername);

    private static async Task<string> GetAccessTokenAsync(string realmAuthority, string username)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = ClientId,
            ["username"] = username,
            ["password"] = DemoOperatorPassword,
        };

        var response = await Http.PostAsync($"{realmAuthority}/protocol/openid-connect/token", new FormUrlEncodedContent(form));
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>();
        return payload!.AccessToken;
    }

    /// <summary>`5-05`'s Done-when: "a token from the *wrong* issuer... is rejected - proven, not
    /// assumed." A locally-forged token would only ever fail on signature, never isolating "wrong
    /// issuer" as the actual reason - so this is a second, genuinely separate realm on the same
    /// Keycloak container, provisioned via the admin REST API (no second static import file needed;
    /// `Testcontainers.Keycloak`'s `WithRealm` takes exactly one file), whose own validly-signed
    /// token the Operator scheme's `Authority` (locked to <see cref="RealmName"/>) must still reject.</summary>
    public async Task<string> GetWrongIssuerAccessTokenAsync()
    {
        var adminToken = await GetAdminTokenAsync();

        await PostAdminApiAsync(adminToken, "/admin/realms", new { realm = OtherRealmName, enabled = true });
        await PostAdminApiAsync(adminToken, $"/admin/realms/{OtherRealmName}/clients", new
        {
            clientId = ClientId,
            publicClient = true,
            standardFlowEnabled = true,
            directAccessGrantsEnabled = true,
            redirectUris = new[] { "*" },
        });
        await PostAdminApiAsync(adminToken, $"/admin/realms/{OtherRealmName}/users", new
        {
            username = DemoOperatorUsername,
            enabled = true,
            emailVerified = true,
            credentials = new[] { new { type = "password", value = DemoOperatorPassword, temporary = false } },
        });

        return await GetAccessTokenAsync($"{_keycloakBaseAddress}/realms/{OtherRealmName}", DemoOperatorUsername);
    }

    private const string OtherRealmName = "ago-chat-other-test";

    private async Task<string> GetAdminTokenAsync()
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = KeycloakBuilder.DefaultUsername,
            ["password"] = KeycloakBuilder.DefaultPassword,
        };
        var response = await Http.PostAsync(
            $"{_keycloakBaseAddress}/realms/master/protocol/openid-connect/token", new FormUrlEncodedContent(form));
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>();
        return payload!.AccessToken;
    }

    private async Task PostAdminApiAsync(string adminToken, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_keycloakBaseAddress}{path}")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
        var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private sealed record TokenResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string AccessToken);
}

[CollectionDefinition(Name)]
public sealed class OperatorOidcCollection : ICollectionFixture<OperatorOidcFixture>
{
    public const string Name = "OperatorOidc";
}
