using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `23-76`: reacts to `AttachmentConfirmed`, the identical event <see cref="AttachmentThumbnailConsumer"/>
/// already reacts to - a second, independent `Competing` subscription with its own
/// <see cref="ConsumerName"/>, not a change to that consumer. This is the standard fan-out shape this
/// codebase already uses whenever one event has more than one independent reaction (each
/// `Competing`-mode subscriber gets its own queue bound to the same topic, so both jobs see every
/// event; *within* one job's own queue, its replicas compete so exactly one of them handles a given
/// message) - "a natural second consumer with a different scaling profile from the message consumer"
/// is `file-storage.md`'s own phrase for the thumbnail consumer, and it applies unchanged here: dedup
/// has nothing to do with thumbnailing and no reason to share its failure/retry/scaling profile.
///
/// Unlike <see cref="AttachmentThumbnailConsumer"/>, this one does not filter by content type - dedup
/// applies to any allowed content type (images and PDFs alike), since a duplicate PDF invoice costs
/// exactly as much tenant storage as a duplicate photo.
/// </summary>
public sealed class AttachmentDeduplicationConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<AttachmentDeduplicationConsumerOptions> options,
    ILogger<AttachmentDeduplicationConsumer> logger) : BackgroundService
{
    // `23-76`'s own stable identity - see AttachmentThumbnailConsumer.ConsumerName's own remarks.
    internal const string ConsumerName = "attachment-dedup";

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

            await using var scope = scopeFactory.CreateAsyncScope();
            var deduplicator = scope.ServiceProvider.GetRequiredService<AttachmentDeduplicator>();
            await deduplicator.DeduplicateAsync(new AttachmentId(contract.AttachmentId), contract.ObjectKey, cancellationToken);

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to deduplicate attachment for outbox message {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
