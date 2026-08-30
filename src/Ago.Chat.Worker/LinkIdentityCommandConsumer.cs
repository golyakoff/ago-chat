using System.Text.Json;
using Ago.Chat.Application.UseCases.HandleLinkIdentityCommand;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `14-12`/`adr/0079`: a fifth consumer of <c>MessageAccepted</c>, alongside `2-05`'s
/// <see cref="UnreadCounterConsumer"/>, `3-02`'s <c>ConnectionFanoutConsumer</c>, `14-04`'s
/// <see cref="OfflineAutoReplyConsumer"/> and `20-07`'s <see cref="ModuleTaskConsumer"/> - the identical
/// shape (<c>Competing</c>, own consumer name, one DI scope per message, a skip is an ack not a nack)
/// each of those already establishes; this class does not repeat that reasoning, only its own
/// differences.
///
/// <para><b>Why this, and not a sixth branch bolted onto <see cref="ModuleTaskConsumer"/>.</b> The two
/// answer genuinely different questions over the same trigger - "does a module task want this message"
/// vs "does this message invoke Chat's own closed <c>/linkidentity</c> command" - and the reserved-word
/// registration guard (<see cref="ReservedChatCommands"/>/<c>EnableModuleForSiteHandler</c>) is what
/// keeps the two from ever needing a runtime precedence rule between them, exactly as
/// <see cref="HandleLinkIdentityCommandHandler"/>'s own remarks state. Two independent consumers on one
/// topic, reacting to unrelated facts, is this codebase's own established shape for exactly this
/// situation (<see cref="ModuleTaskConsumer"/>'s own remarks on coexisting with
/// <see cref="OfflineAutoReplyConsumer"/>).</para>
/// </summary>
public sealed class LinkIdentityCommandConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<LinkIdentityCommandConsumerOptions> options,
    ILogger<LinkIdentityCommandConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{HandleLinkIdentityCommandHandler.ConsumerName}.dlq");

        return consumer.SubscribeAsync(
            nameof(MessageAccepted), SubscriptionMode.Competing, HandleLinkIdentityCommandHandler.ConsumerName,
            retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<MessageAccepted>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {nameof(MessageAccepted)} payload for outbox message {envelope.MessageId}.");

            var authorKind = Enum.TryParse<MessageAuthorKind>(contract.AuthorKind, out var parsed)
                ? parsed
                : MessageAuthorKind.System;

            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<HandleLinkIdentityCommandHandler>();

            var command = new HandleLinkIdentityCommand(
                contract.MessageId,
                new SiteId(contract.SiteId),
                new ConversationId(contract.ConversationId),
                authorKind,
                contract.Sequence);

            var result = await handler.HandleAsync(command, cancellationToken);
            if (result.IsFailure)
            {
                throw new InvalidOperationException(
                    $"{result.Error!.Value.Code}: {result.Error!.Value.Message}");
            }

            if (result.Value == LinkIdentityCommandOutcome.RequestCreated)
            {
                logger.LogDebug(
                    "Created a pending channel link request in conversation {ConversationId}, triggered by message {MessageId}.",
                    contract.ConversationId, contract.MessageId);
            }

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to process {MessageId} for {Consumer}.",
                envelope.MessageId, HandleLinkIdentityCommandHandler.ConsumerName);
            throw;
        }
    }
}
