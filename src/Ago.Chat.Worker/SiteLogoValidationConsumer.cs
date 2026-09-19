using System.Text.Json;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `25-160`: reacts to <see cref="SiteLogoValidationRequested"/> - the sibling of `5-04`'s
/// <see cref="AttachmentThumbnailConsumer"/>, the identical <c>Competing</c> subscription shape (exactly
/// one replica validates a given upload, not every replica).
/// </summary>
public sealed class SiteLogoValidationConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<SiteLogoValidationConsumerOptions> options,
    ILogger<SiteLogoValidationConsumer> logger) : BackgroundService
{
    // `5-11`: this consumer's own stable identity - AttachmentThumbnailConsumer.ConsumerName's own
    // remarks on why `internal`, not `private` (a future end-to-end test's own queue-name computation).
    internal const string ConsumerName = "site-logo-validation";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        return consumer.SubscribeAsync(
            nameof(SiteLogoValidationRequested), SubscriptionMode.Competing, ConsumerName, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<SiteLogoValidationRequested>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {nameof(SiteLogoValidationRequested)} payload for outbox message {envelope.MessageId}.");

            await using var scope = scopeFactory.CreateAsyncScope();
            var validator = scope.ServiceProvider.GetRequiredService<SiteLogoValidator>();
            await validator.ValidateAsync(
                new SiteId(contract.SiteId), contract.ObjectKey, contract.ContentType, cancellationToken);

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to validate a site logo for outbox message {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
