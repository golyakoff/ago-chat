using Ago.Chat.Application.Abstractions;
using Ago.Chat.Application.UseCases.ConfirmAttachment;
using Ago.Chat.Domain;
using Ago.Chat.Infrastructure.Postgres;
using Ago.Chat.Infrastructure.Postgres.Persistence;
using Ago.Chat.Worker;
using Ago.Platform.Abstractions;
using Ago.Platform.Hosting;
using Ago.Platform.Kernel;
using Ago.Platform.Messaging.RabbitMq;
using Ago.Platform.Persistence.Postgres;
using Ago.Platform.Storage.S3;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Polly;
using SkiaSharp;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace Ago.Chat.Integration.Tests;

/// <summary>
/// `5-04`'s Done-when, verbatim: "confirming an image attachment results in a real thumbnail object
/// in storage and `thumbnail_key` populated, against real Postgres/RabbitMQ/MinIO." The whole chain,
/// nothing stubbed: `ConfirmAttachmentHandler` stages `AttachmentConfirmed` in the same transaction as
/// the state change, the real `OutboxDispatcher` publishes it, the real `AttachmentThumbnailConsumer`
/// picks it up and thumbnails.
///
/// Own, non-shared containers - matching `UnreadCounterEndToEndTests`' own reasoning (a shared
/// `AttachmentFixture` would pollute other tests' outbox-topic message counts).
/// </summary>
public sealed class AttachmentThumbnailEndToEndTests
{
    private const string RabbitUsername = "ago-test";
    private const string RabbitPassword = "ago-test-local-dev";
    private const string MinioUsername = "ago-test";
    private const string MinioPassword = "ago-test-local-dev";
    private const string Bucket = "attachments";
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConfirmingAnImageAttachment_ProducesARealThumbnail_ViaTheRealOutboxAndConsumer()
    {
        var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var rabbitMq = new RabbitMqBuilder("rabbitmq:4-management").WithUsername(RabbitUsername).WithPassword(RabbitPassword).Build();
        var minio = new MinioBuilder("minio/minio:RELEASE.2025-09-07T16-13-09Z").WithUsername(MinioUsername).WithPassword(MinioPassword).Build();
        await Task.WhenAll(postgres.StartAsync(), rabbitMq.StartAsync(), minio.StartAsync());

        try
        {
            await using var dataSource = new NpgsqlDataSourceBuilder(postgres.GetConnectionString()).Build();
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
            using var s3Client = S3ClientFactory.Create(s3Options);
            await s3Client.PutBucketAsync(new PutBucketRequest { BucketName = Bucket });
            var resilience = new ResiliencePipelineBuilder().AddTimeout(TimeSpan.FromSeconds(5)).Build();
            IFileStorage fileStorage = new S3FileStorage(s3Client, s3Options, resilience, NullLogger<S3FileStorage>.Instance);

            var siteId = new SiteId(Guid.NewGuid());
            var visitorId = new VisitorId(Guid.NewGuid());
            var conversationId = new ConversationId(Guid.NewGuid());
            var objectKey = $"site/{siteId.Value}/conv/{conversationId.Value}/{Guid.NewGuid():N}.png";
            var imageBytes = CreateTestPngBytes();

            await using (var seed = new AgoChatDbContext(dbOptions))
            {
                seed.Sites.Add(new Site(siteId, $"site_{siteId.Value:N}", []));
                seed.Visitors.Add(new Visitor(visitorId, siteId, Now));
                seed.Conversations.Add(Conversation.Start(conversationId, siteId, visitorId, Now));
                await seed.SaveChangesAsync();
            }

            var attachment = Attachment.CreatePending(
                new AttachmentId(Guid.NewGuid()), siteId, conversationId, objectKey, "image/png", imageBytes.Length, Now);
            await using (var seedAttachment = new AgoChatDbContext(dbOptions))
            {
                seedAttachment.Attachments.Add(attachment);
                await seedAttachment.SaveChangesAsync();
            }

            var presigned = await fileStorage.CreateUploadAsync(
                new ObjectKey(objectKey), new UploadConstraints("image/png", imageBytes.Length, TimeSpan.FromMinutes(5)), CancellationToken.None);
            using (var http = new HttpClient())
            using (var content = new ByteArrayContent(imageBytes))
            {
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                var putResponse = await http.PutAsync(presigned.Url, content);
                putResponse.EnsureSuccessStatusCode();
            }

            var rabbitOptions = Options.Create(new RabbitMqOptions
            {
                HostName = rabbitMq.Hostname,
                Port = rabbitMq.GetMappedPublicPort(5672),
                UserName = RabbitUsername,
                Password = RabbitPassword,
            });

            await using var dispatcherConnection = new RabbitMqConnection(rabbitOptions, NullLogger<RabbitMqConnection>.Instance);
            var dispatcher = new OutboxDispatcher(
                dataSource, new RabbitMqEventPublisher(dispatcherConnection, NullLogger<RabbitMqEventPublisher>.Instance), new SystemClock(),
                Options.Create(new OutboxDispatcherOptions { PollInterval = TimeSpan.FromSeconds(2), BatchSize = 20 }),
                NullLogger<OutboxDispatcher>.Instance);

            await using var services = BuildServiceProvider(dataSource, fileStorage);
            await using var consumerConnection = new RabbitMqConnection(rabbitOptions, NullLogger<RabbitMqConnection>.Instance);
            var consumer = new AttachmentThumbnailConsumer(
                new RabbitMqEventConsumer(consumerConnection),
                services.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new AttachmentThumbnailConsumerOptions()),
                NullLogger<AttachmentThumbnailConsumer>.Instance);

            await dispatcher.StartAsync(CancellationToken.None);
            await consumer.StartAsync(CancellationToken.None);
            try
            {
                // Same reasoning as UnreadCounterEndToEndTests: give the consumer's durable queue
                // time to actually bind before the dispatcher's NOTIFY-driven publish can race ahead
                // of it.
                await Task.Delay(TimeSpan.FromSeconds(2));

                await using var confirmDb = new AgoChatDbContext(dbOptions);
                var confirmHandler = new ConfirmAttachmentHandler(
                    new AttachmentRepository(confirmDb),
                    new ConversationRepository(confirmDb),
                    fileStorage,
                    new PermissionChecker(confirmDb),
                    new EfOutboxWriter<AgoChatDbContext>(confirmDb),
                    new UuidV7Generator(),
                    new SystemClock());

                var confirmed = await confirmHandler.HandleAsVisitorAsync(
                    new ConfirmAttachmentAsVisitor(attachment.Id, visitorId), CancellationToken.None);
                Assert.True(confirmed.IsSuccess);

                await OutboxTestHelpers.WaitUntilAsync(
                    async () =>
                    {
                        await using var verify = new AgoChatDbContext(dbOptions);
                        var reloaded = await verify.Attachments.SingleAsync(a => a.Id == attachment.Id);
                        return reloaded.ThumbnailKey is not null;
                    },
                    TimeSpan.FromSeconds(15));
            }
            finally
            {
                await dispatcher.StopAsync(CancellationToken.None);
                await consumer.StopAsync(CancellationToken.None);
            }

            await using var final = new AgoChatDbContext(dbOptions);
            var result = await final.Attachments.SingleAsync(a => a.Id == attachment.Id);
            Assert.NotNull(result.ThumbnailKey);

            var thumbnailMetadata = await fileStorage.GetMetadataAsync(new ObjectKey(result.ThumbnailKey!), CancellationToken.None);
            Assert.NotNull(thumbnailMetadata);
            Assert.Equal("image/jpeg", thumbnailMetadata.ContentType);
        }
        finally
        {
            await postgres.DisposeAsync();
            await rabbitMq.DisposeAsync();
            await minio.DisposeAsync();
        }
    }

    private static ServiceProvider BuildServiceProvider(NpgsqlDataSource dataSource, IFileStorage fileStorage)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(dataSource);
        services.AddDbContext<AgoChatDbContext>((provider, options) =>
            options.UseNpgsql(provider.GetRequiredService<NpgsqlDataSource>()));
        services.AddScoped<IAttachmentRepository, AttachmentRepository>();
        services.AddSingleton(fileStorage);
        services.AddOptions<AttachmentThumbnailOptions>();
        services.AddScoped<AttachmentThumbnailGenerator>();
        return services.BuildServiceProvider();
    }

    private static byte[] CreateTestPngBytes(int width = 800, int height = 600)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
