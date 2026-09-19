using System.Net.Http.Headers;
using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.Caching;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Persistence.Postgres;
using Ago.Platform.Storage.S3;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Polly;
using SkiaSharp;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `25-160`'s own Done-when, verbatim: "uploading a valid ≤100x100 static PNG... succeeds, ends in
/// `ready`, and is servable from a public, non-expiring URL" and "a wrong-dimension image... is
/// rejected with a clear reason - proven by explicit tests... the server-side authoritative one." Calls
/// <see cref="SiteLogoValidator.ValidateAsync"/> directly against real Postgres and real MinIO - the
/// identical "the whole real decode/store round trip, nothing stubbed" shape
/// <see cref="AttachmentThumbnailEndToEndTests"/> already establishes for its own sibling, minus the
/// RabbitMQ dispatch/consumer half: that routing mechanism is the exact one
/// <c>AttachmentConfirmed</c>/<c>AttachmentThumbnailConsumer</c> already prove correct for an identical
/// shape (`5-04`), and a second full broker round trip for `SiteLogoValidationRequested` would prove the
/// same routing mechanism twice rather than anything specific to this item's own decode/validate logic -
/// a deliberate scope cut, stated here rather than silently assumed covered.
///
/// <b>Not covered here: an animated GIF.</b> Constructing a real, valid multi-frame GIF byte sequence
/// by hand (SkiaSharp has no multi-frame *encoder*, only a decoder) was judged not worth the risk of a
/// subtly-wrong hand-rolled fixture within this item's own time - `SiteLogoValidator.Validate`'s own
/// `codec.FrameCount > 1` check is exercised by nothing but a compiler here. A real gap, named rather
/// than silently left looking covered.
/// </summary>
public sealed class SiteLogoValidatorEndToEndTests
{
    private const string MinioUsername = "ago-test";
    private const string MinioPassword = "ago-test-local-dev";
    private const string Bucket = "attachments";

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ValidateAsync_WithAValidSmallStaticPng_PromotesTheLogoAndWarmsTheBrandingCache()
    {
        await using var harness = await Harness.StartAsync();
        var siteId = await harness.SeedSiteAsync();
        var pendingKey = await harness.UploadPendingAsync(siteId, CreateTestPngBytes(64, 64), "image/png");

        var cache = new FakeCache();
        var validator = harness.BuildValidator(cache);

        await validator.ValidateAsync(siteId, pendingKey, "image/png", CancellationToken.None);

        var site = await harness.ReloadSiteAsync(siteId);
        Assert.Equal(LogoStatus.Ready, site.LogoStatus);
        Assert.True(site.HasLogo);
        Assert.NotEqual(pendingKey, site.LogoObjectKey);

        var cached = await cache.GetAsync<SiteBrandingLogoPayload>(SiteBrandingCacheKeys.ForLogo(siteId), CancellationToken.None);
        Assert.NotNull(cached);
        Assert.Equal("image/png", cached!.ContentType);
    }

    [Fact]
    public async Task ValidateAsync_WithAnOversizedImage_RejectsWithAReasonAndPromotesNothing()
    {
        await using var harness = await Harness.StartAsync();
        var siteId = await harness.SeedSiteAsync();
        var pendingKey = await harness.UploadPendingAsync(siteId, CreateTestPngBytes(800, 600), "image/png");

        var validator = harness.BuildValidator(new FakeCache());

        await validator.ValidateAsync(siteId, pendingKey, "image/png", CancellationToken.None);

        var site = await harness.ReloadSiteAsync(siteId);
        Assert.Equal(LogoStatus.Rejected, site.LogoStatus);
        Assert.False(site.HasLogo);
        Assert.Contains("100x100", site.LogoRejectionReason);
    }

    [Fact]
    public async Task ValidateAsync_WithAWrongFormatFile_RejectsWithAReason()
    {
        await using var harness = await Harness.StartAsync();
        var siteId = await harness.SeedSiteAsync();
        var notAnImage = "this is not an image"u8.ToArray();
        var pendingKey = await harness.UploadPendingAsync(siteId, notAnImage, "image/png");

        var validator = harness.BuildValidator(new FakeCache());

        await validator.ValidateAsync(siteId, pendingKey, "image/png", CancellationToken.None);

        var site = await harness.ReloadSiteAsync(siteId);
        Assert.Equal(LogoStatus.Rejected, site.LogoStatus);
        Assert.False(site.HasLogo);
    }

