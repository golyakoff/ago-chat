using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Platform.Abstractions;
using Ago.Platform.Storage.S3;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Polly;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;

namespace Ago.Chat.Integration.Tests;

/// <summary>`5-03`'s upload flow genuinely spans two external resources (the attachment row in
/// Postgres, the object in MinIO) - the same "one fixture per genuinely-needed resource combination"
/// reasoning as <see cref="SiteCachingFixture"/>. Uses the real <see cref="S3FileStorage"/>
/// (`Ago.Platform.Storage.S3`, `5-02`), not a fake - this is the one test class that must prove the
/// whole chain (presign -&gt; real HTTP PUT -&gt; HEAD-verify) actually works end to end.</summary>
public sealed class AttachmentFixture : IAsyncLifetime
{
    private const string MinioUsername = "ago-test";
    private const string MinioPassword = "ago-test-local-dev";
    private const string Bucket = "attachments";

    private PostgreSqlContainer _postgres = null!;
    private MinioContainer _minio = null!;
    private IAmazonS3 _s3Client = null!;
    private IDisposable _dockerLock = null!;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public IFileStorage FileStorage { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _dockerLock = await DockerResourceLock.AcquireAsync();

        _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        // Pinned image tag, matching Ago.Platform.Integration.Tests' own MinioFixture (`5-02`) -
        // MinioBuilder's parameterless constructor is obsolete in the pinned Testcontainers.Minio
        // version and this project treats warnings as errors.
        _minio = new MinioBuilder("minio/minio:RELEASE.2025-09-07T16-13-09Z").WithUsername(MinioUsername).WithPassword(MinioPassword).Build();
        await Task.WhenAll(_postgres.StartAsync(), _minio.StartAsync());

        DataSource = new NpgsqlDataSourceBuilder(_postgres.GetConnectionString()).Build();
        await using (var db = CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var options = new S3StorageOptions
        {
            ServiceUrl = _minio.GetConnectionString(),
            AccessKey = _minio.GetAccessKey(),
            SecretKey = _minio.GetSecretKey(),
            Bucket = Bucket,
            ForcePathStyle = true,
        };
        _s3Client = S3ClientFactory.Create(options);
        await _s3Client.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });

        var resilience = new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(5)).Build();
        FileStorage = new S3FileStorage(_s3Client, options, resilience, NullLogger<S3FileStorage>.Instance);
    }

    public async Task DisposeAsync()
    {
        _s3Client.Dispose();
        await DataSource.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _minio.DisposeAsync().AsTask());
        _dockerLock.Dispose();
    }

    public AgoChatDbContext CreateDbContext()
    {
        var dbOptions = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(DataSource).Options;
        return new AgoChatDbContext(dbOptions);
    }
}

[CollectionDefinition(Name)]
public sealed class AttachmentCollection : ICollectionFixture<AttachmentFixture>
{
    public const string Name = "Attachment";
}
