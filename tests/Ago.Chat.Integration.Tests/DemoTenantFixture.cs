using System.Net.Http.Json;
using System.Text.Json;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.Keycloak;
using Testcontainers.PostgreSql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `8-07`/`adr/0058`: a real Postgres and a real Keycloak, with the service-account client this item
/// introduces actually created and actually granted `manage-users`.
///
/// <para><b>Why not reuse <see cref="OperatorOidcFixture"/>.</b> That fixture's realm is built for
/// `5-05`'s token validation - fixed users, no confidential client, no service account. What `8-07`
/// needs proved is the opposite end: that a client credential scoped to one role can create and delete
/// users, and that Keycloak accepts a <b>caller-chosen user id</b>, which is the fact
/// <c>MintDemoTenantHandler</c>'s whole write ordering rests on. Proving that against a client this
/// fixture wires the same way `keycloak-realm-import.json` does is the point; borrowing a realm that
/// has no such client would prove nothing.</para>
/// </summary>
/// <summary>Whether the password grant worked, and what Keycloak said if it did not.</summary>
public sealed record LoginAttempt(bool Succeeded, string Body);

public sealed class DemoTenantFixture : IAsyncLifetime
{
    public const string RealmName = "ago-chat-demo-test";
    public const string ProvisionerClientId = "ago-demo-provisioner";

    // Throwaway, generated nowhere and secret from nobody - the same category as AttachmentFixture's
    // MinIO credentials. It exists so this fixture and the client under test agree on a string.
    public const string ProvisionerClientSecret = "demo-tenant-tests-only-client-secret";

