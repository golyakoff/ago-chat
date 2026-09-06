using System.Text.Json;
using Ago.Chat.Application.UseCases.ResolveTeamMessageRemovalDelivery;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `23-33`: the removal-fanout sibling of <see cref="TeamChatFanoutConsumer"/> - reacts to
/// <see cref="TeamMessageRemoved"/> by resolving every operator of the site and handing off to
/// <see cref="ResolveTeamMessageRemovalDeliveryTargetsHandler"/>, the same <c>Competing</c> shape and
/// the same reason: exactly one <c>Worker</c> replica needs to resolve-and-publish per removal, not
/// every replica.
///
/// <para>A separate <see cref="BackgroundService"/> rather than a second branch inside
/// <see cref="TeamChatFanoutConsumer"/> - the same one-consumer-per-event shape every other pair in
/// this project already takes (<see cref="ConnectionFanoutConsumer"/> versus
/// <see cref="ConversationAssignmentFanoutConsumer"/>, not one class branching on event type), so a
/// slow or failing removal fan-out can never starve the ordinary post fan-out's own competing-consumer
/// pool, and vice versa.</para>
///
/// <para>A failure here is safe to retry freely, the identical reason
/// <see cref="TeamChatFanoutConsumer"/>'s own remarks state: resolving participants and re-publishing
/// a fan-out has no idempotency ledger to violate, because the fan-out itself is ephemeral and
/// re-publishing it is exactly as harmless as the first publish (adr/0020).</para>
/// </summary>
public sealed class TeamMessageRemovedFanoutConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<TeamMessageRemovedFanoutConsumerOptions> options,
    ILogger<TeamMessageRemovedFanoutConsumer> logger) : BackgroundService
{
    // `5-11`'s own stable-identity reasoning applies verbatim - see ConnectionFanoutConsumer.ConsumerName's
    // own remarks for why this is a named constant rather than left to default to the topic name.
    internal const string ConsumerName = "team-message-removed-fanout";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        return consumer.SubscribeAsync(
            nameof(TeamMessageRemoved), SubscriptionMode.Competing, ConsumerName, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<TeamMessageRemoved>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {nameof(TeamMessageRemoved)} payload for outbox message {envelope.MessageId}.");

            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<ResolveTeamMessageRemovalDeliveryTargetsHandler>();

            var command = new ResolveTeamMessageRemovalDeliveryTargets(
                new SiteId(contract.SiteId), contract.Sequence, contract.CorrelationId);

            var result = await handler.HandleAsync(command, cancellationToken);
            if (result.IsFailure)
            {
                // Should not happen in practice: TeamMessageRemoved is only published after the
                // removal's own transaction committed (adr/0005), so the row it names is already
                // durable by the time this consumer sees it - the identical reasoning
                // TeamChatFanoutConsumer's own remarks state for TeamMessagePosted.
                throw new InvalidOperationException(
                    $"{result.Error!.Value.Code}: {result.Error!.Value.Message}");
            }

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to resolve team message removal delivery for {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