    private static byte[] CreateTestPngBytes(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>The identical in-memory `ICache` fake `EmailChannelAdapterTests` already establishes
    /// for its own project - duplicated rather than shared, the same "a fake this small is not worth a
    /// shared reference for" judgement that class states for itself.</summary>
    private sealed class FakeCache : ICache
    {
        private readonly Dictionary<string, object?> _store = [];

        public Task<T?> GetAsync<T>(CacheKey key, CancellationToken cancellationToken) where T : class =>
            Task.FromResult(_store.TryGetValue(key.Value, out var value) ? (T?)value : default);

        public Task SetAsync<T>(CacheKey key, T value, CacheEntryOptions options, CancellationToken cancellationToken) where T : class
        {
            _store[key.Value] = value;
            return Task.CompletedTask;
        }

        public async Task<T> GetOrCreateAsync<T>(
            CacheKey key, Func<CancellationToken, Task<T>> factory, CacheEntryOptions options, CancellationToken cancellationToken)
            where T : class
        {
            if (_store.TryGetValue(key.Value, out var cached))
            {
                return (T)cached!;
            }

            var value = await factory(cancellationToken);
            _store[key.Value] = value;
            return value;
        }

        public Task RemoveAsync(CacheKey key, CancellationToken cancellationToken)
        {
            _store.Remove(key.Value);
            return Task.CompletedTask;
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly PostgreSqlContainer _postgres;
        private readonly MinioContainer _minio;
        private readonly DbContextOptions<AgoChatDbContext> _dbOptions;
        private readonly NpgsqlDataSource _dataSource;
        public IFileStorage FileStorage { get; }

        private Harness(PostgreSqlContainer postgres, MinioContainer minio, NpgsqlDataSource dataSource, DbContextOptions<AgoChatDbContext> dbOptions, IFileStorage fileStorage)
        {
            _postgres = postgres;
            _minio = minio;
            _dataSource = dataSource;
            _dbOptions = dbOptions;
            FileStorage = fileStorage;
        }

        public static async Task<Harness> StartAsync()
        {
            var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
            var minio = new MinioBuilder("quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z")
                .WithUsername(MinioUsername).WithPassword(MinioPassword).Build();
            await Task.WhenAll(postgres.StartAsync(), minio.StartAsync());

            var dataSource = new NpgsqlDataSourceBuilder(postgres.GetConnectionString()).Build();
            var dbOptions = new DbContextOptionsBuilder<AgoChatDbContext>().UseNpgsql(dataSource).Options;
            await using (var migrate = new AgoChatDbContext(dbOptions))
            {
                await migrate.Database.MigrateAsync();
            }

            var s3Options = new S3StorageOptions
            {
                ServiceUrl = minio.GetConnectionString(),
                AccessKey = minio.GetAccessKey(),
                SecretKey = minio.GetSecretKey(),
                Bucket = Bucket,
                ForcePathStyle = true,
            };
            var s3Client = S3ClientFactory.Create(s3Options);
            await s3Client.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });
            var resilience = new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(5)).Build();
            IFileStorage fileStorage = new S3FileStorage(s3Client, s3Options, resilience, NullLogger<S3FileStorage>.Instance);

            return new Harness(postgres, minio, dataSource, dbOptions, fileStorage);
        }

        public async Task<SiteId> SeedSiteAsync()
        {
            var siteId = new SiteId(Guid.NewGuid());
            await using var db = new AgoChatDbContext(_dbOptions);
            db.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
            await db.SaveChangesAsync();
            return siteId;
        }

        /// <summary>Uploads the bytes to a fresh pending key <b>and</b> records that upload on the
        /// <see cref="Site"/> aggregate via <see cref="Site.SubmitLogoUpload"/> - the exact step
        /// <c>SubmitLogoUploadHandler</c> performs before any validation ever runs in production.
        /// Skipping this and calling <see cref="SiteLogoValidator.ValidateAsync"/> against a site with
        /// no recorded pending upload would trip <see cref="Site.PromoteLogo"/>/
        /// <see cref="Site.RejectLogoUpload"/>'s own staleness guard (their own remarks) and silently do
        /// nothing - found by this test suite's own first run, not assumed.</summary>
        public async Task<string> UploadPendingAsync(SiteId siteId, byte[] bytes, string contentType)
        {
            var pendingKey = $"site/{siteId.Value}/logo/pending/{Guid.NewGuid():N}.png";
            var presigned = await FileStorage.CreateUploadAsync(
                new ObjectKey(pendingKey), new UploadConstraints(contentType, bytes.Length, TimeSpan.FromMinutes(5)), CancellationToken.None);
            using var http = new HttpClient();
            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            var response = await http.PutAsync(presigned.Url, content);
            response.EnsureSuccessStatusCode();

            await using var db = new AgoChatDbContext(_dbOptions);
            var site = await db.Sites.SingleAsync(s => s.Id == siteId);
            site.SubmitLogoUpload(pendingKey, contentType, Now);
            site.ClearDomainEvents();
            await db.SaveChangesAsync();

            return pendingKey;
        }

        public SiteLogoValidator BuildValidator(ICache cache) =>
            new(
                new SiteRepository(new AgoChatDbContext(_dbOptions)), FileStorage, new FakeOutboxWriter(), cache,
                Options.Create(new Application.UseCases.SubmitLogoUpload.SiteLogoOptions()), new UuidV7Generator(), new SystemClock(),
                NullLogger<SiteLogoValidator>.Instance);

        public async Task<Site> ReloadSiteAsync(SiteId siteId)
        {
            await using var db = new AgoChatDbContext(_dbOptions);
            return await db.Sites.SingleAsync(s => s.Id == siteId);
        }

        public async ValueTask DisposeAsync()
        {
            await _dataSource.DisposeAsync();
            await _postgres.DisposeAsync();
            await _minio.DisposeAsync();
        }

        private sealed class FakeOutboxWriter : IOutboxWriter
        {
            public void Enqueue(EventEnvelope envelope, string? traceContext = null)
            {
            }
        }
    }
}
