using System.Text.Json;
using Ago.Chat.Application.UseCases.RouteConversationToModule;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `20-07`: the fourth consumer of <c>MessageAccepted</c>, alongside `2-05`'s
/// <see cref="UnreadCounterConsumer"/>, `3-02`'s <c>ConnectionFanoutConsumer</c> and `14-04`'s
/// <see cref="OfflineAutoReplyConsumer"/> - the identical shape (<c>Competing</c>, own consumer name,
/// one DI scope per message, a skip is an ack not a nack) <see cref="OfflineAutoReplyConsumer"/>'s own
/// remarks describe in full; this class does not repeat that reasoning, only its own differences.
///
/// <para><b>Why this, and not a fifth handler bolted onto <c>OfflineAutoReplyConsumer</c>.</b> The two
/// consumers answer genuinely different questions ("is nobody online" vs "does a module task want this
/// message") over the same trigger, and `RouteConversationToModuleHandler`'s own loop guard already
/// treats a module-produced system message exactly like an auto-reply's own - both never re-trigger
/// themselves, and neither's own reply is mistaken for the other's trigger, because both act on
/// <see cref="MessageAuthorKind.Visitor"/> only. Two independent consumers on one topic is this
/// codebase's established shape for "two unrelated reactions to the same fact" - see how
/// <see cref="UnreadCounterConsumer"/> and <see cref="OfflineAutoReplyConsumer"/> already coexist.</para>
/// </summary>
public sealed class ModuleTaskConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<ModuleTaskConsumerOptions> options,
    ILogger<ModuleTaskConsumer> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{RouteConversationToModuleHandler.ConsumerName}.dlq");

        return consumer.SubscribeAsync(
            nameof(MessageAccepted), SubscriptionMode.Competing, RouteConversationToModuleHandler.ConsumerName,
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
            var handler = scope.ServiceProvider.GetRequiredService<RouteConversationToModuleHandler>();

            var command = new RouteConversationToModule(
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

            if (result.Value is RouteConversationToModuleOutcome.TaskStarted
                or RouteConversationToModuleOutcome.StepAdvanced or RouteConversationToModuleOutcome.TaskCompleted
                or RouteConversationToModuleOutcome.Escalated or RouteConversationToModuleOutcome.ModuleUnavailableAtTrigger)
            {
                logger.LogDebug(
                    "Module task routing outcome {Outcome} for conversation {ConversationId}, triggered by message {MessageId}.",
                    result.Value, contract.ConversationId, contract.MessageId);
            }

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to process {MessageId} for {Consumer}.",
                envelope.MessageId, RouteConversationToModuleHandler.ConsumerName);
            throw;
        }
    }
}
