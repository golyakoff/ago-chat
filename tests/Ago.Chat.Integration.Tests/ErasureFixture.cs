using System.Net.Http.Json;
using System.Text.Json;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Storage.S3;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Polly;
using Testcontainers.Keycloak;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `16-02`: a real Postgres, a real Keycloak and a real MinIO - the three stores `personal-data.md`'s
/// own table names for a tenant's data, and the three <see cref="SiteErasureIntegrationTests"/>/
/// <see cref="ConversationErasureIntegrationTests"/> assert emptiness against directly, not through a
/// recording double. Combines <see cref="DemoTenantFixture"/>'s own Postgres+Keycloak shape with
/// <c>AttachmentThumbnailEndToEndTests</c>' own real-MinIO/<see cref="S3FileStorage"/> shape - no
/// RabbitMQ, unlike that second test: cache invalidation is proven with a recording
/// <see cref="Ago.Platform.Abstractions.IEventPublisher"/> handed directly to
/// <see cref="Ago.Platform.Caching.Redis.CacheInvalidationPublisher"/>'s own constructor, since what
/// this item's Done-when needs proven is "the right keys were published for invalidation", not
/// "a fourth container's broker actually carried the message" - `caching.md`'s own invalidation
/// mechanism is proven end to end elsewhere.
/// </summary>
public sealed class ErasureFixture : IAsyncLifetime
{
    public const string RealmName = "ago-chat-erasure-test";
    public const string ProvisionerClientId = "ago-erasure-provisioner";

    // Throwaway, generated nowhere and secret from nobody - DemoTenantFixture's own precedent for
    // exactly this kind of test-only constant.
    public const string ProvisionerClientSecret = "erasure-tests-only-client-secret";

    private const string MinioUsername = "ago-test";
    private const string MinioPassword = "ago-test-local-dev";
    public const string Bucket = "attachments";

    private PostgreSqlContainer _postgres = null!;
    private KeycloakContainer _keycloak = null!;
    private MinioContainer _minio = null!;
    private IAmazonS3 _s3Client = null!;
    private IDisposable _dockerLock = null!;
    private static readonly HttpClient Http = new();

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public string KeycloakBaseUrl { get; private set; } = null!;

    public IFileStorage FileStorage { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _dockerLock = await DockerResourceLock.AcquireAsync();

        _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        // 26.0, not OperatorOidcFixture's 21.1 - DemoTenantFixture's own remarks on why this item's
        // deployment-matching version matters whenever a test asserts something about Keycloak's own
        // behaviour (here: that a user this fixture creates is genuinely deletable by subject id).
        _keycloak = new KeycloakBuilder("quay.io/keycloak/keycloak:26.0").Build();
        _minio = new MinioBuilder("minio/minio:RELEASE.2025-09-07T16-13-09Z")
            .WithUsername(MinioUsername).WithPassword(MinioPassword).Build();
        await Task.WhenAll(_postgres.StartAsync(), _keycloak.StartAsync(), _minio.StartAsync());

        DataSource = new NpgsqlDataSourceBuilder(_postgres.GetConnectionString()).Build();
        await using (var db = CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        KeycloakBaseUrl = _keycloak.GetBaseAddress().TrimEnd('/');
        await CreateRealmWithProvisionerClientAsync();

        var s3Options = new S3StorageOptions
        {
            ServiceUrl = _minio.GetConnectionString(),
            AccessKey = _minio.GetAccessKey(),
            SecretKey = _minio.GetSecretKey(),
            Bucket = Bucket,
            ForcePathStyle = true,
        };
        _s3Client = S3ClientFactory.Create(s3Options);
        await _s3Client.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });
        var resilience = new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(5)).Build();
        FileStorage = new S3FileStorage(_s3Client, s3Options, resilience, NullLogger<S3FileStorage>.Instance);
    }

    public async Task DisposeAsync()
    {
        _s3Client.Dispose();
        await DataSource.DisposeAsync();
        await Task.WhenAll(
            _postgres.DisposeAsync().AsTask(), _keycloak.DisposeAsync().AsTask(), _minio.DisposeAsync().AsTask());
        _dockerLock.Dispose();
    }

    public AgoChatDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(DataSource).Options);

    /// <summary>Uploads real bytes to a real MinIO object at <paramref name="key"/>, through the same
    /// presign-then-PUT path a real client uses (`AttachmentThumbnailEndToEndTests`' own precedent) -
    /// not a bypass straight to the S3 SDK, so what erasure deletes is provably the same kind of object
    /// a real attachment upload produces.</summary>
    public async Task UploadTestObjectAsync(string key, byte[] bytes, string contentType)
    {
        var presigned = await FileStorage.CreateUploadAsync(
            new ObjectKey(key), new UploadConstraints(contentType, bytes.Length, TimeSpan.FromMinutes(5)), CancellationToken.None);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        var response = await Http.PutAsync(presigned.Url, content);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Creates a real Keycloak user directly through the master admin - the same shape
    /// `KeycloakDemoIdentityProvisioner.CreateAsync` uses against the provisioner client, reused here
    /// with the master admin so a seeded operator's identity does not depend on the provisioner client
    /// under test being correct to set up test data.</summary>
    public async Task<string> CreateOperatorUserAsync(string username)
    {
        var token = await GetMasterAdminTokenAsync();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{KeycloakBaseUrl}/admin/realms/{RealmName}/users")
        {
            Content = JsonContent.Create(new
            {
                username,
                enabled = true,
                requiredActions = Array.Empty<string>(),
                firstName = "Erasure",
                lastName = "Operator",
                email = $"{username}@erasure.invalid",
                emailVerified = true,
            }),
        };
        request.Headers.Authorization = new("Bearer", token);
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var location = response.Headers.Location?.ToString();
        return location?[(location.LastIndexOf('/') + 1)..]
            ?? throw new InvalidOperationException("Keycloak created a user without a Location header.");
    }

    /// <summary>Whether a user with this id exists - the master admin, deliberately not the
    /// provisioner client under test, the same "the check must not share a credential with the thing
    /// it is checking" reasoning <see cref="DemoTenantFixture.UserExistsAsync"/> already states.</summary>
    public async Task<bool> UserExistsAsync(string subjectId)
    {
        var token = await GetMasterAdminTokenAsync();
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{KeycloakBaseUrl}/admin/realms/{RealmName}/users/{subjectId}");
        request.Headers.Authorization = new("Bearer", token);
        using var response = await Http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    private async Task CreateRealmWithProvisionerClientAsync()
    {
        var token = await GetMasterAdminTokenAsync();

        await PostAsync(token, "/admin/realms", new { realm = RealmName, enabled = true });
        await PostAsync(token, $"/admin/realms/{RealmName}/clients", new
        {
            clientId = ProvisionerClientId,
            publicClient = false,
            serviceAccountsEnabled = true,
            standardFlowEnabled = false,
            directAccessGrantsEnabled = false,
            secret = ProvisionerClientSecret,
        });

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
public sealed class ErasureCollection : ICollectionFixture<ErasureFixture>
{
    public const string Name = "Erasure";
}