    private PostgreSqlContainer _postgres = null!;
    private KeycloakContainer _keycloak = null!;
    private IDisposable _dockerLock = null!;
    private static readonly HttpClient Http = new();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public string KeycloakBaseUrl { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _dockerLock = await DockerResourceLock.AcquireAsync();

        _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        // `quay.io/keycloak/keycloak:26.0` - the version `ago-deploy/k8s/base/keycloak.yaml` actually
        // runs, deliberately *not* the `21.1` OperatorOidcFixture pins. This fixture's whole purpose is
        // to establish what Keycloak does with a caller-chosen user id, and an answer obtained from a
        // version five majors behind the deployment would be an answer to a different question. (It is
        // also a different answer: 21.1 rejects the id with a 409, which is how this was found.)
        _keycloak = new KeycloakBuilder("quay.io/keycloak/keycloak:26.0").Build();
        await Task.WhenAll(_postgres.StartAsync(), _keycloak.StartAsync());

        DataSource = new NpgsqlDataSourceBuilder(_postgres.GetConnectionString()).Build();
        await using (var db = CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        KeycloakBaseUrl = _keycloak.GetBaseAddress().TrimEnd('/');
        await CreateRealmWithProvisionerClientAsync();
    }

    public async Task DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _keycloak.DisposeAsync().AsTask());
        _dockerLock.Dispose();
    }

    public AgoChatDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(DataSource).Options);

    /// <summary>
    /// Creates a user with a <b>caller-chosen id</b>, straight through the master admin, and reports
    /// what Keycloak did with it. This exists because `adr/0058` makes a claim about somebody else's
    /// software - whether the id may be chosen - and the first version of this item got that claim
    /// wrong by inferring it from a 409 that was really a username collision. A claim about an external
    /// system is worth one direct test.
    /// </summary>
    public async Task<(int Status, string? AssignedId)> CreateUserWithChosenIdAsync(
        string chosenId, string username)
    {
        var token = await GetMasterAdminTokenAsync();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{KeycloakBaseUrl}/admin/realms/{RealmName}/users")
        {
            Content = JsonContent.Create(new
            {
                id = chosenId,
                username,
                enabled = true,
                requiredActions = Array.Empty<string>(),
                firstName = "Probe",
                lastName = "User",
                email = $"{username}@demo.invalid",
                emailVerified = true,
            }),
        };
        request.Headers.Authorization = new("Bearer", token);
        using var response = await Http.SendAsync(request);

        var location = response.Headers.Location?.ToString();
        return ((int)response.StatusCode, location?[(location.LastIndexOf('/') + 1)..]);
    }

    /// <summary>Whether a user with this id exists - the assertion that makes "and the Keycloak user is
    /// gone" a fact rather than a hope. Uses the master admin, deliberately *not* the provisioner
    /// client: a check that used the same credential as the thing under test could pass because both
    /// were broken in the same way.</summary>
    public async Task<bool> UserExistsAsync(string subjectId)
    {
        var token = await GetMasterAdminTokenAsync();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{KeycloakBaseUrl}/admin/realms/{RealmName}/users/{subjectId}");
        request.Headers.Authorization = new("Bearer", token);
        using var response = await Http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    /// <summary>Proves the minted credentials actually work: a password grant against the realm, which
    /// is exactly what the console's login does. Done-when #1 is about a stranger getting *working*
    /// credentials, and "a row exists in Keycloak" is not the same claim.</summary>
    public async Task<LoginAttempt> CanLogInAsync(string username, string password)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "ago-console",
            ["username"] = username,
            ["password"] = password,
        });
        using var response = await Http.PostAsync(
            $"{KeycloakBaseUrl}/realms/{RealmName}/protocol/openid-connect/token", form);
        // The body is carried back, not discarded: "the credentials did not work" is a useless
        // assertion message, and Keycloak's own `error_description` is the one that says why.
        return new LoginAttempt(
            response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Builds the realm the way `keycloak-realm-import.json` does for the deployment: a public
    /// `ago-console` client with direct access grants (what the console logs in with), plus `8-07`'s
    /// confidential provisioner client whose service account holds exactly one role.
    /// </summary>
    private async Task CreateRealmWithProvisionerClientAsync()
    {
        var token = await GetMasterAdminTokenAsync();

        await PostAsync(token, "/admin/realms", new { realm = RealmName, enabled = true });
        await PostAsync(token, $"/admin/realms/{RealmName}/clients", new
        {
            clientId = "ago-console",
            publicClient = true,
            directAccessGrantsEnabled = true,
            standardFlowEnabled = true,
        });
        await PostAsync(token, $"/admin/realms/{RealmName}/clients", new
        {
            clientId = ProvisionerClientId,
            publicClient = false,
            serviceAccountsEnabled = true,
            standardFlowEnabled = false,
            directAccessGrantsEnabled = false,
            secret = ProvisionerClientSecret,
        });

        // The narrowing that makes this credential defensible: `manage-users` on this realm's own
        // realm-management client, and nothing else. Not a master-realm admin, which is what
        // `apply-realm-settings.sh` uses from the node and what `adr/0058` argues against holding in a
        // web-facing process.
        var provisionerUuid = await GetClientUuidAsync(token, ProvisionerClientId);
        var realmManagementUuid = await GetClientUuidAsync(token, "realm-management");
        var serviceAccount = await GetJsonAsync(
            token, $"/admin/realms/{RealmName}/clients/{provisionerUuid}/service-account-user");
        var serviceAccountId = serviceAccount.GetProperty("id").GetString();
        var manageUsers = await GetJsonAsync(
            token, $"/admin/realms/{RealmName}/clients/{realmManagementUuid}/roles/manage-users");

        await PostAsync(
            token,
            $"/admin/realms/{RealmName}/users/{serviceAccountId}/role-mappings/clients/{realmManagementUuid}",
            new[]
            {
                new
                {
                    id = manageUsers.GetProperty("id").GetString(),
                    name = manageUsers.GetProperty("name").GetString(),
                },
            });
    }

    private async Task<string> GetClientUuidAsync(string token, string clientId)
    {
        var clients = await GetJsonAsync(token, $"/admin/realms/{RealmName}/clients?clientId={clientId}");
        return clients[0].GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> GetJsonAsync(string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{KeycloakBaseUrl}{path}");
        request.Headers.Authorization = new("Bearer", token);
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private async Task PostAsync(string token, string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{KeycloakBaseUrl}{path}")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new("Bearer", token);
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> GetMasterAdminTokenAsync()
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = "admin-cli",
            ["username"] = KeycloakBuilder.DefaultUsername,
            ["password"] = KeycloakBuilder.DefaultPassword,
        });
        using var response = await Http.PostAsync(
            $"{KeycloakBaseUrl}/realms/master/protocol/openid-connect/token", form);
        response.EnsureSuccessStatusCode();
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return payload.RootElement.GetProperty("access_token").GetString()!;
    }
}

[CollectionDefinition(Name)]
public sealed class DemoTenantCollection : ICollectionFixture<DemoTenantFixture>
{
    public const string Name = "DemoTenant";
}
