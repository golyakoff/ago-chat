using System.Text.Json;
using Ago.Chat.Application.UseCases.ResolveTeamMessageDelivery;
using Ago.Chat.Contracts;
using Ago.Chat.Domain;
using Ago.Platform.Abstractions;
using Microsoft.Extensions.Options;

namespace Ago.Chat.Worker;

/// <summary>
/// `23-32`: the team-chat sibling of <see cref="ConnectionFanoutConsumer"/> - reacts to
/// <see cref="TeamMessagePosted"/> by resolving every operator of the site and handing off to the
/// platform's fan-out path (<see cref="ResolveTeamMessageDeliveryTargetsHandler"/>). <c>Competing</c>,
/// the same shape and the same reason: exactly one <c>Worker</c> replica needs to resolve-and-publish
/// per message, not every replica.
///
/// A failure here is safe to retry freely, for the identical reason
/// <see cref="ConnectionFanoutConsumer"/>'s own remarks state: resolving participants and
/// re-publishing a fan-out has no idempotency ledger to violate, because the fan-out itself is
/// ephemeral and re-publishing it is exactly as harmless as the first publish (adr/0020).
/// </summary>
public sealed class TeamChatFanoutConsumer(
    IEventConsumer consumer,
    IServiceScopeFactory scopeFactory,
    IOptions<TeamChatFanoutConsumerOptions> options,
    ILogger<TeamChatFanoutConsumer> logger) : BackgroundService
{
    // `5-11`'s own stable-identity reasoning applies verbatim - see ConnectionFanoutConsumer.ConsumerName's
    // own remarks for why this is a named constant rather than left to default to the topic name.
    internal const string ConsumerName = "team-chat-fanout";

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryPolicy = new RetryPolicy(
            options.Value.MaxAttempts, options.Value.InitialBackoff, $"{ConsumerName}.dlq");

        return consumer.SubscribeAsync(
            nameof(TeamMessagePosted), SubscriptionMode.Competing, ConsumerName, retryPolicy, HandleAsync, stoppingToken);
    }

    private async Task HandleAsync(EventEnvelope envelope, IMessageContext context, CancellationToken cancellationToken)
    {
        try
        {
            var contract = JsonSerializer.Deserialize<TeamMessagePosted>(envelope.Payload)
                ?? throw new InvalidOperationException(
                    $"Could not deserialize {nameof(TeamMessagePosted)} payload for outbox message {envelope.MessageId}.");

            await using var scope = scopeFactory.CreateAsyncScope();
            var handler = scope.ServiceProvider.GetRequiredService<ResolveTeamMessageDeliveryTargetsHandler>();

            var command = new ResolveTeamMessageDeliveryTargets(
                new SiteId(contract.SiteId), contract.Sequence, contract.CorrelationId);

            var result = await handler.HandleAsync(command, cancellationToken);
            if (result.IsFailure)
            {
                // Should not happen in practice: TeamMessagePosted is only published after the
                // message's own transaction committed (adr/0005), so the row it names is already
                // durable by the time this consumer sees it - the identical reasoning
                // ConnectionFanoutConsumer's own remarks state for MessageAccepted.
                throw new InvalidOperationException(
                    $"{result.Error!.Value.Code}: {result.Error!.Value.Message}");
            }

            await context.AckAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to resolve team message delivery for {MessageId}.", envelope.MessageId);
            throw;
        }
    }
}
