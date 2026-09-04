using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `5-04`: reacts to `AttachmentConfirmed` - "a broker-driven job, so it is a natural second consumer
/// with a different scaling profile from the message consumer" (`file-storage.md`'s own words).
/// `Competing`, matching every other per-item Worker consumer (`ConnectionFanoutConsumer`,
/// `UnreadCounterConsumer`): exactly one replica thumbnails a given attachment, not every replica.
/// Non-image content types are filtered here, not by the event itself - `AttachmentConfirmed` fires
/// for every confirmed attachment regardless of type, since deciding "does this need a thumbnail" is
/// this consumer's own concern, not something worth encoding into the event's own existence.
/// </summary>
public sealed class AttachmentThumbnailConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<AttachmentThumbnailConsumerOptions> options,
    ILogger<AttachmentThumbnailConsumer> logger) : BackgroundService
{
    // `5-11`: this consumer's own stable identity - see `ConnectionFanoutConsumer`'s own remarks.
    // `internal`, not `private`, as of `15-20` - `AttachmentThumbnailEndToEndTests` (in-repo, covered
    // by this project's own `InternalsVisibleTo`, `AssemblyInfo.cs`) needs it to compute this
    // `Competing` subscription's exact queue name for `RabbitMqSubscriptionTestHelpers`'
    // `WaitForCompetingSubscriptionAsync`, the same way `ConnectionFanoutConsumer.ConsumerName`
    // already is for its own tests.
    internal const string ConsumerName = "attachment-thumbnail";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        return consumer.SubscribeAsync(
            nameof(AttachmentConfirmed), SubscriptionMode.Competing, ConsumerName, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<AttachmentConfirmed>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {nameof(AttachmentConfirmed)} payload for outbox message {envelope.MessageId}.");

            if (!contract.ContentType.StartsWith("image/", StringComparison.Ordinal))
            {
                await context.AckAsync(cancellationToken);
                return;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var generator = scope.ServiceProvider.GetRequiredService<AttachmentThumbnailGenerator>();
            await generator.GenerateAsync(new AttachmentId(contract.AttachmentId), contract.ObjectKey, cancellationToken);

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to generate a thumbnail for outbox message {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
