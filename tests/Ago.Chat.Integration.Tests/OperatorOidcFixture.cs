using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ago.Chat.Api.Auth;
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

    /// <summary>`12-01`: an operator that really does hold `5-08`'s site-wide `"Admin"` role
    /// (`site:configure`), seeded in <see cref="InitializeAsync"/> exactly the way
    /// `create-demo-tenant.sh` seeds the real `demo-admin`. Exists so `PlatformOwnerPolicyTests` can
    /// reject a genuinely privileged operator rather than a conveniently powerless one - the
    /// negative case is the whole point of that file.</summary>
    public const string DemoAdminUsername = "demo-admin";

    /// <summary>`12-01`/`adr/0032`: the fixed local identity holding the `platform-owner` *realm*
    /// role. Deliberately has no `operators` row and never gets one - a platform owner is not an
    /// `Operator`, so a test that passed only because this user also happened to be an operator
    /// would prove nothing about the boundary.</summary>
    public const string PlatformOwnerUsername = "platform-owner-test";

    /// <summary>`17-06`: the user <see cref="RealmLoginSecurityTests"/> deliberately locks out, and
    /// the only test user in this realm that no other test ever authenticates as. Brute-force
    /// protection disables an account for real, so a shared user would leave every later test in this
    /// collection failing on a 400 from the token endpoint - the same "give the destructive case its
    /// own fixed identity" reasoning <see cref="OrphanOperatorUsername"/> already applies to the
    /// never-linked case.</summary>
    public const string LockoutTargetUsername = "lockout-target";

    public const string LockoutTargetPassword = "lockout-target-password";

    public const string DemoOperatorPassword = "demo-operator-password";
    public static readonly Guid DemoOperatorExternalSubjectId = Guid.Parse("00000000-0000-0000-0000-0000000000f0");
    public static readonly Guid DemoAdminExternalSubjectId = Guid.Parse("00000000-0000-0000-0000-0000000000f2");

    private PostgreSqlContainer _postgres = null!;
    private KeycloakContainer _keycloak = null!;
    private IDisposable _dockerLock = null!;
    private static readonly HttpClient Http = new();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public string KeycloakAuthority { get; private set; } = null!;

    public SiteId SeededSiteId { get; private set; }

    public OperatorId SeededOperatorId { get; private set; }

    /// <summary>`12-01`: the `demo-admin` operator row, holding the seeded `"Admin"` role.</summary>
    public OperatorId SeededAdminOperatorId { get; private set; }

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
        SeededAdminOperatorId = new OperatorId(Guid.NewGuid());
        var adminRoleId = Guid.NewGuid();
        await using (var db = CreateDbContext())
        {
            db.Sites.Add(new Site(SeededSiteId, $"site_{SeededSiteId.Value:N}", []));
            db.Operators.Add(new Operator(
                SeededOperatorId, SeededSiteId, OperatorStatus.Online, capacity: 5,
                externalSubjectId: DemoOperatorExternalSubjectId.ToString()));
            // `12-01`: the same `"Admin"` role `5-08` seeds for real (`create-demo-tenant.sh`) -
            // every permission that role holds, not a reduced stand-in, so the "a site:configure
            // holder is still not the platform owner" test rejects the strongest operator this
            // codebase can currently produce.
            db.Operators.Add(new Operator(
                SeededAdminOperatorId, SeededSiteId, OperatorStatus.Online, capacity: 5,
                externalSubjectId: DemoAdminExternalSubjectId.ToString()));
            db.Roles.Add(new RoleRecord
            {
                Id = adminRoleId,
                SiteId = SeededSiteId,
                Name = "Admin",
                Permissions =
                [
                    Permission.SiteConfigure.Value,
                    Permission.SiteManageOperators.Value,
                    Permission.AttachmentDelete.Value,
                ],
            });
            db.OperatorRoles.Add(new OperatorRoleRecord { OperatorId = SeededAdminOperatorId, RoleId = adminRoleId });
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

    /// <summary>`12-01`: a real token for the operator holding `5-08`'s `"Admin"` role - a genuine
    /// `operators` row with `site:configure`, so `RequireOperatorIdentity` accepts it and
    /// `RequirePlatformOwner` must still not.</summary>
    public Task<string> GetDemoAdminAccessTokenAsync() => GetAccessTokenAsync(KeycloakAuthority, DemoAdminUsername);

    /// <summary>`12-01`/`adr/0032`: a real token carrying the `platform-owner` realm role in
    /// `realm_access.roles`, for an identity with no `operators` row at all.</summary>
    public Task<string> GetPlatformOwnerAccessTokenAsync() => GetAccessTokenAsync(KeycloakAuthority, PlatformOwnerUsername);

    /// <summary>`10-02`: a brand-new Keycloak user, created via the admin API on demand rather than
    /// realm-import - unlike <see cref="GetOrphanOperatorAccessTokenAsync"/> (which is deliberately
    /// shared and must stay permanently unlinked for every other test in this collection), site
    /// registration tests need a genuinely fresh identity *per call* - the whole point of the test is
    /// that this identity starts unlinked and ends linked, so reusing a shared fixed user across
    /// multiple `[Fact]`s (or even multiple calls within one) would have the second call observe the
    /// first call's own write. Mirrors <see cref="GetWrongIssuerAccessTokenAsync"/>'s own
    /// create-via-admin-API shape, against this realm instead of a throwaway second one.
    ///
    /// Returns the username alongside the token (not just the token) so a caller that needs a
    /// *second*, later token for the identical identity - proving `OperatorIdentityClaimsTransformation`
    /// resolves it fresh at request time, not from something the first token happened to carry - can
    /// do so via <see cref="RefreshAccessTokenAsync"/> without minting a second, unrelated Keycloak
    /// user.</summary>
    public async Task<(string AccessToken, string Username)> CreateFreshUserAccessTokenAsync()
    {
        var adminToken = await GetAdminTokenAsync();
        var username = $"self-register-{Guid.NewGuid():N}";

        await PostAdminApiAsync(adminToken, $"/admin/realms/{RealmName}/users", new
        {
            username,
            email = $"{username}@example.test",
            firstName = "Self",
            lastName = "Register",
            enabled = true,
            emailVerified = true,
            credentials = new[] { new { type = "password", value = FreshUserPassword, temporary = false } },
        });

        return (await GetAccessTokenAsync(KeycloakAuthority, username, FreshUserPassword), username);
    }

    /// <summary>`12-05`: a brand-new Keycloak user that *also* holds the `platform-owner` realm role -
    /// the identity nobody on this deployment had ever been until now, and the only kind of identity
    /// that can prove the two axes `adr/0063` calls orthogonal really are.
    ///
    /// <para><b>Why not <see cref="PlatformOwnerUsername"/>.</b> That user is fixed, shared by every
    /// test in this collection, and documented above as one that never gets an `operators` row -
    /// several tests assert exactly that (`SiteRegistrationTests`, `PlatformOwnerPolicyTests`,
    /// <see cref="OwnerSitesEndpointTests"/>). `12-05`'s subject is an owner who *does* register a
    /// tenant, which is a one-way state transition; doing it to the shared user would make the rest of
    /// the collection order-dependent. Same reasoning, and the same shape, as
    /// <see cref="CreateFreshUserAccessTokenAsync"/> - which this deliberately reuses rather than
    /// re-implements, adding only the realm-role assignment.</para>
    ///
    /// <para>The role is granted over Keycloak's admin API rather than declared in the realm import,
    /// because the user does not exist until this method runs. The realm role itself does come from
    /// the import (`keycloak-realm-import.json`), so this grants the same role
    /// <see cref="PlatformOwnerUsername"/> holds - not a second one that happens to share a name.</para>
    /// </summary>
    public async Task<(string AccessToken, string Username)> CreateFreshPlatformOwnerAccessTokenAsync()
    {
        var (_, username) = await CreateFreshUserAccessTokenAsync();

        var adminToken = await GetAdminTokenAsync();
        var role = await GetAdminApiAsync($"/admin/realms/{RealmName}/roles/{PlatformOwnerRequirement.RealmRoleName}");
        var userId = await GetUserIdAsync(username);
        await PostAdminApiAsync(
            adminToken,
            $"/admin/realms/{RealmName}/users/{userId}/role-mappings/realm",
            new[] { new { id = role.GetProperty("id").GetString(), name = PlatformOwnerRequirement.RealmRoleName } });

        // Minted *after* the grant, never before: `realm_access.roles` is written into the token at
        // issue time, so a token taken from CreateFreshUserAccessTokenAsync above would not carry the
        // role even though the user now holds it.
        return (await GetAccessTokenAsync(KeycloakAuthority, username, FreshUserPassword), username);
    }

    /// <summary>A fresh token for a username <see cref="CreateFreshUserAccessTokenAsync"/> already
    /// created - the same Keycloak identity, a different (later) token, so a caller can prove claims
    /// resolution happens per-request rather than being baked into whichever token was used first.</summary>
    public Task<string> RefreshAccessTokenAsync(string username) =>
        GetAccessTokenAsync(KeycloakAuthority, username, FreshUserPassword);

    /// <summary>`23-02`: changes a user's `firstName`/`lastName` in the IdP itself - what
    /// `OperatorIdentityRefreshEndpointTests` needs to prove "changing the name in the IdP and signing
    /// in again updates the row." The realm's own "full name" mapper (`${firstName} ${lastName}`,
    /// `operatorDisplayName.ts`'s own remarks, `ago-console`) is what turns this into a different
    /// `name` claim on the *next* token minted for this user - the current one, already issued, is
    /// unaffected, which is why every caller mints a fresh token via <see cref="RefreshAccessTokenAsync"/>
    /// afterward rather than reusing one taken before this call.</summary>
    public async Task UpdateUserNameAsync(string username, string firstName, string lastName)
    {
        var adminToken = await GetAdminTokenAsync();
        var userId = await GetUserIdAsync(username);
        using var request = new HttpRequestMessage(
            HttpMethod.Put, $"{_keycloakBaseAddress}/admin/realms/{RealmName}/users/{userId}")
        {
            Content = JsonContent.Create(new { firstName, lastName }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
        var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private const string FreshUserPassword = "self-register-password";

    /// <summary>`17-06`: a raw password-grant attempt whose *failure* is the point, so unlike every
    /// other token helper here this one never throws on a non-success status - the caller asserts on
    /// it. Returns the token endpoint's status code and body together, because Keycloak deliberately
    /// answers a locked-out account and a wrong password with the same `invalid_grant` code (no user
    /// enumeration), which is exactly why the behavioural proof of a lockout has to be "the *correct*
    /// password stops working", not "the error text changed".</summary>
    public async Task<(HttpStatusCode Status, string Body)> TryPasswordGrantAsync(string username, string password)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = ClientId,
            ["username"] = username,
            ["password"] = password,
        };

        var response = await Http.PostAsync(
            $"{KeycloakAuthority}/protocol/openid-connect/token", new FormUrlEncodedContent(form));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>`17-06`: the realm as Keycloak actually holds it, not as the import file spells it.
    /// The distinction matters here specifically - `--import-realm` is skip-if-exists, so a settings
    /// change that never reached a running realm is a real and previously-hit failure mode
    /// (`kustomization.yaml`'s own note in `ago-deploy`); reading it back over the admin API is the
    /// only way a test can tell "the file says so" from "the realm does so".</summary>
    public Task<JsonElement> GetRealmRepresentationAsync() =>
        GetAdminApiAsync($"/admin/realms/{RealmName}");

    /// <summary>`17-06`: Keycloak's own brute-force view of one user - `numFailures`, and `disabled`,
    /// which is the unambiguous statement that the account is currently locked out.</summary>
    public Task<JsonElement> GetBruteForceStatusAsync(string userId) =>
        GetAdminApiAsync($"/admin/realms/{RealmName}/attack-detection/brute-force/users/{userId}");

    /// <summary>Clears the brute-force counters for the whole realm, so a locked-out user is usable
    /// again - called by the lockout test itself, so a re-run against a container that some earlier
    /// run already locked still starts from a known state.</summary>
    public async Task ClearBruteForceAttemptsAsync()
    {
        var adminToken = await GetAdminTokenAsync();
        using var request = new HttpRequestMessage(
            HttpMethod.Delete, $"{_keycloakBaseAddress}/admin/realms/{RealmName}/attack-detection/brute-force/users");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
        var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>`17-06`: resolves a username to Keycloak's own user id, so a test never has to
    /// hardcode one of the realm import's fixed UUIDs in a second place.</summary>
    public async Task<string> GetUserIdAsync(string username)
    {
        var users = await GetAdminApiAsync($"/admin/realms/{RealmName}/users?username={username}&exact=true");
        return users.EnumerateArray().Single().GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> GetAdminApiAsync(string path)
    {
        var adminToken = await GetAdminTokenAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_keycloakBaseAddress}{path}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminToken);
        var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        // Parsed into a JsonDocument that is deliberately not disposed: the JsonElement handed back
        // borrows that document's own pooled buffer, so disposing here would hand the caller a
        // use-after-free. Test-only, one small document per call - the GC reclaims it.
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement;
    }

    private static Task<string> GetAccessTokenAsync(string realmAuthority, string username) =>
        GetAccessTokenAsync(realmAuthority, username, DemoOperatorPassword);

    private static async Task<string> GetAccessTokenAsync(string realmAuthority, string username, string password)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = ClientId,
            ["username"] = username,
            ["password"] = password,
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
